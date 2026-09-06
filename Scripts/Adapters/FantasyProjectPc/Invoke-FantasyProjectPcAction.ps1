[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','RunDevelopment','ForceBuildAndRun','BuildFrontend','TestFrontend','TestRust','RunRelease','BuildLauncherPackage','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

function Get-FantasyProjectPcOutputRoot {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        throw '尚未配置 AxTools 工作区产物目录。'
    }
    return [IO.Path]::GetFullPath($OutputRoot)
}

function Invoke-FantasyProjectPcCommand {
    param([string]$File, [string[]]$Arguments, [string]$Name)

    $exitCode = 0
    Invoke-AxExternalCommand $File $Arguments $root ([ref]$exitCode)
    Assert-AxExternalSuccess $exitCode $Name
}

function Invoke-FantasyProjectPcDevelopment {
    param([switch]$Force)

    $executable = if (![string]::IsNullOrWhiteSpace($DevelopmentExecutable)) {
        [IO.Path]::GetFullPath($DevelopmentExecutable)
    }
    else {
        Join-Path $root 'src-tauri\target\debug\fantasyproject-pc-launcher.exe'
    }
    $stateParameters = @{
        ToolKey = 'FantasyProjectPc'
        ProjectRoot = $root
        Profile = 'Tauri'
        Configuration = 'DebugX64'
        ExecutablePath = $executable
    }
    if (!$Force -and (Test-AxDevelopmentBuildCurrent @stateParameters)) {
        Write-AxTaskProgress -Stage 'launch-development' -Percent 90 -Message '源码未变化，开发产物有效，正在直接启动。' -Detail $executable
        Start-AxExecutable $executable
        return
    }

    $nativeEnvironment = Initialize-AxMsvcEnvironment
    Write-AxTaskProgress -Stage 'msvc-environment' -Percent 5 `
        -Message '已加载 MSVC 与 Windows SDK。' `
        -Detail "MSVC=$($nativeEnvironment.MsvcVersion); SDK=$($nativeEnvironment.WindowsSdkVersion)"
    $buildMessage = if ($Force) { '已选择强制重新编译。' } else { '检测到源码或配置变化，正在增量编译。' }
    Write-AxTaskProgress -Stage 'build-development' -Percent 10 -Message $buildMessage
    $npm = Resolve-AxCommand -Name 'npm.cmd'
    Invoke-FantasyProjectPcCommand $npm @('run', 'tauri', '--', 'build', '--debug', '--no-bundle') 'FantasyProject-PC Tauri Debug 开发版构建'
    Write-AxDevelopmentBuildState @stateParameters
    Write-AxTaskProgress -Stage 'launch-development' -Percent 90 -Message '编译完成，正在启动 FantasyProject-PC Tauri Debug 开发版...' -Detail $executable
    Start-AxExecutable $executable
}

