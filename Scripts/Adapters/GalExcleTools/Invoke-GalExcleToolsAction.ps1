[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','BuildAndRun','ForceBuildAndRun','Build','SourceHealthCheck','RunRelease','PackageX64','ValidatePackage')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

$appName = 'TFAC剧情箱-轮椅版'

function Assert-GalExcleBuildOutput {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        throw '尚未配置剧情工具箱开发版程序路径。'
    }

    $executable = [IO.Path]::GetFullPath($ExecutablePath)
    $outputDirectory = Split-Path -Parent $executable
    foreach ($path in @(
        $executable,
        (Join-Path $outputDirectory "$appName.pri"),
        (Join-Path $outputDirectory 'App.xbf'),
        (Join-Path $outputDirectory 'MainWindow.xbf')
    )) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "剧情工具箱编译产物不完整，缺少：$path"
        }
    }

    return $executable
}

function Test-GalExcleRestoreCache {
    param([Parameter(Mandatory)][string]$Root)

    $projectPath = Join-Path $Root 'GalExcleTools.csproj'
    $assetsPath = Join-Path $Root 'obj\project.assets.json'
    if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $assetsPath).LastWriteTimeUtc -lt
        (Get-Item -LiteralPath $projectPath).LastWriteTimeUtc) { return $false }

    try {
        $assets = Get-Content -Raw -Encoding UTF8 -LiteralPath $assetsPath |
            ConvertFrom-Json -AsHashtable
        $packageFolders = @($assets.packageFolders.Keys)
        if ($packageFolders.Count -eq 0) { return $false }

        foreach ($entry in $assets.libraries.GetEnumerator()) {
            if ($entry.Value.type -ne 'package') { continue }
            $packagePath = [string]$entry.Value.path
            $available = $packageFolders | Where-Object {
                Test-Path -LiteralPath (Join-Path $_ $packagePath) -PathType Container
            } | Select-Object -First 1
            if ($null -eq $available) { return $false }
        }

        return $true
    }
    catch {
        return $false
    }
}

function Get-NewestGalExclePackage {
    param([Parameter(Mandatory)][string]$PackageOutputRoot)

    if ([string]::IsNullOrWhiteSpace($PackageOutputRoot)) {
        throw '尚未配置剧情工具箱打包输出目录。'
    }

    $fullOutputRoot = [IO.Path]::GetFullPath($PackageOutputRoot)
    if (!(Test-Path -LiteralPath $fullOutputRoot -PathType Container)) {
        throw "打包输出目录不存在：$fullOutputRoot"
    }

    $prefix = "$appName" + 'V'
    $packages = foreach ($directory in Get-ChildItem -LiteralPath $fullOutputRoot -Directory) {
        if (!$directory.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $parsedVersion = $null
        if ([Version]::TryParse($directory.Name.Substring($prefix.Length), [ref]$parsedVersion)) {
            [pscustomobject]@{ Directory = $directory.FullName; Version = $parsedVersion }
        }
    }

    $package = $packages | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw "未找到有效的剧情工具箱版本目录：$fullOutputRoot\${prefix}*"
    }

    return $package.Directory
}

