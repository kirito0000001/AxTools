[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment','Build','BuildAndRun','ForceBuildAndRun','Test','RunRelease','PackageStable','PackageBeta','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

function Get-AxToolsDevelopmentOutput {
    param([switch]$Validate)

    if ([string]::IsNullOrWhiteSpace($DevelopmentExecutable)) {
        throw '尚未配置 AxTools 开发版程序路径。'
    }
    $executable = [IO.Path]::GetFullPath($DevelopmentExecutable)
    $directory = Split-Path -Parent $executable
    $resources = @(
        (Join-Path $directory 'AxTools.pri'),
        (Join-Path $directory 'App.xbf'),
        (Join-Path $directory 'MainWindow.xbf'))
    if ($Validate) {
        foreach ($path in @($executable) + $resources) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "AxTools 编译产物不完整，缺少：$path"
            }
        }
    }
    return [pscustomobject]@{ Executable = $executable; Resources = $resources }
}

function Get-AxToolsReleaseRoot {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        throw '尚未配置 AxTools 工作区产物目录。'
    }
    return [IO.Path]::GetFullPath($OutputRoot)
}

function Invoke-AxToolsPackage {
    param(
        [Parameter(Mandatory)][ValidateSet('stable','beta')][string]$PackageChannel,
        [switch]$AsDryRun
    )

    if ([string]::IsNullOrWhiteSpace($Version)) { throw '打包或发布必须填写版本号。' }
    $packageScript = Join-Path $root 'Scripts\Release\Package-AxTools.ps1'
    $arguments = @{
        ProjectRoot = $root
        Version = $Version
        Channel = $PackageChannel
        OutputRoot = Get-AxToolsReleaseRoot
        Runtime = 'win-x64'
        ReleaseNotes = $ReleaseNotes
        DryRun = $AsDryRun
    }
    Write-AxTaskProgress -Stage 'package' -Percent 10 -Message "正在制作 AxTools $PackageChannel 发布包..."
    & $packageScript @arguments
    if (!$AsDryRun) { Invoke-AxToolsPackageValidation }
}

function Invoke-AxToolsPackageValidation {
    $validateScript = Join-Path $root 'Scripts\Release\Test-AxToolsPackage.ps1'
    $arguments = @{ ReleaseAssetRoot = Get-AxToolsReleaseRoot }
    if (![string]::IsNullOrWhiteSpace($Version)) { $arguments.ExpectedVersion = $Version }
    Write-AxTaskProgress -Stage 'validate' -Percent 70 -Message '正在校验 AxTools ZIP、SHA-256 与包内清单...'
    & $validateScript @arguments
}

