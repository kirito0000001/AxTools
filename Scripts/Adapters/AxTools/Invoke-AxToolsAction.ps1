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
Import-Module (Join-Path $scriptsRoot 'Release\AxToolsReleaseCommon.psm1') -Force

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

function Get-AxToolsTargetVersion {
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '打包或发布必须填写版本号。' }
    return $Version.Trim().TrimStart('v')
}

function Get-AxToolsStagingDirectory {
    # 每一版一个产物目录（和 ZD 工具箱一个规矩）：zip / sha256 / 清单 / 台账 / 文件索引 / 程序目录都在这下面，
    # 下一版算增量时要回来读上一版的 file-index.json。
    return Join-Path (Get-AxToolsReleaseRoot) "v$(Get-AxToolsTargetVersion)"
}

function Test-AxToolsAiRuntimeExcluded {
    # 体积墙开关：默认**剔除** WindowsAppSDK 传递进来的 AI 推理运行时（AxTools 用不到，实测省 16 MB 左右）；
    # 设 AXTOOLS_EXCLUDE_AI_RUNTIME=0 可以要回它们（见 Build-AxToolsUpdatePackage.ps1 里的实测依据）。
    foreach ($scope in @('Process', 'User')) {
        $value = [Environment]::GetEnvironmentVariable('AXTOOLS_EXCLUDE_AI_RUNTIME', $scope)
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim() -notin @('0', 'false', 'False', 'FALSE', 'no', 'NO')
        }
    }
    return $true
}

function Assert-AxToolsChannelMatchesVersion {
    <#
        通道由**版本号**决定（带 -Beta 之类后缀就是测试版）—— 这与 ZD 工具箱、以及两条通道
        prerelease 的判定是同一套规则。界面上那个通道选择框只做校对：
        选 beta 却填 1.1.0，说明两边不一致，这里直接拒绝，别发出一个"通道标测试版、版本却像正式版"的包。
    #>
    $targetVersion = Get-AxToolsTargetVersion
    $expected = if ($targetVersion.Contains('-')) { 'beta' } else { 'stable' }
    $selected = if ([string]::IsNullOrWhiteSpace($Channel)) { $expected } else { $Channel.Trim().ToLowerInvariant() }
    if ($selected -notin @('stable', 'beta')) { throw "通道只能是 stable 或 beta：$Channel" }
    if ($selected -ne $expected) {
        $hint = if ($expected -eq 'beta') { "测试版请把版本号写成 $targetVersion（已带后缀）" } else { '正式版请填纯三段版本号，例如 1.1.0' }
        throw ("通道与版本号不一致：通道选了 $selected，但版本号 $targetVersion 判定为 $expected。" +
            "通道以版本号为准 —— $hint。")
    }
    return $expected
}

function Get-AxToolsBuildArguments {
    # 出包参数：体积墙那道开关（见 Build-AxToolsUpdatePackage.ps1 里的实测依据）。
    $arguments = @{
        Version          = (Get-AxToolsTargetVersion)
        ProjectRoot      = $root
        StagingDirectory = (Get-AxToolsStagingDirectory)
    }
    if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments.Notes = $ReleaseNotes }
    if (Test-AxToolsAiRuntimeExcluded) {
        $arguments.ExcludeAiRuntime = $true
    }
    else {
        Write-Host 'AXTOOLS_EXCLUDE_AI_RUNTIME=0：本次不剔除 WindowsAppSDK 带进来的 AI 推理运行时（包会大 16 MB 左右）。' -ForegroundColor Yellow
    }
    if ($DryRun) { $arguments.DryRun = $true }
    return $arguments
}

function Invoke-AxToolsPackage {
    param([Parameter(Mandatory)][string]$PackageChannel)

    [void](Assert-AxToolsChannelMatchesVersion)
    $packageScript = Join-Path $root 'Scripts\Release\Build-AxToolsUpdatePackage.ps1'
    Write-AxTaskProgress -Stage 'package' -Percent 10 -Message "正在制作 AxTools $PackageChannel 发布包..."
    $buildArguments = Get-AxToolsBuildArguments
    & $packageScript @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "出包失败，退出码 $LASTEXITCODE。" }
    # 演练不落任何文件，也就没有可校验的产物。
    if (!$DryRun) { Invoke-AxToolsPackageValidation }
}

function Invoke-AxToolsPackageValidation {
    $validateScript = Join-Path $root 'Scripts\Release\Test-AxToolsPackage.ps1'
    $arguments = @{ ReleaseAssetRoot = (Get-AxToolsStagingDirectory) }
    if (![string]::IsNullOrWhiteSpace($Version)) { $arguments.ExpectedVersion = (Get-AxToolsTargetVersion) }
    Write-AxTaskProgress -Stage 'validate' -Percent 70 -Message '正在校验清单、ZIP、SHA-256、分卷与增量包...'
    & $validateScript @arguments
    if ($LASTEXITCODE -ne 0) { throw "产物校验失败，退出码 $LASTEXITCODE。" }
}

