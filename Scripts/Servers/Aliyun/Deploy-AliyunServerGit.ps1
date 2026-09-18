[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SshTarget
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 远端 Git 动作脚本变更后运行一次即可；日常调用只走 Invoke-AliyunServerGit.ps1。
$source = Join-Path $PSScriptRoot 'Remote-AliyunServerGit.ps1'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "缺少远端 Git 脚本：$source"
}

$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$scp = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'
foreach ($tool in @($ssh, $scp)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "缺少 Windows OpenSSH 工具：$tool"
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) "ax-remote-git-$([guid]::NewGuid().ToString('N')).ps1"
try {
    # 远端以 Windows PowerShell 5.1 执行，脚本带 UTF-8 BOM 才能被正确解析。
    $body = Get-Content -Raw -Encoding UTF8 -LiteralPath $source
    [IO.File]::WriteAllText($staging, $body, [Text.UTF8Encoding]::new($true))

    $target = "${SshTarget}:/C:/UEWatchdog/Remote-AliyunServerGit.ps1"
    $stdout = (& $scp -o BatchMode=yes -o ConnectTimeout=15 $staging $target 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "部署远端 Git 脚本失败，scp 退出码 $LASTEXITCODE：$stdout"
    }

    Write-Host "已部署：$target"

    # 解析验证：远端能把未知动作识别出来，就说明脚本可被 5.1 正确读取并执行。
    $probeBody = "& 'C:/UEWatchdog/Remote-AliyunServerGit.ps1' -Action 'unsupported-probe' -ProgramKey 'narutobp'"
    $probeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probeBody))
    $probeCommand = "powershell -NoProfile -EncodedCommand $probeEncoded"
    $probe = (& $ssh -o BatchMode=yes -o ConnectTimeout=15 $SshTarget $probeCommand 2>&1 | Out-String).Trim()
    if ($probe -notmatch 'unsupported action') {
        throw "远端 Git 脚本自检未通过：$probe"
    }
    Write-Host '远端脚本自检通过。'
    exit 0
}
finally {
    Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
}
