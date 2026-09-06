[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$downloadScript = Join-Path $repoRoot 'Scripts\Release\Download-ManagedToolRelease.ps1'
if (!(Test-Path -LiteralPath $downloadScript -PathType Leaf)) {
    throw "缺少正式版下载脚本：$downloadScript"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'AxTools-ReleaseDownload-' + [Guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
try {
    $fakeBin = Join-Path $testRoot 'bin'
    $target = Join-Path $testRoot 'DemoTool-Release'
    New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fakeBin 'gh.cmd') -Encoding ASCII -Value @'
@echo off
if /I "%2"=="view" echo {"tagName":"v1.2.3","isPrerelease":false}& exit /b 0
if /I "%2"=="download" goto download
exit /b 2
:download
:loop
if "%1"=="" exit /b 3
if /I "%1"=="--dir" goto found
shift
goto loop
:found
mkdir "%2" 2>nul
echo package>"%2\DemoTool-v1.2.3.zip"
exit /b 0
'@
    $env:PATH = "$fakeBin;$originalPath"

    $lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $downloadScript `
        -RepositoryName 'owner/DemoTool' `
        -DestinationPath $target `
        -ExpectedDirectoryName 'DemoTool-Release' 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "正式版下载脚本失败：$($lines -join [Environment]::NewLine)"
    }
    if (!(Test-Path -LiteralPath (Join-Path $target 'DemoTool-v1.2.3.zip') -PathType Leaf)) {
        throw '正式版附件没有下载到目标目录。'
    }
    if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
        throw '正式版下载脚本缺少成功 result。'
    }
    Write-Output 'PASS: 正式版下载脚本契约通过。'
}
finally {
    $env:PATH = $originalPath
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
exit 0