function Invoke-AxToolsUpload {
    param([switch]$AsDryRun, [switch]$SkipBuild)

    [void](Assert-AxToolsChannelMatchesVersion)
    $publishScript = Join-Path $root 'Scripts\Release\Publish-AxToolsRelease.ps1'
    $arguments = @{
        Version          = (Get-AxToolsTargetVersion)
        ProjectRoot      = $root
        StagingDirectory = (Get-AxToolsStagingDirectory)
    }
    if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments.Notes = $ReleaseNotes }
    if ($SkipBuild) { $arguments.SkipBuild = $true }
    if ($AsDryRun) { $arguments.DryRun = $true }
    Write-AxTaskProgress -Stage 'publish' -Percent 80 -Message $(if ($AsDryRun) {
            '正在执行 AxTools 发布演练（不碰网络）...'
        }
        else {
            '正在发布 AxTools 到 Gitee 与 GitHub...'
        })
    & $publishScript @arguments
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE。" }
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @('AxTools.csproj','AxTools.sln')
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 AxTools 任务。'
    if ($Action -eq 'CheckEnvironment') {
        Write-AxTaskStage -Stage 'environment' -Message '正在检查 AxTools 出包与发布环境...'
        $details = [Collections.Generic.List[string]]::new()

        $dotnet = Resolve-AxCommand -Name 'dotnet'
        $sdk = @(& $dotnet --list-sdks 2>&1 | Where-Object { $_ -match '^8\.' })
        if ($sdk.Count -eq 0) { throw '未检测到 .NET 8 SDK。' }
        $details.Add("dotnet=$dotnet（.NET 8 SDK 就绪）")

        $bandizip = @('C:\Program Files\Bandizip\bz.exe', 'C:\Program Files (x86)\Bandizip\bz.exe') |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        $details.Add($(if ([string]::IsNullOrWhiteSpace($bandizip)) {
                    'Bandizip 未找到 → 会退到 .NET ZipFile（压缩率略差，包会更接近 100 MiB 墙）'
                }
                else { "Bandizip=$bandizip" }))

        $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $curl) { throw '没有找到 curl.exe，Gitee / GitHub 附件上传无法进行。' }
        $details.Add("curl=$($curl.Source)")

        $gitee = ''
        try { $gitee = (Get-AxToolsGiteeCredential).SourceName } catch { $gitee = '' }
        $details.Add($(if ([string]::IsNullOrWhiteSpace($gitee)) {
                    '★ 没有找到 Gitee 令牌 → 出包能跑，真实发布会被拒绝'
                }
                else { "Gitee 令牌来源：$gitee（长度已隐去）" }))
        $github = Get-AxToolsGithubCredential
        $details.Add($(if ($null -eq $github) { '★ 没有找到 GitHub 令牌 → 只能加 -SkipGithub 发 Gitee' }
                else { "GitHub 令牌来源：$($github.SourceName)（长度已隐去）" }))

        $updater = Join-Path $root 'Scripts\Release\Update-AxTools.ps1'
        if (!(Test-Path -LiteralPath $updater -PathType Leaf)) {
            $details.Add('★ 缺少 Scripts\Release\Update-AxTools.ps1 → 出包会被拒绝（客户端交付物）')
        }
        else {
            $stream = [IO.File]::OpenRead($updater)
            try {
                $head = [byte[]]::new(3)
                [void]$stream.Read($head, 0, 3)
                $hasBom = $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF
            }
            finally { $stream.Dispose() }
            $details.Add("Update-AxTools.ps1：BOM=$hasBom")
        }
        $details.Add("产物根：$(if ([string]::IsNullOrWhiteSpace($OutputRoot)) { '（未配置）' } else { (Get-AxToolsReleaseRoot) })")

        Write-AxTaskProgress -Stage 'environment' -Percent 100 `
            -Message 'AxTools 出包与发布环境检查完成。' `
            -Detail ($details -join [Environment]::NewLine)
    }
    elseif ($Action -eq 'RunRelease') {
        Start-AxExecutable -ExecutablePath $ReleaseExecutable
        Write-AxTaskProgress -Stage 'launch' -Percent 100 -Message 'AxTools 已启动。' -Detail $ReleaseExecutable
    }
    elseif ($Action -in @('PackageStable','PackageBeta','ValidatePackage','UploadDryRun','Upload','PublishDryRun','Publish')) {
        switch ($Action) {
            'PackageStable' { Invoke-AxToolsPackage -PackageChannel 'stable' }
            'PackageBeta' { Invoke-AxToolsPackage -PackageChannel 'beta' }
            'ValidatePackage' { Invoke-AxToolsPackageValidation }
            'UploadDryRun' { Invoke-AxToolsPackageValidation; Invoke-AxToolsUpload -SkipBuild -AsDryRun }
            'Upload' {
                Invoke-AxToolsPackageValidation
                Set-AxTaskCancellationMode -Mode 'locked' -Message '正在提交 AxTools Release，不可停止。'
                Invoke-AxToolsUpload -SkipBuild
            }
            'PublishDryRun' { Invoke-AxToolsUpload -AsDryRun }
            'Publish' {
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
