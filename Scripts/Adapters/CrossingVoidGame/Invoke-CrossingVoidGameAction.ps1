[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('CheckEnvironment','BuildGameChunks','UploadGameChunks','PublishGamePackage')][string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$GamePackageRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Windows','Android')][string]$Platform = 'Windows',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\..\Publishing\CrossingVoid\Publish-CrossingVoidGame.ps1'
if ($Action -eq 'CheckEnvironment') {
    if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "游戏项目目录不存在：$ProjectRoot" }
    Write-Host "零境游戏目录检查通过：$ProjectRoot"
    exit 0
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $ProjectRoot 'Saved\GamePackages' }
$mode = switch ($Action) {
    'BuildGameChunks' { 'Package' }
    'UploadGameChunks' { 'Upload' }
    'PublishGamePackage' { 'Publish' }
}
$channelName = if ($Channel -eq 'beta') { 'Test' } else { 'Stable' }
$params = @{
    Mode = $mode
    Platform = $Platform
    Channel = $channelName
    GameDirectory = $GamePackageRoot
    OutputRoot = $OutputRoot
    ReleaseVersion = $Version
    ReleaseTitle = "零境交错 $Version | $Platform"
    ReleaseNotes = $ReleaseNotes
}
if ($DryRun) { $params['DryRun'] = $true }
& $script @params
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
