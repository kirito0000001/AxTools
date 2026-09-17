[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RemotePath = 'C:\UEWatchdog\state\status.json',
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Invoke-AxSshCapture {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $ssh = (Get-Command ssh.exe -CommandType Application -ErrorAction Stop |
        Select-Object -First 1).Source
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ssh
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @(
        '-o', 'BatchMode=yes',
        '-o', "ConnectTimeout=$TimeoutSeconds",
        $Target,
        $Command)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(($TimeoutSeconds + 10) * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "读取服务器状态超时：$Target"
        }

        return [pscustomobject]@{
            ExitCode       = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError  = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

$remoteScript = @"
`$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new(`$false)
`$path = '$RemotePath'
if (Test-Path -LiteralPath `$path -PathType Leaf) {
    [Console]::Out.Write((Get-Content -LiteralPath `$path -Raw -Encoding UTF8))
}
else {
    [Console]::Error.Write("status file not found: `$path")
    exit 2
}
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))

$result = Invoke-AxSshCapture -Target $SshTarget `
    -Command "powershell -NoProfile -EncodedCommand $encoded" `
    -TimeoutSeconds $TimeoutSeconds

if ($result.ExitCode -ne 0) {
    $detail = $result.StandardError.Trim()
    if ([string]::IsNullOrWhiteSpace($detail)) { $detail = 'ssh 未返回错误详情。' }
    throw "读取服务器状态失败，退出码 $($result.ExitCode)：$detail"
}

$json = $result.StandardOutput.Trim()
if ([string]::IsNullOrWhiteSpace($json)) {
    throw "服务器状态文件为空：$RemotePath"
}

try {
    [void]($json | ConvertFrom-Json)
}
catch {
    throw "服务器状态内容不是有效 JSON：$($_.Exception.Message)"
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
