[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','TestFrontend','BuildFrontend','BuildAndroidDebug','BuildAndroidAndInstall','BuildAndroidRelease','ListAndroidDevices','StopAndroidApp','RunRelease','PublishDryRun','Publish','BuildGameChunks','UploadGameChunks','PublishGamePackage')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Android')][string]$Platform = 'Android',
    [string]$GamePackageRoot = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

function Invoke-AndroidCommand {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [string]$Name)

    $exitCode = 0
    Invoke-AxExternalCommand $File $Arguments $WorkingDirectory ([ref]$exitCode)
    Assert-AxExternalSuccess $exitCode $Name
}

function Initialize-AndroidBuildEnvironment {
    $script:npm = Resolve-AxCommand -Name 'npm.cmd'
    $script:npx = Resolve-AxCommand -Name 'npx.cmd'
    $script:java = Resolve-AxCommand -Name 'java.exe'

    $javaVersion = @(& $script:java -version 2>&1) -join [Environment]::NewLine
    if ($javaVersion -notmatch '(?i)version\s+"21(?:\.|"|\s)') {
        throw "零境启动器 Android 需要 JDK 21，当前 java 输出：$javaVersion"
    }
    if ([string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        throw 'AxTools 没有为当前任务注入 JAVA_HOME。'
    }
    if ([string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        throw 'AxTools 没有为当前任务注入 ANDROID_SDK_ROOT。'
    }
}

function Assert-AndroidEnvironment {
    Initialize-AndroidBuildEnvironment
    $script:adb = Resolve-AxCommand -Name 'adb.exe'
}

function Build-AndroidDebugApk {
    Write-AxTaskProgress -Stage 'frontend' -Percent 10 -Message '正在构建前端...'
    Invoke-AndroidCommand $script:npm @('run', 'build') $root '前端构建'
    Write-AxTaskProgress -Stage 'capacitor' -Percent 35 -Message '正在同步 Capacitor Android 工程...'
    Invoke-AndroidCommand $script:npx @('cap', 'sync', 'android') $root 'Capacitor 同步'
    Write-AxTaskProgress -Stage 'gradle' -Percent 55 -Message '正在构建 Debug APK...'
    Invoke-AndroidCommand (Join-Path $root 'android\gradlew.bat') `
        @('assembleDebug', '--console=plain', '--no-daemon') `
        (Join-Path $root 'android') `
        'Gradle Debug APK 构建'
    $apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
    if (!(Test-Path -LiteralPath $apk -PathType Leaf)) { throw "Debug APK 构建完成但没有找到产物：$apk" }
    Write-AxTaskEvent -Data ([ordered]@{ type = 'artifact'; kind = 'apk'; path = $apk })
    return $apk
}

function Build-AndroidReleaseApk {
    Write-AxTaskProgress -Stage 'frontend' -Percent 10 -Message '正在构建前端...'
    Invoke-AndroidCommand $script:npm @('run', 'build') $root '前端构建'
    Write-AxTaskProgress -Stage 'capacitor' -Percent 35 -Message '正在同步 Capacitor Android 工程...'
    Invoke-AndroidCommand $script:npx @('cap', 'sync', 'android') $root 'Capacitor 同步'
    Write-AxTaskProgress -Stage 'gradle' -Percent 55 -Message '正在构建 Release APK...'
    Invoke-AndroidCommand (Join-Path $root 'android\gradlew.bat') `
        @('assembleRelease', '--console=plain', '--no-daemon') `
        (Join-Path $root 'android') `
        'Gradle Release APK 构建'
    $apk = Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk'
    if (!(Test-Path -LiteralPath $apk -PathType Leaf)) { throw "Release APK 构建完成但没有找到产物：$apk" }
    Write-AxTaskEvent -Data ([ordered]@{ type = 'artifact'; kind = 'apk'; path = $apk })
    return $apk
}

function Invoke-AndroidGamePublishing {
    param([ValidateSet('Package','Upload','Publish')][string]$Mode)
    if ([string]::IsNullOrWhiteSpace($GamePackageRoot)) { throw '请先选择 Android 打包目录。' }
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) { throw '请先配置游戏分片输出目录。' }
    $publishingScript = Join-Path (Split-Path -Parent $scriptsRoot) 'Scripts\Publishing\CrossingVoid\Publish-CrossingVoidGame.ps1'
    if (!(Test-Path -LiteralPath $publishingScript -PathType Leaf)) { throw "缺少 AxTools 游戏发布脚本：$publishingScript" }
    & $publishingScript `
        -Mode $Mode `
        -Platform 'Android' `
        -Channel $(if ($Channel -eq 'beta') { 'Test' } else { 'Stable' }) `
        -GameDirectory $GamePackageRoot `
        -OutputRoot $OutputRoot `
        -ReleaseVersion $Version `
        -ReleaseTitle "零境交错 Android $Version" `
        -ReleaseNotes $ReleaseNotes
    if ($LASTEXITCODE -ne 0) { throw "Android 游戏分包发布失败，退出码 $LASTEXITCODE。" }
}

function Assert-AndroidPublicVersion {
    param([string]$ExpectedVersion)
    $observed = ''
    $lastError = ''
    Write-AxTaskProgress -Stage 'public-version' -Percent 99 -Message "正在确认 Android 启动器公网版本 $ExpectedVersion..."
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $uri = "https://www.crossingvoid.top/manifests/launcher/android-latest.json?t=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
            $response = Invoke-RestMethod -Uri $uri -Headers @{ 'Cache-Control'='no-cache'; 'User-Agent'='AxTools-PublishVerify/1.0' } -TimeoutSec 20
            $observed = ([string]$response.versionName).Trim()
            if ([string]::Equals($observed, $ExpectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
                return
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        if ($attempt -lt 5) { Start-Sleep -Seconds 2 }
    }
    $detail = if (![string]::IsNullOrWhiteSpace($lastError)) { $lastError } else { "公网当前版本：$observed" }
    throw "Android 发布未通过公网版本核验。期望：$ExpectedVersion；$detail"
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'package.json',
        'android\gradlew.bat',
        'android\gradle\wrapper\gradle-wrapper.properties',
        'Scripts\Publish-AndroidLauncher.ps1')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 Android 启动器任务。'
    switch ($Action) {
        'CheckEnvironment' {
            Write-AxTaskStage -Stage 'environment' -Message '正在检查零境启动器 Android 环境...'
            Assert-AndroidEnvironment
            Write-AxTaskProgress -Stage 'environment' -Percent 100 `
                -Message '零境启动器 Android 环境检查通过。' `
                -Detail "JAVA_HOME=$env:JAVA_HOME; ANDROID_SDK_ROOT=$env:ANDROID_SDK_ROOT; npm=$script:npm; adb=$script:adb"
        }
        'TestFrontend' {
            $script:npm = Resolve-AxCommand -Name 'npm.cmd'
            Invoke-AndroidCommand $script:npm @('test') $root '前端测试'
        }
        'BuildFrontend' {
            $script:npm = Resolve-AxCommand -Name 'npm.cmd'
            Invoke-AndroidCommand $script:npm @('run', 'build') $root '前端构建'
        }
        'BuildAndroidDebug' {
            Initialize-AndroidBuildEnvironment
            Build-AndroidDebugApk | Out-Null
        }
        'BuildAndroidAndInstall' {
            Initialize-AndroidBuildEnvironment
            $apk = Build-AndroidDebugApk
            Write-AxTaskProgress -Stage 'install' -Percent 85 -Message '正在安装 Debug APK 到已连接设备...'
            $script:adb = Resolve-AxCommand -Name 'adb.exe'
            Invoke-AndroidCommand $script:adb @('install', '-r', $apk) $root 'ADB 安装 Debug APK'
        }
        'BuildAndroidRelease' {
            Initialize-AndroidBuildEnvironment
            Build-AndroidReleaseApk | Out-Null
        }
        'ListAndroidDevices' {
            $script:adb = Resolve-AxCommand -Name 'adb.exe'
            Invoke-AndroidCommand $script:adb @('devices', '-l') $root '查询 ADB 设备'
        }
        'StopAndroidApp' {
            $script:adb = Resolve-AxCommand -Name 'adb.exe'
            Invoke-AndroidCommand $script:adb @('shell', 'am', 'force-stop', 'com.TFAC.CorssingVoid') $root '停止设备端启动器'
        }
        'RunRelease' {
            $script:adb = Resolve-AxCommand -Name 'adb.exe'
            Invoke-AndroidCommand $script:adb `
                @('shell', 'monkey', '-p', 'com.TFAC.CorssingVoid', '-c', 'android.intent.category.LAUNCHER', '1') `
                $root `
                '启动已安装 Android 版本'
        }
        { $_ -in @('PublishDryRun', 'Publish') } {
            if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
                throw '尚未配置 AxTools 工作区产物目录。'
            }
            Set-AxTaskCancellationMode -Mode 'stop' -Message '构建和上传阶段可停止；再次执行会从有效断点继续。'
            $publishArguments = @{
                VersionName = $Version
                ProjectRoot = $root
                OutputDir = [IO.Path]::GetFullPath($OutputRoot)
            }
            if ($Action -eq 'PublishDryRun' -or $DryRun) {
                $publishArguments.DryRun = $true
            }
            & (Join-Path $root 'Scripts\Publish-AndroidLauncher.ps1') @publishArguments
            if ($LASTEXITCODE -notin @(0, $null)) {
                throw "Android 启动器发布失败，退出码 $LASTEXITCODE。"
            }
            if ($Action -eq 'Publish' -and !$DryRun) {
                Assert-AndroidPublicVersion -ExpectedVersion $Version
            }
        }
        'BuildGameChunks' { Invoke-AndroidGamePublishing -Mode Package }
        'UploadGameChunks' { Invoke-AndroidGamePublishing -Mode Upload }
        'PublishGamePackage' { Invoke-AndroidGamePublishing -Mode Publish }
    }

    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "零境启动器 Android $Action 已完成。"
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "零境启动器 Android $Action 完成。"
    exit 0
}
catch {
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
