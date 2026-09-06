[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('CheckEnvironment','BuildAndRun','ForceBuildAndRun','Test','CheckUnrealSyncEnvironment','InspectArtifacts','OpenDevelopmentOutput','OpenWorkspace','RunRelease','OpenReleaseDirectory','ValidateAndStagePackage','ReplaceRelease')][string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

$appName = '零境交错：ZD工具箱'
$settingsPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'CrossingVoidZDTool\settings.json'
$pendingRoot = if (![string]::IsNullOrWhiteSpace($env:AXTOOLS_PENDING_PACKAGE_ROOT)) {
    [IO.Path]::GetFullPath($env:AXTOOLS_PENDING_PACKAGE_ROOT)
}
else {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AxTools\PendingPackages'
}
$pendingPath = Join-Path $pendingRoot 'CrossingVoidZDTool.json'

function Assert-ZdBuildOutput {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { throw '尚未配置 ZD空界幻境开发版程序路径。' }
    $executable = [IO.Path]::GetFullPath($ExecutablePath)
    $directory = Split-Path -Parent $executable
    foreach ($path in @(
        $executable,
        (Join-Path $directory "$appName.pri"),
        (Join-Path $directory 'App.xbf'),
        (Join-Path $directory 'MainWindow.xbf'))) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "ZD空界幻境编译产物不完整，缺少：$path"
        }
    }
    return $executable
}

function Get-ZdPackageRoot {
    param([Parameter(Mandatory)][string]$Root)

    $prefix = $appName + 'V'
    $packages = foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop) {
        if (!$directory.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $parsed = $null
        if ([Version]::TryParse($directory.Name.Substring($prefix.Length), [ref]$parsed)) {
            [pscustomobject]@{ Path = $directory.FullName; Version = $parsed }
        }
    }
    $package = $packages | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $package) { throw "未找到 ZD空界幻境版本目录：$Root\${prefix}*" }
    return $package
}

function Get-ZdPackageFiles {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $programRoot = Join-Path $PackageRoot $appName
    return @(
        (Join-Path $programRoot "$appName.exe"),
        (Join-Path $programRoot "$appName.pri"),
        (Join-Path $programRoot 'App.xbf'),
        (Join-Path $programRoot 'MainWindow.xbf'),
        (Join-Path $programRoot "$appName.runtimeconfig.json"),
        (Join-Path $programRoot "$appName.deps.json"))
}

function Assert-ZdPackage {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $files = Get-ZdPackageFiles -PackageRoot $PackageRoot
    foreach ($path in $files) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "ZD空界幻境打包产物不完整，缺少：$path"
        }
    }
    return $files
}

