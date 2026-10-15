# GitHub 单通道入口（实现见 AxToolsPublish.ps1）。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Notes = '',
    [string]$StagingDirectory = '',
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$GithubRepository = 'kirito0000001/AxTools',
    [string]$GithubBranch = 'main',
    [string]$GithubAccessToken = '',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipSelfCheck,
    [switch]$TolerateSelfCheckFailure
)
. (Join-Path $PSScriptRoot 'AxToolsPublish.ps1')
exit (Invoke-AxToolsGithubPublish @PSBoundParameters)
