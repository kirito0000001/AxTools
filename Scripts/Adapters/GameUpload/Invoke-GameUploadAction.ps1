[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment', 'CheckGamePackage', 'PublishGameVersion', 'RebuildGameDownloadRepo')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
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
    [Parameter(Mandatory)][string]$ProductSegment,
    # 跟着产物上线的版本标记文件名（每个游戏一个，格式统一）；留空用内核默认。
    [string]$VersionMarkerName = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force

# 三个游戏（零境 / 幻杀 / 火影）共用这一份；每家的产品段名和标记文件名由各自的
# 包装脚本（Adapters\<游戏>\Invoke-<游戏>Action.ps1）传进来。
$kernel = Join-Path $scriptsRoot 'Publishing\Games\Publish-GameDownloadRelease.ps1'
$segment = if ($Platform -eq 'Android') { "$ProductSegment-android" } else { $ProductSegment }

try {
    if ($Action -eq 'CheckEnvironment') {
        if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
            throw "游戏项目目录不存在：$ProjectRoot"
        }
        if (-not (Test-Path -LiteralPath $kernel -PathType Leaf)) {
            throw "缺少游戏包发布内核：$kernel"
        }
        $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $git) { throw '找不到 git，无法向下载站做增量推送。' }

        Write-AxTaskStage -Stage 'environment' -Message '正在检查游戏包发布环境…'
        Write-AxTaskProgress `
            -Stage 'environment' `
            -Percent 100 `
            -Message '游戏包发布环境检查通过。' `
            -Detail "git=$($git.Source)；内核=$kernel；产品段=$segment"
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '游戏包环境检查完成。'
        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($GamePackageRoot)) { throw '请先选择游戏包目录。' }
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写游戏版本号。' }

    $mode = switch ($Action) {
        'CheckGamePackage' { 'Check' }
        'PublishGameVersion' { 'Publish' }
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
