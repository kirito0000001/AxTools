# Gitee 单通道入口（实现见 AxToolsPublish.ps1）。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Notes = '',
    [string]$StagingDirectory = '',
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$GiteeRepository = 'xiaojie578/AxTools',
    [string]$GiteeBranch = 'master',
    [string]$GiteeAccessToken = '',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$DryRun,
    [switch]$ForceRebuild,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipVersionWrite,
    [int64]$PartSizeBytes = 0,
    [switch]$ExcludeAiRuntime,
    [switch]$NoDelta,
    [int]$KeepReleasesWithAssets = 3,
    [switch]$SkipPrune,
    [switch]$KeepOldReleaseRecords,
    [switch]$PruneOnly,
    [switch]$SkipSelfCheck,
    [switch]$TolerateSelfCheckFailure
)
. (Join-Path $PSScriptRoot 'AxToolsPublish.ps1')
exit (Invoke-AxToolsGiteePublish @PSBoundParameters)
