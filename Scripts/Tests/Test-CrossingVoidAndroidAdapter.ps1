[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1'
if (!(Test-Path -LiteralPath $entry -PathType Leaf)) {
    throw "零境启动器 Android 适配入口不存在：$entry"
}

$source = Get-Content -LiteralPath $entry -Raw
foreach ($requiredText in @(
    "'BuildAndroidDebug'",
    "'BuildAndroidAndInstall'",
    "'BuildAndroidRelease'",
    "'ListAndroidDevices'",
    "'StopAndroidApp'",
    'npm.cmd',
    'npx.cmd',
    'cap',
    'sync',
    'assembleDebug',
    'assembleRelease',
    "'install', '-r'",
    'force-stop',
    'JDK 21',
    'Publish-AndroidLauncher.ps1',
    'ANDROID_SDK_ROOT',
    'JAVA_HOME')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "零境启动器 Android 适配器缺少契约文本：$requiredText"
    }
}
# 游戏本体发布已经搬到「零境交错：空界幻境」那个工具里（平台只是参数），启动器这边不该再有游戏分包入口。
foreach ($forbiddenText in @('BuildGameChunks', 'UploadGameChunks', 'PublishGamePackage', 'Publish-CrossingVoidGame.ps1')) {
    if ($source.Contains($forbiddenText, [StringComparison]::Ordinal)) {
        throw "零境启动器 Android 适配器仍残留游戏分包入口：$forbiddenText"
    }
}
foreach ($requiredText in @('Assert-AndroidPublicVersion', 'manifests/launcher/android-latest.json', '期望：$ExpectedVersion')) {
    if (!$source.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "零境启动器 Android 适配入口缺少发布后公网版本核验：$requiredText"
    }
}
if ($source.Contains('Write-Output $_.Exception.Message', [StringComparison]::Ordinal)) {
    throw 'Android 适配器不应把最终异常重复写成普通 Info 日志。'
}
foreach ($forbiddenText in @('Get-AndroidVersionCode', 'VersionCode =')) {
    if ($source.Contains($forbiddenText, [StringComparison]::Ordinal)) {
        throw "Android 启动器普通发布不应计算或传递 VersionCode：$forbiddenText"
    }
}

$projectScript = 'D:\UnrealMap\CrossingVoidinitiator-Android\Scripts\Publish-AndroidLauncher.ps1'
if (Test-Path -LiteralPath $projectScript -PathType Leaf) {
    $publishSource = Get-Content -LiteralPath $projectScript -Raw
    foreach ($requiredText in @('JAVA_HOME', 'ANDROID_SDK_ROOT')) {
        if (!$publishSource.Contains($requiredText, [StringComparison]::Ordinal)) {
            throw "Android 发布脚本没有接受任务环境：$requiredText"
        }
    }
}

Write-Output 'PASS: 零境启动器 Android 适配器契约通过。'
exit 0