function Assert-GalExclePackage {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $programRoot = Join-Path $PackageRoot $appName
    foreach ($path in @(
        (Join-Path $PackageRoot "$appName.lnk"),
        (Join-Path $programRoot "$appName.exe"),
        (Join-Path $programRoot "$appName.pri"),
        (Join-Path $programRoot 'App.xbf'),
        (Join-Path $programRoot 'MainWindow.xbf'),
        (Join-Path $programRoot "$appName.runtimeconfig.json"),
        (Join-Path $programRoot "$appName.deps.json")
    )) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "剧情工具箱打包产物不完整，缺少：$path"
        }
    }

    Write-AxTaskEvent -Data ([ordered]@{ type = 'artifact'; kind = 'package'; path = $PackageRoot })
    return $PackageRoot
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'GalExcleTools.csproj',
        'GalExcleTools.sln',
        'Scripts\Test-SourceHealth.ps1',
        'Scripts\Package-App.ps1'
    )
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前剧情工具箱任务。'
    if ($Action -eq 'CheckEnvironment') {
        Write-AxTaskStage -Stage 'environment' -Message '正在检查剧情工具箱环境...'
        $dotnet = Resolve-AxCommand -Name 'dotnet'
        Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message '剧情工具箱环境检查通过。' -Detail $dotnet
    }
    elseif ($Action -eq 'RunRelease') {
        Start-AxExecutable -ExecutablePath $ReleaseExecutable
        Write-AxTaskProgress -Stage 'launch' -Percent 100 -Message '剧情工具箱已启动。' -Detail $ReleaseExecutable
    }
    elseif ($Action -in @('Build','BuildAndRun','ForceBuildAndRun')) {
        $projectPath = Join-Path $root 'GalExcleTools.csproj'
        $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($DevelopmentExecutable))
        $resourcePaths = @(
            (Join-Path $outputDirectory "$appName.pri"),
            (Join-Path $outputDirectory 'App.xbf'),
            (Join-Path $outputDirectory 'MainWindow.xbf'))
        $stateParameters = @{
            ToolKey = 'GalExcleTools'
            ProjectRoot = $root
            Profile = 'DotNet'
            Configuration = 'DebugX64'
            ExecutablePath = $DevelopmentExecutable
            RequiredResourcePaths = $resourcePaths
        }
        if ($Action -eq 'BuildAndRun' -and (Test-AxDevelopmentBuildCurrent @stateParameters)) {
            Write-AxTaskProgress -Stage 'launch' -Percent 90 -Message '源码未变化，开发产物有效，正在直接启动。' -Detail $DevelopmentExecutable
            Start-AxExecutable -ExecutablePath $DevelopmentExecutable
            Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "剧情工具箱 $Action 完成。"
            exit 0
        }

        $buildMessage = if ($Action -eq 'ForceBuildAndRun') { '已选择强制重新编译。' } else { '检测到源码或配置变化，正在增量编译。' }
        Write-AxTaskProgress -Stage 'build' -Percent 5 -Message $buildMessage
        $dotnet = Resolve-AxCommand -Name 'dotnet'
        Write-AxTaskProgress -Stage 'dependencies' -Percent 10 -Message '正在检查剧情工具箱依赖缓存...'
        $buildProperties = @(
            '--runtime', 'win-x64',
            '-p:Platform=x64',
            '-p:RuntimeIdentifiers=win-x64',
            '-p:WindowsPackageType=None',
            '-p:WindowsAppSDKSelfContained=true'
        )
        if (!(Test-GalExcleRestoreCache -Root $root)) {
            Write-AxTaskProgress -Stage 'restore' -Percent 20 -Message '依赖缓存需要更新，正在恢复 win-x64 依赖...'
            $restoreArguments = @('restore', $projectPath) + $buildProperties
            $restoreExitCode = 0
            Invoke-AxExternalCommand -FilePath $dotnet -ArgumentList $restoreArguments -WorkingDirectory $root -ExitCode ([ref]$restoreExitCode)
            Assert-AxExternalSuccess -ExitCode $restoreExitCode -Operation '剧情工具箱依赖恢复'
        }
        else {
            Write-AxTaskProgress -Stage 'dependencies' -Percent 20 -Message '依赖缓存有效，将跳过联网恢复。'
        }

        Write-AxTaskProgress -Stage 'build' -Percent 35 -Message '正在增量编译剧情工具箱 Debug x64...'
        $arguments = @(
            'build',
            $projectPath,
            '--configuration', 'Debug',
            '--no-restore'
        ) + $buildProperties
        $exitCode = 0
        Invoke-AxExternalCommand -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $root -ExitCode ([ref]$exitCode)
        Assert-AxExternalSuccess -ExitCode $exitCode -Operation '剧情工具箱编译'
        Write-AxTaskProgress -Stage 'validate' -Percent 90 -Message '正在校验剧情工具箱编译产物...'
        $executable = Assert-GalExcleBuildOutput -ExecutablePath $DevelopmentExecutable
        $stateParameters.ExecutablePath = $executable
        Write-AxDevelopmentBuildState @stateParameters
        Write-AxTaskProgress -Stage 'build' -Percent 100 -Message '剧情工具箱编译及资源校验通过。' -Detail $executable
        if ($Action -in @('BuildAndRun','ForceBuildAndRun')) {
            Start-AxExecutable -ExecutablePath $executable
        }
    }
    elseif ($Action -eq 'SourceHealthCheck') {
        Write-AxTaskStage -Stage 'health' -Message '正在执行剧情工具箱源码健康检查...'
        & (Join-Path $root 'Scripts\Test-SourceHealth.ps1')
        Write-AxTaskProgress -Stage 'health' -Percent 100 -Message '剧情工具箱源码健康检查通过。'
    }
    elseif ($Action -eq 'PackageX64') {
        if ([string]::IsNullOrWhiteSpace($OutputRoot)) { throw '尚未配置剧情工具箱打包输出目录。' }
        Write-AxTaskStage -Stage 'package' -Message '正在打包剧情工具箱 Release x64...'
        & (Join-Path $root 'Scripts\Package-App.ps1') -Configuration Release -Runtime win-x64 -OutputRoot $OutputRoot
        Write-AxTaskProgress -Stage 'package' -Percent 100 -Message '剧情工具箱打包脚本执行完成。' -Detail $OutputRoot
    }
    elseif ($Action -eq 'ValidatePackage') {
        Write-AxTaskStage -Stage 'validate' -Message '正在校验剧情工具箱最新打包产物...'
        $packageRoot = Get-NewestGalExclePackage -PackageOutputRoot $OutputRoot
        Assert-GalExclePackage -PackageRoot $packageRoot | Out-Null
        Write-AxTaskProgress -Stage 'validate' -Percent 100 -Message '剧情工具箱打包产物校验通过。' -Detail $packageRoot
    }

    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "剧情工具箱 $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