function Get-FantasyProjectPcPackage {
    $searchRoots = @(
        (Get-FantasyProjectPcOutputRoot),
        (Join-Path $root 'dist-launcher-update')) | Select-Object -Unique
    $manifestPath = $searchRoots |
        ForEach-Object { Join-Path $_ 'latest.json' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (!$manifestPath) {
        throw '缺少 FantasyProject-PC 启动器 latest.json。'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        throw 'latest.json 缺少启动器版本。'
    }

    $platform = $manifest.platforms.'windows-x86_64'
    if ($null -eq $platform) {
        throw 'latest.json 缺少 windows-x86_64 平台。'
    }
    if ([string]::IsNullOrWhiteSpace([string]$platform.signature)) {
        throw 'latest.json 缺少 Windows 启动器签名。'
    }
    if ([string]::IsNullOrWhiteSpace([string]$platform.url)) {
        throw 'latest.json 缺少 Windows 启动器下载地址。'
    }

    try {
        $installerName = [IO.Path]::GetFileName(([uri][string]$platform.url).AbsolutePath)
        $installerName = [uri]::UnescapeDataString($installerName)
    }
    catch {
        throw 'latest.json 的 Windows 启动器下载地址无效。'
    }
    if ([string]::IsNullOrWhiteSpace($installerName) -or
        ![string]::Equals([IO.Path]::GetExtension($installerName), '.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'latest.json 的 Windows 下载地址未指向 EXE 安装包。'
    }

    $directory = Split-Path -Parent $manifestPath
    $installerPath = Join-Path $directory $installerName
    if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "缺少启动器安装包：$installerPath"
    }
    $signaturePath = "$installerPath.sig"
    if (!(Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw "缺少启动器签名文件：$signaturePath"
    }

    Write-AxTaskEvent -Data ([ordered]@{
        type = 'artifact'
        kind = 'installer'
        path = $installerPath
    })
    return [pscustomobject]@{
        Manifest = $manifestPath
        Installer = $installerPath
        Signature = $signaturePath
        Version = [string]$manifest.version
    }
}

function Invoke-FantasyProjectPcBuildPackage {
    Initialize-AxMsvcEnvironment | Out-Null
    $output = Get-FantasyProjectPcOutputRoot
    $intermediate = Join-Path $root 'dist-launcher-update'
    & (Join-Path $root 'Scripts\Build-LauncherUpdaterPackage.ps1') `
        -ProjectRoot $root `
        -OutputDir $output `
        -IntermediateOutputDir $intermediate
    if ($LASTEXITCODE -notin @(0, $null)) {
        throw "FantasyProject-PC 启动器包构建失败，退出码 $LASTEXITCODE。"
    }
}

function Invoke-FantasyProjectPcPublish {
    param([switch]$SkipBuild, [switch]$AsDryRun)

    if (!$SkipBuild) { Initialize-AxMsvcEnvironment | Out-Null }
    $output = Get-FantasyProjectPcOutputRoot
    $intermediate = Join-Path $root 'dist-launcher-update'
    & (Join-Path $root 'Scripts\Publish-LauncherGiteePackage.ps1') `
        -ProjectRoot $root `
        -ReleasePackageDir $output `
        -IntermediateOutputDir $intermediate `
        -SkipBuild:$SkipBuild `
        -DryRun:$AsDryRun
    if ($LASTEXITCODE -notin @(0, $null)) {
        throw "FantasyProject-PC 启动器发布失败，退出码 $LASTEXITCODE。"
    }
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'package.json',
        'launcher.config.json',
        'src-tauri\Cargo.toml',
        'Scripts\Build-LauncherUpdaterPackage.ps1',
        'Scripts\Publish-LauncherGiteePackage.ps1')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 FantasyProject-PC 任务。'
    switch ($Action) {
        'CheckEnvironment' {
            Write-AxTaskStage -Stage 'environment' -Message '正在检查 FantasyProject-PC 环境...'
            $npm = Resolve-AxCommand -Name 'npm.cmd'
            $nativeEnvironment = Initialize-AxMsvcEnvironment
            $cargo = Resolve-AxCommand -Name 'cargo'
            Write-AxTaskProgress -Stage 'environment' -Percent 100 `
                -Message 'FantasyProject-PC 环境检查通过。' `
                -Detail "npm=$npm; cargo=$cargo; MSVC=$($nativeEnvironment.MsvcVersion); SDK=$($nativeEnvironment.WindowsSdkVersion)"
        }
        'RunDevelopment' {
            Invoke-FantasyProjectPcDevelopment
        }
        'ForceBuildAndRun' {
            Invoke-FantasyProjectPcDevelopment -Force
        }
        'BuildFrontend' {
            $npm = Resolve-AxCommand -Name 'npm.cmd'
            Invoke-FantasyProjectPcCommand $npm @('run', 'build') '前端构建'
        }
        'TestFrontend' {
            $npm = Resolve-AxCommand -Name 'npm.cmd'
            Invoke-FantasyProjectPcCommand $npm @('test') '前端测试'
        }
        'TestRust' {
            Initialize-AxMsvcEnvironment | Out-Null
            $cargo = Resolve-AxCommand -Name 'cargo'
            Invoke-FantasyProjectPcCommand $cargo @(
                'test',
                '--manifest-path',
                (Join-Path $root 'src-tauri\Cargo.toml')) 'Rust 测试'
        }
        'RunRelease' { Start-AxExecutable $ReleaseExecutable }
        'BuildLauncherPackage' {
            Invoke-FantasyProjectPcBuildPackage
            Get-FantasyProjectPcPackage | Out-Null
        }
        'ValidatePackage' { Get-FantasyProjectPcPackage | Out-Null }
        'UploadDryRun' {
            Get-FantasyProjectPcPackage | Out-Null
            Invoke-FantasyProjectPcPublish -SkipBuild -AsDryRun
        }
        'Upload' {
            Get-FantasyProjectPcPackage | Out-Null
            Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交 FantasyProject-PC 发布，不可停止。'
            Invoke-FantasyProjectPcPublish -SkipBuild
        }
        'PublishDryRun' { Invoke-FantasyProjectPcPublish -AsDryRun }
        'Publish' {
            Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交 FantasyProject-PC 发布，不可停止。'
            Invoke-FantasyProjectPcPublish
        }
    }

    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "FantasyProject-PC $Action 已完成。"
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "FantasyProject-PC $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
