[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1'
if (!(Test-Path -LiteralPath $entry -PathType Leaf)) {
    throw "FantasyProject-PC 适配入口不存在：$entry"
}

$source = Get-Content -LiteralPath $entry -Raw
$forbiddenScriptName = 'Publish-' + 'GamePackage.ps1'
if ($source.Contains($forbiddenScriptName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'FantasyProject-PC 适配器包含游戏包发布脚本引用。'
}
foreach ($requiredText in @(
    'launcher.config.json',
    'Build-LauncherUpdaterPackage.ps1',
    'Publish-LauncherGiteePackage.ps1',
    "'windows-x86_64'",
    'IntermediateOutputDir')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "FantasyProject-PC 适配器缺少契约文本：$requiredText"
    }
}

$projectRoot = 'D:\UnrealMap\FantasyProject-PC'
$lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
    -Action CheckEnvironment -ProjectRoot $projectRoot 2>&1 |
    ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) {
    throw "FantasyProject-PC 环境检查失败：$($lines -join [Environment]::NewLine)"
}
if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
    throw 'FantasyProject-PC 环境检查缺少成功 result。'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-FantasyProjectPc-' + [guid]::NewGuid().ToString('N'))
$originalStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT
try {
    $env:AXTOOLS_BUILD_STATE_ROOT = Join-Path $temporaryRoot 'build-state'
    foreach ($marker in @(
        'package.json',
        'launcher.config.json',
        'src-tauri\Cargo.toml',
        'Scripts\Build-LauncherUpdaterPackage.ps1',
        'Scripts\Publish-LauncherGiteePackage.ps1')) {
        $markerPath = Join-Path $temporaryRoot $marker
        New-Item -ItemType Directory -Path (Split-Path -Parent $markerPath) -Force | Out-Null
        Set-Content -LiteralPath $markerPath -Value '' -Encoding utf8NoBOM
    }
    $fakeBin = Join-Path $temporaryRoot 'fake-bin'
    New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fakeBin 'npm.cmd') -Value '@echo ARGS=%*' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fakeBin 'cargo.cmd') -Value '@exit /b 0' -Encoding ASCII
    $originalPath = $env:PATH
    $env:PATH = "$fakeBin;$originalPath"
    try {
        $developmentLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
            -Action RunDevelopment -ProjectRoot $temporaryRoot -DevelopmentExecutable "$env:WINDIR\System32\where.exe" 2>&1 |
            ForEach-Object { $_.ToString() })
    }
    finally {
        $env:PATH = $originalPath
    }
    if ($LASTEXITCODE -ne 0 -or
        !($developmentLines | Where-Object { $_ -eq 'ARGS=run tauri -- build --debug --no-bundle' })) {
        throw "FantasyProject-PC 开发启动未使用一次性 Debug 构建：$($developmentLines -join [Environment]::NewLine)"
    }

    $packageRoot = Join-Path $temporaryRoot 'dist-launcher-update'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $installerName = 'FantasyProject-PC_1.0.0_x64-setup.exe'
    $installerPath = Join-Path $packageRoot $installerName
    Set-Content -LiteralPath $installerPath -Value 'installer' -Encoding utf8NoBOM
    Set-Content -LiteralPath "$installerPath.sig" -Value 'signature' -Encoding utf8NoBOM
    [ordered]@{
        version = '1.0.0'
        platforms = [ordered]@{
            'windows-x86_64' = [ordered]@{
                signature = 'test-signature'
                url = "https://example.invalid/$installerName"
            }
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'latest.json') -Encoding utf8NoBOM

    $validationLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action ValidatePackage -ProjectRoot $temporaryRoot -OutputRoot (Join-Path $temporaryRoot 'empty-output') 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0 -or !($validationLines | Where-Object { $_ -match '"status":"success"' })) {
        throw "FantasyProject-PC 合成包校验失败：$($validationLines -join [Environment]::NewLine)"
    }

    Remove-Item -LiteralPath "$installerPath.sig" -Force
    $missingSignatureLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action ValidatePackage -ProjectRoot $temporaryRoot -OutputRoot (Join-Path $temporaryRoot 'empty-output') 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -eq 0 -or !($missingSignatureLines | Where-Object { $_ -match '缺少启动器签名文件' })) {
        throw 'FantasyProject-PC 包校验没有拒绝缺失的 .sig 文件。'
    }
}
finally {
    $env:AXTOOLS_BUILD_STATE_ROOT = $originalStateRoot
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output 'PASS: FantasyProject-PC 适配器契约通过。'
exit 0
