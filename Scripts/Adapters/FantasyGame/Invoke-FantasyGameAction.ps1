[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CheckEnvironment', 'CheckGamePackage', 'PublishGameVersion', 'RebuildGameDownloadRepo')]
    [string]$Action,
    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$GamePackageRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Windows', 'Android')][string]$Platform = 'Windows',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
# 真正的实现在 ..\GameUpload\Invoke-GameUploadAction.ps1（三个游戏共用），
# 这里只带本游戏在下载站上的产品段名和版本标记文件名。
$shared = Join-Path (Split-Path -Parent $PSScriptRoot) 'GameUpload\Invoke-GameUploadAction.ps1'
& $shared @PSBoundParameters -ProductSegment 'fantasyproject' -VersionMarkerName 'FantasyProject.version.json'
exit $LASTEXITCODE