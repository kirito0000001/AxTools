[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
foreach ($testName in @(
    'Test-AxTaskProtocol.ps1',
    'Test-AxAdapterCommon.ps1',
    'Test-AxDevelopmentBuildState.ps1',
    'Test-SmartDotNetDevelopmentLaunch.ps1',
    'Test-SmartTauriDevelopmentLaunch.ps1',
    'Test-DevelopmentLaunchPolicy.ps1',
    'Test-SeparatedEnvironmentCheck.ps1',
    'Test-UnifiedWorkspaceOutputPolicy.ps1',
    'Test-ManagedProjectClone.ps1',
    'Test-ManagedReleaseDownload.ps1',
    'Test-AxToolsAdapter.ps1',
    'Test-FantasyToolsAdapter.ps1',
    'Test-GalExcleToolsAdapter.ps1',
    'Test-CrossingVoidZDToolAdapter.ps1',
    'Test-FantasyProjectPcAdapter.ps1',
    'Test-CrossingVoidPcAdapter.ps1',
    'Test-CrossingVoidAndroidAdapter.ps1',
    'Test-CrossingVoidGamePackage.ps1')) {
    & (Join-Path $PSScriptRoot $testName)
    if ($LASTEXITCODE -ne 0) { throw "$testName 返回退出码 $LASTEXITCODE。" }
}

Write-Output 'PASS: 全部统一工具适配器契约通过。'