function Get-ZdFileIdentity {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [ordered]@{
        path = $item.FullName
        length = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Write-ZdPendingPackage {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][Version]$PackageVersion,
        [Parameter(Mandatory)][string]$TargetRoot,
        [Parameter(Mandatory)][string[]]$Files)

    New-Item -ItemType Directory -Path $pendingRoot -Force | Out-Null
    $record = [ordered]@{
        schemaVersion = 1
        packageRoot = [IO.Path]::GetFullPath($PackageRoot)
        targetRoot = [IO.Path]::GetFullPath($TargetRoot)
        version = $PackageVersion.ToString()
        validatedAtUtc = [DateTime]::UtcNow.ToString('O')
        files = @($Files | ForEach-Object { Get-ZdFileIdentity -Path $_ })
    }
    $temporary = "$pendingPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $pendingPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Read-ValidZdPendingPackage {
    if (!(Test-Path -LiteralPath $pendingPath -PathType Leaf)) { throw '没有已验证、等待替换的 ZD空界幻境包。' }
    $record = Get-Content -Raw -Encoding UTF8 -LiteralPath $pendingPath | ConvertFrom-Json
    if ([int]$record.schemaVersion -ne 1) { throw 'ZD空界幻境待替换记录版本无效，请重新打包验证。' }
    $packageRoot = [IO.Path]::GetFullPath([string]$record.packageRoot)
    $stagingRoot = [IO.Path]::GetFullPath((Join-Path ([string]$OutputRoot) '.axtools-staging')).TrimEnd('\')
    if (!$packageRoot.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '待替换包不在 AxTools 暂存目录内。'
    }
    foreach ($identity in @($record.files)) {
        $path = [IO.Path]::GetFullPath([string]$identity.path)
        if (!$path.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            !(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw '待替换包文件缺失或越过暂存目录，请重新打包验证。'
        }
        $actual = Get-ZdFileIdentity -Path $path
        if ([long]$actual.length -ne [long]$identity.length -or
            ![string]::Equals([string]$actual.sha256, [string]$identity.sha256, [StringComparison]::Ordinal)) {
            throw "待替换包已发生变化，请重新打包验证：$path"
        }
    }
    return $record
}

function Test-ZdExecutableRunning {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { return $false }
    $target = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in [Diagnostics.Process]::GetProcesses()) {
        try {
            if ([string]::Equals($process.MainModule.FileName, $target, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        catch { }
        finally { $process.Dispose() }
    }
    return $false
}

function Open-ZdDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (!(Test-Path -LiteralPath $fullPath -PathType Container)) { throw "目录不存在：$fullPath" }
    Start-Process -FilePath 'explorer.exe' -ArgumentList $fullPath
}

function Get-ZdSettings {
    if (!(Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw "ZD空界幻境设置不存在：$settingsPath" }
    return Get-Content -Raw -Encoding UTF8 -LiteralPath $settingsPath | ConvertFrom-Json
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'CrossingVoidZDTool.csproj',
        'Tests\CrossingVoidZDTool.RegressionTests\CrossingVoidZDTool.RegressionTests.csproj',
        'Pakout.ps1')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 ZD空界幻境任务。'
    if ($Action -eq 'CheckEnvironment') {
        Write-AxTaskStage -Stage 'environment' -Message '正在检查 ZD空界幻境环境...'
        $dotnet = Resolve-AxCommand -Name 'dotnet'
        $sdk = @(& $dotnet --list-sdks 2>&1 | Where-Object { $_ -match '^8\.' })
        if ($sdk.Count -eq 0) { throw '未检测到 .NET 8 SDK。' }
        Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message 'ZD空界幻境环境检查通过。' -Detail ($sdk -join '; ')
    }
    elseif ($Action -in @('BuildAndRun','ForceBuildAndRun')) {
        $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($DevelopmentExecutable))
        $resources = @(
            (Join-Path $outputDirectory "$appName.pri"),
            (Join-Path $outputDirectory 'App.xbf'),
            (Join-Path $outputDirectory 'MainWindow.xbf'))
        $state = @{
            ToolKey = 'CrossingVoidZDTool'
            ProjectRoot = $root
            Profile = 'DotNet'
            Configuration = 'DebugX64'
            ExecutablePath = $DevelopmentExecutable
            RequiredResourcePaths = $resources
        }
        Write-AxTaskProgress `
            -Stage 'build-state' `
            -Percent 3 `
            -Message '步骤 1/8 · 正在检查源码与 Debug 产物状态...' `
            -Detail '正在比较源码指纹，并检查 Debug EXE、PRI、App.xbf 与 MainWindow.xbf。'
        $isCurrent = $Action -eq 'BuildAndRun' -and (Test-AxDevelopmentBuildCurrent @state)
        if ($isCurrent) {
            Write-AxTaskProgress `
                -Stage 'build-state' `
                -Percent 35 `
                -Message '步骤 1/3 · 源码未变化，Debug 产物有效' `
                -Detail '已命中上次成功构建状态，本次无需调用 MSBuild。'
            Write-AxTaskProgress `
                -Stage 'launch' `
                -Percent 70 `
                -Message '步骤 2/3 · 正在启动现有 Debug 产物...' `
                -Detail $DevelopmentExecutable
            Start-AxExecutable -ExecutablePath $DevelopmentExecutable
            Write-AxTaskProgress `
                -Stage 'launch' `
                -Percent 100 `
                -Message '步骤 3/3 · ZD Debug 程序已启动' `
                -Detail '本次为源码无变化智能直启。'
        }
        else {
            $message = if ($Action -eq 'ForceBuildAndRun') {
                '步骤 2/8 · 已选择强制重新构建'
            }
            else {
                '步骤 2/8 · 检测到变化，准备增量构建'
            }
            $detail = if ($Action -eq 'ForceBuildAndRun') {
                '忽略已有构建状态，重新运行 Debug x64 增量构建。'
            }
            else {
                '源码指纹、配置或必要运行资源与上次成功构建状态不一致。'
            }
            Write-AxTaskProgress -Stage 'build' -Percent 5 -Message $message -Detail $detail
            $dotnet = Resolve-AxCommand -Name 'dotnet'
            $project = Join-Path $root 'CrossingVoidZDTool.csproj'
            Invoke-AxDotNetIncrementalBuild `
                -DotNetPath $dotnet `
                -TargetPath $project `
                -ProjectRoot $root `
                -Configuration 'Debug' `
                -AdditionalArguments @('--runtime','win-x64','-p:Platform=x64') `
                -Operation 'ZD空界幻境 Debug x64' `
                -DependencyStepPrefix '步骤 3/8 · ' `
                -BuildStepPrefix '步骤 4/8 · ' `
                -ForceRestore `
                -BuildVerbosity 'normal'
            Write-AxTaskProgress `
                -Stage 'verify' `
                -Percent 85 `
                -Message '步骤 5/8 · 正在校验 EXE 与 WinUI 资源...' `
                -Detail '需要校验：EXE、PRI、App.xbf、MainWindow.xbf'
            $executable = Assert-ZdBuildOutput -ExecutablePath $DevelopmentExecutable
            $state.ExecutablePath = $executable
            Write-AxTaskProgress `
                -Stage 'build-state' `
                -Percent 90 `
                -Message '步骤 6/8 · 正在记录源码指纹与构建状态...' `
                -Detail '资源校验通过；正在写入本次成功构建的源码指纹和产物信息。'
            Write-AxDevelopmentBuildState @state
            Write-AxTaskProgress `
                -Stage 'launch' `
                -Percent 95 `
                -Message '步骤 7/8 · 正在启动 ZD Debug 程序...' `
                -Detail $executable
            Start-AxExecutable -ExecutablePath $executable
            Write-AxTaskProgress `
                -Stage 'launch' `
                -Percent 100 `
                -Message '步骤 8/8 · ZD Debug 构建与启动完成' `
                -Detail 'Debug x64 产物与 WinUI 资源已校验，构建状态已更新。'
        }
    }
    elseif ($Action -eq 'Test') {
        $dotnet = Resolve-AxCommand -Name 'dotnet'
        $testProject = Join-Path $root 'Tests\CrossingVoidZDTool.RegressionTests\CrossingVoidZDTool.RegressionTests.csproj'
        $exitCode = 0
        Invoke-AxExternalCommand -FilePath $dotnet -ArgumentList @('test', $testProject, '--configuration', 'Release') -WorkingDirectory $root -ExitCode ([ref]$exitCode)
        Assert-AxExternalSuccess -ExitCode $exitCode -Operation 'ZD空界幻境回归测试'
        Write-AxTaskProgress -Stage 'test' -Percent 100 -Message 'ZD空界幻境回归测试通过。'
    }
    elseif ($Action -eq 'RunRelease') {
        Start-AxExecutable -ExecutablePath $ReleaseExecutable
        Write-AxTaskProgress -Stage 'launch' -Percent 100 -Message 'ZD空界幻境正式版已启动。' -Detail $ReleaseExecutable
    }
    elseif ($Action -eq 'OpenDevelopmentOutput') {
        Open-ZdDirectory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($DevelopmentExecutable)))
    }
    elseif ($Action -eq 'OpenReleaseDirectory') {
        Open-ZdDirectory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReleaseExecutable)))
    }
    elseif ($Action -eq 'OpenWorkspace') {
        $settings = Get-ZdSettings
        Open-ZdDirectory -Path ([string]$settings.ProjectRootPath)
    }
    elseif ($Action -eq 'CheckUnrealSyncEnvironment') {
        $settings = Get-ZdSettings
        foreach ($path in @([string]$settings.UnrealEnginePath,[string]$settings.UnrealProjectPath)) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Unreal 同步环境文件不存在：$path" }
        }
        Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message 'Unreal 同步环境检查通过；本次未启动或构建 Unreal。' -Detail "UE=$($settings.UnrealEnginePath); Project=$($settings.UnrealProjectPath)"
    }
    elseif ($Action -eq 'InspectArtifacts') {
        [xml]$projectXml = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'CrossingVoidZDTool.csproj')
        $projectVersion = [string]$projectXml.Project.PropertyGroup.Version | Select-Object -First 1
        $details = @("项目版本=$projectVersion")
        foreach ($path in @($DevelopmentExecutable,$ReleaseExecutable)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $item = Get-Item -LiteralPath $path
                $details += "$($item.FullName) | 文件版本=$($item.VersionInfo.FileVersion) | 时间=$($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
            }
            else { $details += "缺失=$path" }
        }
        Write-AxTaskProgress -Stage 'inspect' -Percent 100 -Message 'ZD空界幻境版本与产物检查完成。' -Detail ($details -join [Environment]::NewLine)
    }
    elseif ($Action -eq 'ValidateAndStagePackage') {
        if ([string]::IsNullOrWhiteSpace($OutputRoot)) { throw '尚未配置 ZD空界幻境正式输出根目录。' }
        $stagingOutput = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('.axtools-staging\' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stagingOutput -Force | Out-Null
        Write-AxTaskProgress -Stage 'package' -Percent 10 -Message '正在同盘临时目录打包 ZD空界幻境。' -Detail $stagingOutput
        & (Join-Path $root 'Pakout.ps1') -Configuration Release -Runtime win-x64 -OutputRoot $stagingOutput
        $package = Get-ZdPackageRoot -Root $stagingOutput
        $files = @(Assert-ZdPackage -PackageRoot $package.Path)
        $targetRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) (Split-Path -Leaf $package.Path)
        Write-ZdPendingPackage -PackageRoot $package.Path -PackageVersion $package.Version -TargetRoot $targetRoot -Files $files
        Write-AxTaskEvent -Data ([ordered]@{ type = 'artifact'; kind = 'pending-package'; path = $package.Path })
        Write-AxTaskProgress -Stage 'validate' -Percent 100 -Message '临时包校验通过，可以确认替换正式版。' -Detail $package.Path
    }
    elseif ($Action -eq 'ReplaceRelease') {
        $record = Read-ValidZdPendingPackage
        if (Test-ZdExecutableRunning -ExecutablePath $ReleaseExecutable) { throw 'ZD空界幻境正式版仍在运行，请关闭后再替换。' }
        $source = [IO.Path]::GetFullPath([string]$record.packageRoot)
        $target = [IO.Path]::GetFullPath([string]$record.targetRoot)
        $backup = "$target.axtools-backup-$([Guid]::NewGuid().ToString('N'))"
        $movedOld = $false
        try {
            if (Test-Path -LiteralPath $target -PathType Container) {
                Move-Item -LiteralPath $target -Destination $backup
                $movedOld = $true
            }
            Move-Item -LiteralPath $source -Destination $target
            if ($movedOld) { Remove-Item -LiteralPath $backup -Recurse -Force }
            Remove-Item -LiteralPath $pendingPath -Force
        }
        catch {
            if (!(Test-Path -LiteralPath $target) -and $movedOld -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $target
            }
            throw
        }
        Write-AxTaskProgress -Stage 'replace' -Percent 100 -Message 'ZD空界幻境正式版替换完成。' -Detail $target
    }

    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "ZD空界幻境 $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.ToString()
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
