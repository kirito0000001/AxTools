[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget,
    [Parameter(Mandatory)][string]$Action,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$InstanceName = '',
    [int]$TailLines = 200,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 远端动作脚本由 Deploy-AliyunServerAction.ps1 一次性部署，这里只传短参数，
# 既避免 Windows 命令行长度上限，也避免每次多一跳 scp。
$remoteScriptPath = 'C:/UEWatchdog/Remote-AliyunServerAction.ps1'

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
            throw "服务器动作超时（$Action）。"
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

# 与状态读取脚本保持一致：用 EncodedCommand，实测 -File 方式在 ssh 下会挂住。
$parts = [System.Collections.Generic.List[string]]::new()
[void]$parts.Add("& '$remoteScriptPath'")
[void]$parts.Add("-Action '$Action'")
if (-not [string]::IsNullOrWhiteSpace($InstanceName)) {
    [void]$parts.Add("-InstanceName '$InstanceName'")
}
[void]$parts.Add("-TailLines $TailLines")
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
    throw "服务器动作失败：$Action（退出码 $($run.ExitCode)）：$detail"
}

try {
    [void]($json | ConvertFrom-Json)
}
catch {
    throw "服务器动作返回的内容不是有效 JSON：$($_.Exception.Message)。stderr：$($run.StdErr.Trim())"
}

# 日志内容改由 scp 取回后本地读取：服务端只回传文件位置，
# 避免在服务端读取正在被看门狗写入的日志文件（实测会卡住）。
$resultObject = $json | ConvertFrom-Json
if ($Action -eq 'logs' -and $null -ne $resultObject.data) {
    $lines = @()
    $remotePath = ''
    if ($resultObject.data.PSObject.Properties['path']) {
        $remotePath = [string]$resultObject.data.path
    }

    if (-not [string]::IsNullOrWhiteSpace($remotePath)) {
        $suffix = [guid]::NewGuid().ToString('N')
        $localLog = Join-Path ([IO.Path]::GetTempPath()) "ax-log-$suffix.log"
        $scpError = Join-Path ([IO.Path]::GetTempPath()) "ax-log-$suffix.err"
        try {
            $scpSource = "${SshTarget}:" + $remotePath.Replace('\', '/')
            $scp = Resolve-AxOpenSshExecutable -Name 'scp.exe'
            & $scp -o BatchMode=yes -o ConnectTimeout=15 $scpSource $localLog 2>$scpError | Out-Null
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $localLog -PathType Leaf)) {
                $lines = @(Get-Content -LiteralPath $localLog -Tail $TailLines -Encoding UTF8 -ErrorAction SilentlyContinue)
            }
            else {
                $detail = (Get-Content -Raw -LiteralPath $scpError -ErrorAction SilentlyContinue)
                throw "读取日志文件失败：$remotePath（scp 退出码 $LASTEXITCODE）。$detail"
            }
        }
        finally {
            Remove-Item -LiteralPath $localLog -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $scpError -Force -ErrorAction SilentlyContinue
        }
    }

    $resultObject.data | Add-Member -NotePropertyName 'lines' -NotePropertyValue $lines -Force
    $json = ($resultObject | ConvertTo-Json -Depth 8 -Compress)
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
