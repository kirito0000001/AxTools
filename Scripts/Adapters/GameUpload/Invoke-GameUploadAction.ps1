[CmdletBinding()]
param(
    # 这些参数一律声明成"可空 + 体内校验"：PowerShell 在参数绑定阶段抛的错会用系统本地代码页
    # （中文机器上是 GBK）写到 stderr，AxTools 按 UTF-8 读就是一串乱码；放到体内 throw 才走
    # 任务协议的 UTF-8 通道，日志里能看清原因。
    [string]$Action = '',
    [AllowEmptyString()][string]$ProjectRoot = '',
    # 内核不需要这两个路径，但 AxTools 的任务协议统一会传，必须声明（否则 -File 绑定直接报错）。
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$GamePackageRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Windows', 'Android')][string]$Platform = 'Windows',
    # 本游戏在下载站上的产品段名（PC 侧）。安卓固定用 <这个>-android。
    [string]$ProductSegment = '',
    # 传输方式：files（默认，逐文件同步）或 git（老路子，兜底/对照）
    [ValidateSet('files', 'git')][string]$TransferMode = 'files',
    # 跟着产物上线的版本标记文件名（每个游戏一个，格式统一）；留空用内核默认。
    [string]$VersionMarkerName = '',
    # GitHub 备用渠道的仓库（owner/repo）；留空 = 这个游戏暂时不往 GitHub 发。
    [string]$GitHubRepo = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force

function Read-GamePackageVersion {
    param([string]$PackageRoot, [string]$ExpectedPlatform)

    $root = [IO.Path]::GetFullPath($PackageRoot)
    $candidates = @(
        (Join-Path $root 'Version.json'),
        (Join-Path (Split-Path -Parent $root) 'Version.json')
    ) | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $data = Get-Content -LiteralPath $candidate -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$data.version)) {
            throw "版本文件缺少 version：$candidate"
        }

        $platformOk = if ($ExpectedPlatform -eq 'Android') {
            ([string]$data.platform) -match 'Android'
        }
        else {
            ([string]$data.platform) -match 'Win'
        }
        if (-not $platformOk) {
            throw "版本文件平台不匹配：期望 $ExpectedPlatform，实际 $($data.platform)（$candidate）"
        }

        Write-AxTaskStage `
            -Stage 'version' `
            -Message "已从打包产物读取版本：$($data.version)" `
            -Detail ("mode={0}；releaseVersion={1}；文件={2}" -f $data.mode, $data.releaseVersion, $candidate)
        return [pscustomobject]@{
            Version        = ([string]$data.version).Trim()
            ReleaseVersion = [string]$data.releaseVersion
            Mode           = [string]$data.mode
            Platform       = [string]$data.platform
            SourcePath     = $candidate
        }
    }

    throw "游戏包里没有 Version.json（包根或上一级），拒绝使用手写版本发布。"
}

# 三个游戏（零境 / 幻杀 / 火影）共用这一份；每家的产品段名和标记文件名由各自的
# 包装脚本（Adapters\<游戏>\Invoke-<游戏>Action.ps1）传进来。
$kernel = Join-Path $scriptsRoot 'Publishing\Games\Publish-GameDownloadRelease.ps1'
$segment = if ($Platform -eq 'Android') { "$ProductSegment-android" } else { $ProductSegment }

try {
    if ($Action -notin @('CheckEnvironment', 'CheckGamePackage', 'PublishGameVersion', 'PublishGameVersionToGitHub', 'PublishGamePatch', 'RebuildGameDownloadRepo')) {
        throw "未知的游戏包发布动作：'$Action'。"
    }
    if ([string]::IsNullOrWhiteSpace($ProductSegment)) {
        throw '缺少产品段名（ProductSegment），无法定位下载站目录。'
    }

    if ($Action -eq 'CheckEnvironment') {
        # 游戏工具没有源码目录，ProjectRoot 就是打包产物目录；两者都空时以产物目录为准。
        $projectRoot = if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $GamePackageRoot } else { $ProjectRoot }
        if ([string]::IsNullOrWhiteSpace($projectRoot) -or -not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
            throw "找不到游戏包目录，无法检查环境：$projectRoot"
        }
        if (-not (Test-Path -LiteralPath $kernel -PathType Leaf)) {
            throw "缺少游戏包发布内核：$kernel"
        }
        $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $git) { throw '找不到 git，无法向下载站做增量推送。' }
        if (-not [string]::IsNullOrWhiteSpace($GitHubRepo)) {
            if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
                throw "配置了 GitHub 渠道（$GitHubRepo），但找不到 GitHub CLI（gh）。"
            }
        }

        Write-AxTaskStage -Stage 'environment' -Message '正在检查游戏包发布环境…'
        Write-AxTaskProgress `
            -Stage 'environment' `
            -Percent 100 `
            -Message '游戏包发布环境检查通过。' `
            -Detail ("git={0}；内核={1}；产品段={2}；GitHub={3}" -f $git.Source, $kernel, $segment, $(if ([string]::IsNullOrWhiteSpace($GitHubRepo)) { '未配置（跳过）' } else { $GitHubRepo }))
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '游戏包环境检查完成。'
        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($GamePackageRoot)) { throw '请先选择游戏包目录。' }
    $buildInfo = Read-GamePackageVersion -PackageRoot $GamePackageRoot -ExpectedPlatform $Platform
    $Version = $buildInfo.Version

    if ($Action -eq 'PublishGamePatch') {
        if ($Platform -ne 'Windows') {
            throw 'Windows 补丁渠道目前只支持 PC 游戏包。'
        }
        if ([string]::IsNullOrWhiteSpace($GitHubRepo)) {
            throw '当前游戏没有配置 GitHub 仓库，无法发布补丁。'
        }
        $patchKernel = Join-Path $scriptsRoot 'Publishing\Games\Publish-GamePatch.ps1'
        if (-not (Test-Path -LiteralPath $patchKernel -PathType Leaf)) {
            throw "缺少 Windows 补丁发布脚本：$patchKernel"
        }
        $patchParameters = @{
            GamePackageRoot = $GamePackageRoot
            ProductSegment  = $segment
            Channel         = $(if ($Channel -eq 'beta') { 'Test' } else { 'Stable' })
            GitHubRepo      = $GitHubRepo
            Version         = $Version
        }
        if ($DryRun) { $patchParameters['DryRun'] = $true }
        Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 Windows 补丁发布任务。'
        & $patchKernel @patchParameters
        if ($LASTEXITCODE -ne 0) {
            throw "Windows 补丁发布脚本返回退出码 $LASTEXITCODE。"
        }
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message 'Windows 补丁发布完成。'
        exit 0
    }

    $mode = switch ($Action) {
        'CheckGamePackage' { 'Check' }
        'PublishGameVersion' { 'Publish' }
        'PublishGameVersionToGitHub' { 'Publish' }
        'RebuildGameDownloadRepo' { 'RebuildRepo' }
    }
    $parameters = @{
        Mode           = $mode
        Platform       = $Platform
        Channel        = $(if ($Channel -eq 'beta') { 'Test' } else { 'Stable' })
        GameDirectory  = $GamePackageRoot
        ReleaseVersion = $Version
        ReleaseNotes   = $ReleaseNotes
        ProductSegment = $segment
    }
    if (-not [string]::IsNullOrWhiteSpace($VersionMarkerName)) {
        $parameters['VersionMarkerName'] = $VersionMarkerName
    }
    if (-not [string]::IsNullOrWhiteSpace($GitHubRepo)) {
        $parameters['GitHubRepo'] = $GitHubRepo
    }
    if ($Action -eq 'PublishGameVersionToGitHub') {
        if ([string]::IsNullOrWhiteSpace($GitHubRepo)) {
            throw "这个游戏没有配置 GitHub 发布仓库，不能执行「发布到 GitHub」。"
        }
        $parameters['GitHubOnly'] = $true
    }
    if ($Action -eq 'PublishGameVersion') {
        # 两个按钮互斥：服务器按钮不再顺带把 GitHub 备用渠道也发一遍。
        $parameters['SkipGitHub'] = $true
    }
    if ($DryRun) { $parameters['DryRun'] = $true }

    if ($Action -eq 'RebuildGameDownloadRepo') {
        Write-AxTaskStage `
            -Stage 'prepare' `
            -Message '正在重整仓库：重建本机暂存仓库并压成单提交，这一次要全量重传（最慢的一步）。'
    }
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前游戏发布任务。'

    & $kernel @parameters
    if ($LASTEXITCODE -ne 0) {
        throw "游戏包发布内核返回退出码 $LASTEXITCODE（详细原因见上面的输出）。"
    }

    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "游戏包 $Action 已完成（$segment）。"
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "游戏包 $Action 完成（$segment）。"
    exit 0
}
catch [System.OperationCanceledException] {
    Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message $_.Exception.Message
    exit 130
}
catch {
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
