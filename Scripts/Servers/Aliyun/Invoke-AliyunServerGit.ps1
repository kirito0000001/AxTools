[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget,
    [Parameter(Mandatory)]
    [ValidateSet('bootstrap', 'status', 'apply', 'rollback')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProgramKey,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$CommitMessage = '',
    [int]$HealthTimeoutSeconds = 180,
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
    try {
        [void]$process.Start()
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try { $process.Kill($true) } catch { }
            throw "服务器 Git 动作超时（$Action）。"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdoutTask.GetAwaiter().GetResult()
            StdErr   = $stderrTask.GetAwaiter().GetResult()
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
