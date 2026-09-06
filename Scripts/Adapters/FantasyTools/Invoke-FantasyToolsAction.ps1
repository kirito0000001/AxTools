[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','Build','BuildAndRun','ForceBuildAndRun','RunRelease','PackageStable','PackageBeta','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')]
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

function Get-FantasyDevelopmentOutput {
    param([switch]$Validate)

    $standardOutput = Join-Path $root 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
    $candidatePaths = @(
        $DevelopmentExecutable,
        (Join-Path $standardOutput '幻杀工具箱.exe'),
        (Join-Path $standardOutput 'FantasyTools.exe'),
        (Join-Path $standardOutput 'AppX\FantasyTools.exe')
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    $outputs = @($candidatePaths | ForEach-Object {
        $executable = [IO.Path]::GetFullPath($_)
        $directory = Split-Path -Parent $executable
        [pscustomobject]@{
            Executable = $executable
            Resources = @(
                (Join-Path $directory (([IO.Path]::GetFileNameWithoutExtension($executable)) + '.pri')),
                (Join-Path $directory 'App.xbf'),
                (Join-Path $directory 'MainWindow.xbf'))
        }
    })
    $output = $outputs | Where-Object {
        $paths = @($_.Executable) + $_.Resources
        @($paths | Where-Object { !(Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0
    } | Select-Object -First 1
    if ($null -eq $output) {
        $standardExecutable = [IO.Path]::GetFullPath((Join-Path $standardOutput '幻杀工具箱.exe'))
        $output = $outputs | Where-Object {
            [string]::Equals($_.Executable, $standardExecutable, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
    }
    if ($null -eq $output) { throw '无法确定 FantasyTools Debug x64 输出路径。' }
    if ($Validate) {
        foreach ($path in @($output.Executable) + $output.Resources) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "FantasyTools 编译产物不完整，缺少：$path"
            }
        }
    }
    return $output
}

function Invoke-FantasyDevelopmentBuild {
    param([switch]$Launch,[switch]$Force)

    $output = Get-FantasyDevelopmentOutput
    $stateParameters = @{
        ToolKey = 'FantasyTools'
        ProjectRoot = $root
        Profile = 'DotNet'
        Configuration = 'DebugX64'
        ExecutablePath = $output.Executable
        RequiredResourcePaths = $output.Resources
    }
    if ($Launch -and !$Force -and (Test-AxDevelopmentBuildCurrent @stateParameters)) {
        Write-AxTaskProgress -Stage 'launch' -Percent 90 -Message '源码未变化，开发产物有效，正在直接启动。' -Detail $output.Executable
        Start-AxExecutable $output.Executable
        return
    }

    $buildMessage = if ($Force) { '已选择强制重新编译。' } else { '检测到源码或配置变化，正在增量编译。' }
    Write-AxTaskProgress -Stage 'build' -Percent 25 -Message $buildMessage
    $dotnet = Resolve-AxCommand -Name 'dotnet'
    Invoke-AxDotNetIncrementalBuild -DotNetPath $dotnet -TargetPath (Join-Path $root 'FantasyTools.sln') -ProjectRoot $root -Configuration 'Debug' -AdditionalArguments @('-p:Platform=x64') -Operation 'FantasyTools 构建'
    $output = Get-FantasyDevelopmentOutput -Validate
    $stateParameters.ExecutablePath = $output.Executable
    $stateParameters.RequiredResourcePaths = $output.Resources
    Write-AxDevelopmentBuildState @stateParameters
    if ($Launch) { Start-AxExecutable $output.Executable }
}

function Get-ReleaseAssetRoot {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        throw '尚未配置 AxTools 工作区产物目录。'
    }
    return [IO.Path]::GetFullPath($OutputRoot)
}

function Test-FantasyPackage {
    $assetRoot = Get-ReleaseAssetRoot
    $manifestPath = Join-Path $assetRoot 'toolbox-update.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少更新清单：$manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $asset = @($manifest.assets | Where-Object { $_.fileName } | Select-Object -First 1)
    if ($asset.Count -eq 0) { throw '更新清单没有可校验的资产。' }
    $zipPath = Join-Path $assetRoot ([string]$asset[0].fileName)
    if (!(Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "缺少 ZIP：$zipPath" }
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = ([string]$asset[0].sha256).ToLowerInvariant()
    if ($actual -ne $expected) { throw "ZIP SHA-256 不匹配：$zipPath" }
    if ([long]$asset[0].sizeBytes -ne (Get-Item -LiteralPath $zipPath).Length) { throw "ZIP 大小与清单不一致：$zipPath" }
    $shaPath = Join-Path $assetRoot (([IO.Path]::GetFileNameWithoutExtension($zipPath)) + '.sha256.txt')
    if (!(Test-Path -LiteralPath $shaPath -PathType Leaf)) { throw "缺少 SHA-256 文件：$shaPath" }
    $shaText = (Get-Content -LiteralPath $shaPath -Raw).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].ToLowerInvariant()
    if ($shaText -ne $actual) { throw "SHA-256 文本与 ZIP 不一致：$shaPath" }
    Write-AxTaskEvent -Data ([ordered]@{ type='artifact'; kind='zip'; path=$zipPath })
    return $assetRoot
}

function Invoke-FantasyPackage {
    param([string]$PackageChannel)
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '打包或完整发布必须填写版本号。' }
    $packageScript = Join-Path $root 'Scripts\打包工具箱.ps1'
    $assetRoot = Get-ReleaseAssetRoot
    & $packageScript -Configuration Release -Runtime win-x64 -OutputRoot $assetRoot -ReleaseAssetRoot $assetRoot -Version $Version -Channel $PackageChannel
    if ($LASTEXITCODE -notin @(0,$null)) { throw "FantasyTools 打包失败，退出码 $LASTEXITCODE。" }
}

function Invoke-FantasyPublish {
    param([switch]$AsDryRun)
    $publishScript = Join-Path $root 'Scripts\发布新版本.ps1'
    $assetRoot = Get-ReleaseAssetRoot
    & $publishScript -OutputRoot $assetRoot -ReleaseAssetRoot $assetRoot -DryRun:$AsDryRun
    if ($LASTEXITCODE -notin @(0,$null)) { throw "FantasyTools 发布脚本失败，退出码 $LASTEXITCODE。" }
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @('FantasyTools.csproj','Scripts\打包工具箱.ps1','Scripts\发布新版本.ps1')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 FantasyTools 任务。'
    switch ($Action) {
        'CheckEnvironment' { Write-AxTaskStage -Stage 'environment' -Message '正在检查 FantasyTools 环境...'; $dotnet = Resolve-AxCommand -Name 'dotnet'; Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message 'FantasyTools 环境检查通过。' -Detail $dotnet }
        'RunRelease' { Start-AxExecutable $ReleaseExecutable }
        'Build' { Invoke-FantasyDevelopmentBuild }
        'BuildAndRun' { Invoke-FantasyDevelopmentBuild -Launch }
        'ForceBuildAndRun' { Invoke-FantasyDevelopmentBuild -Launch -Force }
        'PackageStable' { Invoke-FantasyPackage 'stable'; Test-FantasyPackage | Out-Null }
        'PackageBeta' { Invoke-FantasyPackage 'beta'; Test-FantasyPackage | Out-Null }
        'ValidatePackage' { Test-FantasyPackage | Out-Null }
        'UploadDryRun' { Test-FantasyPackage | Out-Null; Invoke-FantasyPublish -AsDryRun }
        'Upload' { Test-FantasyPackage | Out-Null; Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交发布，不可停止。'; Invoke-FantasyPublish }
        'PublishDryRun' { Invoke-FantasyPackage $Channel; Test-FantasyPackage | Out-Null; Invoke-FantasyPublish -AsDryRun }
        'Publish' { Invoke-FantasyPackage $Channel; Test-FantasyPackage | Out-Null; Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交发布，不可停止。'; Invoke-FantasyPublish }
    }
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "FantasyTools $Action 已完成。"
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "FantasyTools $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
