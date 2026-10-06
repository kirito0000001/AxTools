[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget,
    [Parameter(Mandatory)]
    [ValidateSet('bootstrap', 'status', 'apply', 'rollback', 'recover')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProgramKey,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$CommitMessage = '',
    # 与远端脚本一致：现在这套产物要 ~183 秒才监听端口（服务端上的分片下载器在重试 404）。
    [int]$HealthTimeoutSeconds = 420,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 远端 Git 动作脚本由 Deploy-AliyunServerGit.ps1 一次性部署，这里只传短参数，
# 既避免 Windows 命令行长度上限，也避免每次多一跳 scp。
$remoteScriptPath = 'C:/UEWatchdog/Remote-AliyunServerGit.ps1'

function Resolve-AxOpenSshExecutable {
    param([Parameter(Mandatory)][string]$Name)

    # 明确优先 Windows 自带 OpenSSH：Git 自带的 MSYS 版本会改写 / 开头的参数。
    foreach ($candidate in @(
        (Join-Path $env:SystemRoot "System32\OpenSSH\$Name"),
        (Join-Path ${env:ProgramFiles} "OpenSSH\$Name"))) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        throw "缺少命令：$Name"
    }
    return $command.Source
}

function ConvertTo-AxSingleQuotedLiteral {
    param([AllowEmptyString()][string]$Value)
    # 远端以单引号包裹；顺带把内嵌单引号按 PowerShell 规则翻倍。
    return "'" + ($Value -replace "'", "''") + "'"
}

function Invoke-AxRemoteProcess {
    param(
        [Parameter(Mandatory)][string]$ExecutableName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Resolve-AxOpenSshExecutable -Name $ExecutableName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $payloadLines = [System.Collections.Generic.List[string]]::new()
    $errorLines = [System.Collections.Generic.List[string]]::new()
    try {
        [void]$process.Start()
        $process.StandardInput.Close()

        # ssh 的 stdout 里混着两种东西：运行中不断吐出的进度事件（::axtools 行）和最后
        # 那份结果 JSON。逐行读才能让进度条实时动 —— 一次性 ReadToEnd 要等 ssh 结束，
        # 那正好是"拉取更新整段时间里界面没有任何反馈"的原因。
        # 注意：这里必须用"主线程轮询 ReadLineAsync"的写法，不能把读取丢给
        # Task.Run([Action]{...})：PowerShell 的脚本块换线程执行拿不到外层变量，
        # 读取任务会当场死掉，ssh 的管道没人读，远端一写满 64 KB 就永远卡住
        # （2026-09-19 实测：远端明明已经切换完成，本机却一直等不到返回）。
        $stdoutTask = $process.StandardOutput.ReadLineAsync()
        $stderrTask = $process.StandardError.ReadLineAsync()
        $deadline = (Get-Date).AddMilliseconds($TimeoutMilliseconds)
        $timedOut = $false

        while ($true) {
            if ($null -ne $stdoutTask -and $stdoutTask.Wait(120)) {
                $line = $stdoutTask.Result
                if ($null -eq $line) {
                    $stdoutTask = $null
                }
                else {
                    if ($line.StartsWith('::axtools ', [StringComparison]::Ordinal)) {
                        [Console]::Out.WriteLine($line)
                        [Console]::Out.Flush()
                    }
                    else {
                        $payloadLines.Add($line)
                    }
                    $stdoutTask = $process.StandardOutput.ReadLineAsync()
                }
            }

            if ($null -ne $stderrTask -and $stderrTask.Wait(0)) {
                $line = $stderrTask.Result
                if ($null -eq $line) {
                    $stderrTask = $null
                }
                else {
                    $errorLines.Add($line)
                    $stderrTask = $process.StandardError.ReadLineAsync()
                }
            }

            # 两个流都读到 EOF 才算读完（进程退出但管道还有尾巴的情况也要收干净）。
            if ($null -eq $stdoutTask -and $null -eq $stderrTask) { break }
            if ((Get-Date) -gt $deadline) {
                $timedOut = $true
                break
            }
        }

        if (-not $process.HasExited -and -not $process.WaitForExit(2000)) {
            try { $process.Kill($true) } catch { }
        }
        if ($timedOut) {
            throw "服务器 Git 动作超时（$Action）。"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = ($payloadLines -join "`n")
            StdErr   = ($errorLines -join "`n")
        }
    }
    finally {
        $process.Dispose()
    }
}

# EncodedCommand：实测 -File 方式在 ssh 下会挂住。
$parts = [System.Collections.Generic.List[string]]::new()
[void]$parts.Add("& '$remoteScriptPath'")
[void]$parts.Add("-Action $(ConvertTo-AxSingleQuotedLiteral -Value $Action)")
[void]$parts.Add("-ProgramKey $(ConvertTo-AxSingleQuotedLiteral -Value $ProgramKey)")
[void]$parts.Add("-CommitMessage $(ConvertTo-AxSingleQuotedLiteral -Value $CommitMessage)")
[void]$parts.Add("-HealthTimeoutSeconds $HealthTimeoutSeconds")
$remoteBody = ($parts -join ' ')
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteBody))
$remoteCommand = "powershell -NoProfile -EncodedCommand $encoded"

$run = Invoke-AxRemoteProcess -ExecutableName 'ssh.exe' -Arguments @(
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=15',
    $SshTarget,
    $remoteCommand
) -TimeoutMilliseconds (($TimeoutSeconds + 30) * 1000)

$json = $run.StdOut.Trim()
if ([string]::IsNullOrWhiteSpace($json)) {
    $detail = $run.StdErr.Trim()
    if ([string]::IsNullOrWhiteSpace($detail)) { $detail = 'ssh 未返回内容。' }
    throw "服务器 Git 动作失败：$Action（退出码 $($run.ExitCode)）：$detail"
}

try {
    [void]($json | ConvertFrom-Json)
}
catch {
    throw "服务器 Git 动作返回的内容不是有效 JSON：$($_.Exception.Message)。stderr：$($run.StdErr.Trim())"
}

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    $json,
    [Text.UTF8Encoding]::new($false))

exit 0
