[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1'
$originalVisualStudioRoot = $env:AXTOOLS_VISUAL_STUDIO_ROOT
$originalMsvcVersion = $env:AXTOOLS_MSVC_VERSION
$env:AXTOOLS_VISUAL_STUDIO_ROOT = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
$env:AXTOOLS_MSVC_VERSION = '14.44.35207'
if (!(Test-Path -LiteralPath $entry -PathType Leaf)) { throw "Crossing 适配入口不存在：$entry" }
$source = Get-Content -LiteralPath $entry -Raw -Encoding UTF8
$publisherPath = 'D:\UnrealMap\CrossingVoidinitiator-PC\Scripts\Publish-LauncherUpdaterPackage.ps1'
$publisherSource = if (Test-Path -LiteralPath $publisherPath -PathType Leaf) {
    Get-Content -LiteralPath $publisherPath -Raw -Encoding UTF8
} else { '' }
foreach ($requiredText in @('Initialize-AxMsvcEnvironment', 'linker=$($nativeEnvironment.LinkerPath)', 'Windows SDK')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Crossing 适配入口缺少 MSVC 链接器兼容逻辑：$requiredText"
    }
}
foreach ($requiredText in @('BuildGameChunks', 'UploadGameChunks', 'PublishGamePackage', 'Publish-CrossingVoidGame.ps1', "'Windows'")) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Crossing PC 适配入口缺少游戏发布合同：$requiredText"
    }
}
foreach ($requiredText in @('Assert-CrossingPublicVersion', 'api/toolbox-updates/tauri/crossingvoid-launcher-pc', '期望：$ExpectedVersion')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Crossing PC 适配入口缺少发布后公网版本核验：$requiredText"
    }
}
foreach ($requiredText in @('$checkpoint = Test-CrossingPublishCheckpoint', 'if ($checkpoint.IsValid)', 'checkpoint-invalid')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Crossing PC 适配入口缺少无歧义断点判断：$requiredText"
    }
}
if ($source.Contains('if (Test-CrossingPublishCheckpoint)', [StringComparison]::Ordinal)) {
    throw 'Crossing PC 适配入口仍会把协议输出误判为有效断点。'
}
if ($source.Contains('Write-Output $_.Exception.Message', [StringComparison]::Ordinal)) {
    throw 'Crossing PC 适配器不应把最终异常重复写成普通 Info 日志。'
}
foreach ($requiredText in @(
    'for ($attempt = 1; $attempt -le 3; $attempt++)',
    '-o BatchMode=yes -o ConnectTimeout=10',
    '2> $stderrPath',
    'Remove-Item -LiteralPath $stderrPath',
    '$serverSnapshotAvailable = $false',
    'server-snapshot-unavailable',
    'cleanup-skipped',
    '-and $serverSnapshotAvailable')) {
    if (!$publisherSource.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "PC 发布脚本缺少服务器快照容错合同：$requiredText"
    }
}
foreach ($requiredText in @(
    "Join-Path (Get-CrossingOutputRoot) 'Launcher'",
    "Join-Path (Get-CrossingOutputRoot) 'GamePackages'",
    '$output = Get-CrossingGameOutputRoot')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Crossing PC 适配入口没有隔离启动器与游戏产物：$requiredText"
    }
}
if ([regex]::Matches($source, '\$output = Get-CrossingLauncherOutputRoot').Count -lt 2) {
    throw 'Crossing PC 构建与发布没有统一使用独立 Launcher 目录。'
}

$lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
    -Action CheckEnvironment -ProjectRoot 'D:\UnrealMap\CrossingVoidinitiator-PC' 2>&1 |
    ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) { throw "Crossing 环境检查失败：$($lines -join [Environment]::NewLine)" }
if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
    throw 'Crossing 环境检查缺少成功 result。'
}

$forbiddenLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
    -Action CheckEnvironment -ProjectRoot 'D:\UnrealMap\CrossingVoid' 2>&1 |
    ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -eq 0) { throw 'Crossing 适配器没有拒绝虚幻项目路径。' }
if (!($forbiddenLines | Where-Object { $_ -match '虚幻项目' })) {
    throw 'Crossing 危险路径失败信息不明确。'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("AxTools-CrossingVoidPcAdapter-" + [Guid]::NewGuid().ToString('N'))
$originalStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT
try {
    $env:AXTOOLS_BUILD_STATE_ROOT = Join-Path $testRoot 'build-state'
    $fakeBin = Join-Path $testRoot 'bin'
    $projectRoot = Join-Path $testRoot 'project'
    New-Item -ItemType Directory -Path $fakeBin,(Join-Path $projectRoot 'src-tauri'),(Join-Path $projectRoot 'Scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Value '{}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $projectRoot 'src-tauri\Cargo.toml') -Value '[package]' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $projectRoot 'Scripts\Build-LauncherUpdaterPackage.ps1') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $projectRoot 'Scripts\Publish-LauncherGiteePackage.ps1') -Value '' -Encoding UTF8
    New-Item -ItemType Directory -Path (Join-Path $projectRoot 'Saved\Launcher') -Force | Out-Null
    '{"version":"1.0.13"}' | Set-Content -LiteralPath (Join-Path $projectRoot 'Saved\Launcher\developer-version.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fakeBin 'npm.cmd') -Value '@echo ARGS=%*' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fakeBin 'cargo.cmd') -Value '@exit /b 0' -Encoding ASCII

    $originalPath = $env:PATH
    $env:PATH = "$fakeBin;$originalPath"
    try {
        $argumentLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
            -Action RunDevelopment -ProjectRoot $projectRoot -DevelopmentExecutable "$env:WINDIR\System32\where.exe" 2>&1 |
            ForEach-Object { $_.ToString() })
    }
    finally {
        $env:PATH = $originalPath
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Crossing 开发启动参数合同执行失败：$($argumentLines -join [Environment]::NewLine)"
    }
    if (!($argumentLines | Where-Object { $_ -eq 'ARGS=run tauri -- build --debug --no-bundle' })) {
        throw "Crossing 开发启动没有完整传递 npm 参数：$($argumentLines -join [Environment]::NewLine)"
    }

    $versionLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action SetVersion -ProjectRoot $projectRoot -Version '1.0.14' 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "Crossing 保存新版本设置失败：$($versionLines -join [Environment]::NewLine)"
    }
    $savedVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'Saved\Launcher\developer-version.json') -Raw | ConvertFrom-Json).version
    if ($savedVersion -ne '1.0.14') {
        throw "Crossing 保存的新版本不正确：$savedVersion"
    }

    $sameVersionLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action SetVersion -ProjectRoot $projectRoot -Version '1.0.14' 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "Crossing 重复保存当前版本不应失败：$($sameVersionLines -join [Environment]::NewLine)"
    }
    if (!($sameVersionLines | Where-Object { $_ -match '当前已是启动器版本 1\.0\.14' })) {
        throw 'Crossing 重复保存当前版本缺少明确提示。'
    }
}
finally {
    $env:AXTOOLS_VISUAL_STUDIO_ROOT = $originalVisualStudioRoot
    $env:AXTOOLS_MSVC_VERSION = $originalMsvcVersion
    $env:AXTOOLS_BUILD_STATE_ROOT = $originalStateRoot
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'PASS: CrossingVoidinitiator-PC 适配器契约通过。'
exit 0
