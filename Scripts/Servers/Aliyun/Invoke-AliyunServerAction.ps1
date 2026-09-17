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

$remoteScriptSource = Join-Path $PSScriptRoot 'Remote-AliyunServerAction.ps1'
# Windows OpenSSH 会吞掉远程命令里的反斜杠，因此远端路径一律用正斜杠。
$remoteScriptPath = 'C:/UEWatchdog/Remote-AliyunServerAction.ps1'
$remoteScpTarget = "${SshTarget}:/C:/UEWatchdog/Remote-AliyunServerAction.ps1"

if (-not (Test-Path -LiteralPath $remoteScriptSource -PathType Leaf)) {
    throw "缺少服务器动作脚本：$remoteScriptSource"
}

function Invoke-AxRemoteProcess {
    param(
        [Parameter(Mandatory)][string]$ExecutableName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )

    $executable = (Get-Command $ExecutableName -CommandType Application -ErrorAction Stop |
        Select-Object -First 1).Source
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
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

$staging = Join-Path ([IO.Path]::GetTempPath()) "ax-remote-action-$([guid]::NewGuid().ToString('N')).ps1"
try {
    # 远端脚本体积超过 Windows 命令行上限，先落盘再执行，调用时只传短参数。
    $body = Get-Content -Raw -Encoding UTF8 -LiteralPath $remoteScriptSource
    [IO.File]::WriteAllText($staging, $body, [Text.UTF8Encoding]::new($true))

    $copy = Invoke-AxRemoteProcess -ExecutableName 'scp.exe' -Arguments @(
        '-o', 'BatchMode=yes',
        '-o', 'ConnectTimeout=15',
        $staging,
        $remoteScpTarget
    ) -TimeoutMilliseconds 60000
    if ($copy.ExitCode -ne 0) {
        throw "部署服务器动作脚本失败，scp 退出码 $($copy.ExitCode)：$($copy.StdErr.Trim())"
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    [void]$parts.Add("powershell -NoProfile -ExecutionPolicy Bypass -File $remoteScriptPath")
    [void]$parts.Add("-Action $Action")
    if (-not [string]::IsNullOrWhiteSpace($InstanceName)) {
        [void]$parts.Add("-InstanceName $InstanceName")
    }
    [void]$parts.Add("-TailLines $TailLines")
    $remoteCommand = ($parts -join ' ')

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

    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        $json,
        [Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
}

exit 0
