[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','RunDevelopment','ForceBuildAndRun','BuildFrontend','TestFrontend','TestRust','RunRelease','SetVersion','BuildLauncherPackage','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Windows')][string]$Platform = 'Windows',
    [string]$GamePackageRoot = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force
$forbiddenRoot = 'D:\UnrealMap\CrossingVoid'

function Get-CrossingOutputRoot {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        throw '尚未配置 AxTools 工作区产物目录。'
    }
    return [IO.Path]::GetFullPath($OutputRoot)
}

function Get-CrossingLauncherOutputRoot {
    return Join-Path (Get-CrossingOutputRoot) 'Launcher'
}

function Test-CrossingPackage {
    param([string]$ExpectedVersion = '')
    $searchRoots = @((Get-CrossingLauncherOutputRoot), (Join-Path $root 'dist-launcher-update')) | Select-Object -Unique
    $manifestPath = $searchRoots | ForEach-Object { Join-Path $_ 'latest.json' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (!$manifestPath) { throw '缺少启动器 latest.json。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
        -not [string]::Equals([string]$manifest.version, $ExpectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "现有发布断点版本为 $($manifest.version)，当前需要 $ExpectedVersion。"
    }
    $platform = $manifest.platforms.'windows-x86_64'
    if ($null -eq $platform -or [string]::IsNullOrWhiteSpace([string]$platform.signature)) { throw 'latest.json 缺少 Windows 签名。' }
    $directory = Split-Path -Parent $manifestPath
    $installer = Get-ChildItem -LiteralPath $directory -Filter '*setup.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $installer) { throw "缺少启动器安装包：$directory" }
    $signature = "$($installer.FullName).sig"
    if (!(Test-Path -LiteralPath $signature -PathType Leaf)) { throw "缺少启动器签名：$signature" }
    Write-AxTaskEvent -Data ([ordered]@{ type='artifact'; kind='installer'; path=$installer.FullName })
    return [pscustomobject]@{ Manifest=$manifestPath; Installer=$installer.FullName }
}

function Test-CrossingPublishCheckpoint {
    try {
        Test-CrossingPackage -ExpectedVersion $Version | Out-Null
        return [pscustomobject]@{ IsValid = $true; Message = '' }
    }
    catch {
        return [pscustomobject]@{ IsValid = $false; Message = $_.Exception.Message }
    }
}

function Invoke-CrossingCommand {
    param([string]$File,[string[]]$Arguments,[string]$Name)
    $code=0; Invoke-AxExternalCommand $File $Arguments $root ([ref]$code); Assert-AxExternalSuccess $code $Name
}

function Invoke-CrossingDevelopment {
    param([switch]$Force)

    # 启动器是 Tauri 项目，开发版必须由 Tauri CLI 拉起：它会先启动 Vite 开发服务器，
    # 再让应用加载该地址，此时前端处于 DEV 模式，设置里的“开发者”页签才会出现。
    # 直接运行 src-tauri\target\debug\tauri-vue-launcher.exe 会加载已构建的前端，
    # 页面处于生产模式，开发页签不会出现（这也是它不适合当作开发启动方式的原因）。

    if (!(Test-Path -LiteralPath (Join-Path $root 'node_modules\.bin\tauri.cmd'))) {
        throw "前端依赖未安装：缺少 $root\node_modules\.bin\tauri.cmd。请先在项目目录执行 npm install。"
    }

    $nativeEnvironment = Initialize-AxMsvcEnvironment
    Write-AxTaskProgress -Stage 'msvc-environment' -Percent 5 -Message '已加载 MSVC 与 Windows SDK。' -Detail "MSVC=$($nativeEnvironment.MsvcVersion); SDK=$($nativeEnvironment.WindowsSdkVersion); linker=$($nativeEnvironment.LinkerPath)"

    if ($Force) {
        # 强制重编译只清理零境启动器自身 crate 的产物，依赖缓存保留，避免几十分钟的全量重编。
        $cargo = Resolve-AxCommand -Name 'cargo'
        Write-AxTaskProgress -Stage 'clean-development' -Percent 10 -Message '已选择强制重编译，正在清理零境启动器自身的编译产物（依赖缓存保留）。'
        Invoke-CrossingCommand $cargo @('clean','-p','tauri-vue-launcher','--manifest-path',(Join-Path $root 'src-tauri\Cargo.toml')) '清理零境启动器编译产物'
    }

    $npm = Resolve-AxCommand -Name 'npm.cmd'
    $forceMessage = if ($Force) { '（已清理应用产物）' } else { '' }
    Write-AxTaskProgress -Stage 'launch-development' -Percent 20 -Message "正在新窗口启动 Tauri 开发版$forceMessage：Vite 开发服务器 + 应用。"

    # 用独立控制台承载开发会话：AxTools 不占任务位，输出与热更新日志都在那个窗口里，
    # 关闭该窗口即结束开发会话。
    $developmentCommand = "`"$npm`" run tauri -- dev"
    Start-Process -FilePath 'cmd.exe' -ArgumentList @('/k', $developmentCommand) -WorkingDirectory $root

    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message 'Tauri 开发版已在新窗口启动；关闭该窗口即结束开发会话。'
}

function Get-CrossingVersionPath {
    return Join-Path $root 'Saved\Launcher\developer-version.json'
}

function Get-CrossingConfiguredVersion {
    $path = Get-CrossingVersionPath
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return '' }
    try {
        return ([string]((Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json).version)).Trim()
    }
    catch {
        throw "启动器版本设置文件无效：$path"
    }
}

function Test-CrossingVersionGreater {
    param([string]$Candidate, [string]$Current)

    $pattern = '^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$'
    $candidateMatch = [regex]::Match($Candidate, $pattern)
    if (!$candidateMatch.Success) {
        throw "启动器版本必须使用三段数字，例如 1.0.14 或 1.0.14-Beta：$Candidate"
    }
    if ([string]::IsNullOrWhiteSpace($Current)) { return $true }
    $currentMatch = [regex]::Match($Current, $pattern)
    if (!$currentMatch.Success) { return $true }

    $candidateParts = @([int64]$candidateMatch.Groups[1].Value, [int64]$candidateMatch.Groups[2].Value, [int64]$candidateMatch.Groups[3].Value)
    $candidateSuffix = $candidateMatch.Groups[4].Value
    $currentParts = @([int64]$currentMatch.Groups[1].Value, [int64]$currentMatch.Groups[2].Value, [int64]$currentMatch.Groups[3].Value)
    $currentSuffix = $currentMatch.Groups[4].Value
    for ($index = 0; $index -lt 3; $index++) {
        if ($candidateParts[$index] -ne $currentParts[$index]) { return $candidateParts[$index] -gt $currentParts[$index] }
    }
    return [string]::IsNullOrWhiteSpace($candidateSuffix) -and ![string]::IsNullOrWhiteSpace($currentSuffix)
}

function Set-CrossingVersion {
    param([string]$RequestedVersion)

    $current = Get-CrossingConfiguredVersion
    if ([string]::Equals($RequestedVersion, $current, [StringComparison]::OrdinalIgnoreCase)) {
        Write-AxTaskProgress -Stage 'version' -Percent 100 -Message "当前已是启动器版本 $current，无需重复保存。" -Detail (Get-CrossingVersionPath)
        return
    }
    if (!(Test-CrossingVersionGreater -Candidate $RequestedVersion -Current $current)) {
        throw "新版本号必须高于当前版本。当前版本：$current"
    }
    $path = Get-CrossingVersionPath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [ordered]@{ version = $RequestedVersion } | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    Write-AxTaskProgress -Stage 'version' -Percent 100 -Message "已保存启动器版本 $RequestedVersion。" -Detail $path
}

function Assert-CrossingVersionConfigured {
    param([string]$RequestedVersion)

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) { throw '请先填写版本号。' }
    $configured = Get-CrossingConfiguredVersion
    if (!([string]::Equals($configured, $RequestedVersion, [StringComparison]::OrdinalIgnoreCase))) {
        throw "项目保存的版本是 $configured，与 AX 当前版本 $RequestedVersion 不一致。请重新调整三段版本号后再试。"
    }
}

function Invoke-CrossingBuildPackage {
    $nativeEnvironment = Initialize-AxMsvcEnvironment
    Write-AxTaskProgress -Stage 'msvc-environment' -Percent 5 -Message '已加载 MSVC 与 Windows SDK。' -Detail "MSVC=$($nativeEnvironment.MsvcVersion); SDK=$($nativeEnvironment.WindowsSdkVersion)"
    $output = Get-CrossingLauncherOutputRoot
    $intermediate = Join-Path $root 'dist-launcher-update'
    & (Join-Path $root 'Scripts\Build-LauncherUpdaterPackage.ps1') -ProjectRoot $root -OutputDir $output -IntermediateOutputDir $intermediate
    if ($LASTEXITCODE -notin @(0,$null)) { throw "启动器包构建失败，退出码 $LASTEXITCODE。" }
}

function Invoke-CrossingPublish {
    param([switch]$SkipBuild,[switch]$AsDryRun)
    if (!$SkipBuild) { Initialize-AxMsvcEnvironment | Out-Null }
    $output = Get-CrossingLauncherOutputRoot
    $intermediate = Join-Path $root 'dist-launcher-update'
    & (Join-Path $root 'Scripts\Publish-LauncherGiteePackage.ps1') -ProjectRoot $root -ReleasePackageDir $output -IntermediateOutputDir $intermediate -SkipBuild:$SkipBuild -DryRun:$AsDryRun
    if ($LASTEXITCODE -notin @(0,$null)) { throw "启动器发布失败，退出码 $LASTEXITCODE。" }
}

function Assert-CrossingPublicVersion {
    param([string]$ExpectedVersion)
    $observed = ''
    $lastError = ''
    Write-AxTaskProgress -Stage 'public-version' -Percent 99 -Message "正在确认 PC 启动器公网版本 $ExpectedVersion..."
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $uri = "https://www.crossingvoid.top/api/toolbox-updates/tauri/crossingvoid-launcher-pc/windows/x86_64/0.0.0?t=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
            $response = Invoke-RestMethod -Uri $uri -Headers @{ 'Cache-Control'='no-cache'; 'User-Agent'='AxTools-PublishVerify/1.0' } -TimeoutSec 20
            $observed = ([string]$response.version).Trim()
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
    throw "PC 发布未通过公网版本核验。期望：$ExpectedVersion；$detail"
}

try {
    Assert-AxPathOutsideRoot -Path $ProjectRoot -ForbiddenRoot $forbiddenRoot -Message '拒绝访问虚幻项目目录。'
    Assert-AxPathOutsideRoot -Path $OutputRoot -ForbiddenRoot $forbiddenRoot -Message '拒绝把虚幻项目作为输出目录。'
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @('package.json','src-tauri\Cargo.toml','Scripts\Build-LauncherUpdaterPackage.ps1','Scripts\Publish-LauncherGiteePackage.ps1')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前启动器任务。'
    switch ($Action) {
        'CheckEnvironment' { Write-AxTaskStage -Stage 'environment' -Message '正在检查零境启动器 PC 环境...'; $npm = Resolve-AxCommand -Name 'npm.cmd'; $cargo = Resolve-AxCommand -Name 'cargo'; $nativeEnvironment = Initialize-AxMsvcEnvironment; Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message '零境启动器 PC 环境检查通过。' -Detail "npm=$npm; cargo=$cargo; MSVC=$($nativeEnvironment.MsvcVersion); SDK=$($nativeEnvironment.WindowsSdkVersion)" }
        'RunDevelopment' { Invoke-CrossingDevelopment }
        'ForceBuildAndRun' { Invoke-CrossingDevelopment -Force }
        'RunRelease' { Start-AxExecutable $ReleaseExecutable }
        'BuildFrontend' { $npm = Resolve-AxCommand -Name 'npm.cmd'; Invoke-CrossingCommand $npm @('run','build') '前端构建' }
        'TestFrontend' { $npm = Resolve-AxCommand -Name 'npm.cmd'; Invoke-CrossingCommand $npm @('test') '前端测试' }
        'TestRust' { Initialize-AxMsvcEnvironment | Out-Null; $cargo = Resolve-AxCommand -Name 'cargo'; Invoke-CrossingCommand $cargo @('test','--manifest-path',(Join-Path $root 'src-tauri\Cargo.toml')) 'Rust 测试' }
        'SetVersion' { Set-CrossingVersion -RequestedVersion $Version }
        'BuildLauncherPackage' { Assert-CrossingVersionConfigured -RequestedVersion $Version; Invoke-CrossingBuildPackage; Test-CrossingPackage | Out-Null }
        'ValidatePackage' { Test-CrossingPackage | Out-Null }
        'UploadDryRun' { Test-CrossingPackage | Out-Null; Invoke-CrossingPublish -SkipBuild -AsDryRun }
        'Upload' { Test-CrossingPackage -ExpectedVersion $Version | Out-Null; Set-AxTaskCancellationMode -Mode 'stop' -Message '上传阶段可停止；再次执行会续传已完成附件。'; Invoke-CrossingPublish -SkipBuild; Assert-CrossingPublicVersion -ExpectedVersion $Version }
        'PublishDryRun' { Assert-CrossingVersionConfigured -RequestedVersion $Version; Invoke-CrossingPublish -AsDryRun }
        'Publish' {
            Assert-CrossingVersionConfigured -RequestedVersion $Version
            Set-AxTaskCancellationMode -Mode 'stop' -Message '构建和上传阶段可停止；再次执行会从有效断点继续。'
            $checkpoint = Test-CrossingPublishCheckpoint
            if ($checkpoint.IsValid) {
                Write-AxTaskProgress -Stage 'checkpoint-resume' -Percent 58 -Message "检测到 $Version 有效发布断点，跳过重新构建。" -Detail (Get-CrossingLauncherOutputRoot)
                Invoke-CrossingPublish -SkipBuild
            }
            else {
                Write-AxTaskEvent -Data ([ordered]@{
                    type = 'warning'
                    code = 'checkpoint-invalid'
                    message = "现有发布断点不可复用，将重新构建：$($checkpoint.Message)"
                })
                Write-AxTaskProgress -Stage 'checkpoint-rebuild' -Percent 2 -Message '没有可用发布断点，正在重新构建完整发布包。'
                Invoke-CrossingPublish
            }
            Assert-CrossingPublicVersion -ExpectedVersion $Version
        }
    }
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "零境启动器 PC $Action 已完成。"
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "零境启动器 PC $Action 完成。"
    exit 0
}
catch {
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