function Invoke-AxToolsUpload {
    param([switch]$AsDryRun)

    if ([string]::IsNullOrWhiteSpace($Version)) { throw '上传或发布必须填写版本号。' }
    $publishScript = Join-Path $root 'Scripts\Release\Publish-AxToolsRelease.ps1'
    Write-AxTaskProgress -Stage 'publish' -Percent 80 -Message $(if ($AsDryRun) {
        '正在执行 AxTools 发布演练...'
    } else {
        '正在发布 AxTools 到 GitHub 与 Gitee...'
    })
    & $publishScript `
        -ProjectRoot $root `
        -Version $Version `
        -Channel $Channel `
        -OutputRoot (Get-AxToolsReleaseRoot) `
        -Target 'Both' `
        -ReleaseNotes $ReleaseNotes `
        -SkipBuild `
        -DryRun:$AsDryRun
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @('AxTools.csproj','AxTools.sln')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 AxTools 任务。'
    if ($Action -eq 'CheckEnvironment') {
        Write-AxTaskStage -Stage 'environment' -Message '正在检查 AxTools 环境...'
        $dotnet = Resolve-AxCommand -Name 'dotnet'
        Write-AxTaskProgress -Stage 'environment' -Percent 100 -Message 'AxTools 环境检查通过。' -Detail $dotnet
    }
    elseif ($Action -eq 'RunRelease') {
        Start-AxExecutable -ExecutablePath $ReleaseExecutable
        Write-AxTaskProgress -Stage 'launch' -Percent 100 -Message 'AxTools 已启动。' -Detail $ReleaseExecutable
    }
    elseif ($Action -in @('PackageStable','PackageBeta','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')) {
        switch ($Action) {
            'PackageStable' { Invoke-AxToolsPackage -PackageChannel 'stable' -AsDryRun:$DryRun }
            'PackageBeta' { Invoke-AxToolsPackage -PackageChannel 'beta' -AsDryRun:$DryRun }
            'ValidatePackage' { Invoke-AxToolsPackageValidation }
            'UploadDryRun' { Invoke-AxToolsPackageValidation; Invoke-AxToolsUpload -AsDryRun }
            'Upload' {
                Invoke-AxToolsPackageValidation
                Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交 AxTools Release，不可停止。'
                Invoke-AxToolsUpload
            }
            'PublishDryRun' {
                Invoke-AxToolsPackage -PackageChannel $Channel
                Invoke-AxToolsUpload -AsDryRun
            }
            'Publish' {
                Invoke-AxToolsPackage -PackageChannel $Channel
                Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交 AxTools Release，不可停止。'
                Invoke-AxToolsUpload
            }
        }
        Write-AxTaskProgress -Stage 'release' -Percent 100 -Message "AxTools $Action 已完成。"
    }
    else {
        $arguments = if ($Action -eq 'Test') {
            @('test', (Join-Path $root 'AxTools.Tests\AxTools.Tests.csproj'), '-c', 'Debug')
        }
        else {
            @()
        }
        Write-AxTaskProgress -Stage 'execute' -Percent 15 -Message "正在执行 $Action..."
        if ($Action -eq 'Test') {
            $dotnet = Resolve-AxCommand -Name 'dotnet'
            $exitCode = 0
            Invoke-AxExternalCommand -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $root -ExitCode ([ref]$exitCode)
            Assert-AxExternalSuccess -ExitCode $exitCode -Operation $Action
        }
        else {
            $output = Get-AxToolsDevelopmentOutput
            $stateParameters = @{
                ToolKey = 'AxTools'
                ProjectRoot = $root
                Profile = 'DotNet'
                Configuration = 'DebugX64'
                ExecutablePath = $output.Executable
                RequiredResourcePaths = $output.Resources
            }
            $isLaunch = $Action -in @('BuildAndRun','ForceBuildAndRun')
            $isCurrent = $Action -eq 'BuildAndRun' -and (Test-AxDevelopmentBuildCurrent @stateParameters)
            if ($isCurrent) {
                Write-AxTaskProgress -Stage 'launch' -Percent 90 -Message '源码未变化，开发产物有效，正在直接启动。' -Detail $output.Executable
            }
            else {
                $buildMessage = if ($Action -eq 'ForceBuildAndRun') { '已选择强制重新编译。' } else { '检测到源码或配置变化，正在增量编译。' }
                Write-AxTaskProgress -Stage 'build' -Percent 25 -Message $buildMessage
                $dotnet = Resolve-AxCommand -Name 'dotnet'
                Invoke-AxDotNetIncrementalBuild `
                    -DotNetPath $dotnet `
                    -TargetPath (Join-Path $root 'AxTools.sln') `
                    -ProjectRoot $root `
                    -Configuration 'Debug' `
                    -AdditionalArguments @('-p:Platform=x64') `
                    -Operation 'AxTools'
                $output = Get-AxToolsDevelopmentOutput -Validate
                $stateParameters.ExecutablePath = $output.Executable
                $stateParameters.RequiredResourcePaths = $output.Resources
                Write-AxDevelopmentBuildState @stateParameters
            }
            if ($isLaunch) { Start-AxExecutable -ExecutablePath $output.Executable }
        }
        Write-AxTaskProgress -Stage 'execute' -Percent 100 -Message "$Action 已完成。"
    }

    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "AxTools $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
