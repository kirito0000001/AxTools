[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 远端动作脚本变更后运行一次即可；日常调用只走 Invoke-AliyunServerAction.ps1。
$source = Join-Path $PSScriptRoot 'Remote-AliyunServerAction.ps1'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "缺少远端动作脚本：$source"
}

$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$scp = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'
foreach ($tool in @($ssh, $scp)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "缺少 Windows OpenSSH 工具：$tool"
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) "ax-remote-action-$([guid]::NewGuid().ToString('N')).ps1"
try {
    # 远端以 Windows PowerShell 5.1 执行 -File，必须带 UTF-8 BOM 才能正确解析。
    $body = Get-Content -Raw -Encoding UTF8 -LiteralPath $source
    [IO.File]::WriteAllText($staging, $body, [Text.UTF8Encoding]::new($true))

    $target = "${SshTarget}:/C:/UEWatchdog/Remote-AliyunServerAction.ps1"
    $stdout = (& $scp -o BatchMode=yes -o ConnectTimeout=15 $staging $target 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "部署远端动作脚本失败，scp 退出码 $LASTEXITCODE：$stdout"
    }

    Write-Host "已部署：$target"
    exit 0
}
finally {
    Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
}
