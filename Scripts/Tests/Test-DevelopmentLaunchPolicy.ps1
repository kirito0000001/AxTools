[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$pwsh = Join-Path $PSHOME 'pwsh.exe'

function Assert-ActionRejected {
    param([string]$RelativeEntry,[string]$Action)

    $entry = Join-Path $scriptsRoot $RelativeEntry
    $lines = @(& $pwsh -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action $Action -ProjectRoot 'D:\UnrealMap\AxTools' 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -eq 0) {
        throw "$RelativeEntry 仍接受已删除动作 $Action。"
    }
    if (!($lines | Where-Object { $_ -match 'ValidateSet|validate argument' })) {
        throw "$RelativeEntry 拒绝 $Action 时没有命中动作白名单：$($lines -join [Environment]::NewLine)"
    }
}

function Assert-ActionAcceptedByValidateSet {
    param([string]$RelativeEntry)

    $entry = Join-Path $scriptsRoot $RelativeEntry
    $source = Get-Content -Raw -Encoding UTF8 -LiteralPath $entry
    if ($source -notmatch "(?s)ValidateSet\([^)]*'ForceBuildAndRun'[^)]*\)") {
        throw "$RelativeEntry 尚未在动作白名单中接受 ForceBuildAndRun。"
    }
}

function Assert-SourceMatches {
    param([string]$RelativeEntry,[string]$Pattern,[string]$Message)

    $source = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $scriptsRoot $RelativeEntry)
    if ($source -notmatch $Pattern) {
        throw "$RelativeEntry：$Message"
    }
}

Assert-ActionRejected 'Adapters\AxTools\Invoke-AxToolsAction.ps1' 'RunDevelopment'
Assert-ActionRejected 'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1' 'RunDevelopment'
Assert-ActionRejected 'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1' 'RunDevelopment'
Assert-ActionRejected 'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1' 'RunDevelopment'
Assert-ActionRejected 'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1' 'RunExistingDevelopment'
Assert-ActionRejected 'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1' 'RunExistingDevelopment'

Assert-ActionAcceptedByValidateSet 'Adapters\AxTools\Invoke-AxToolsAction.ps1'
Assert-ActionAcceptedByValidateSet 'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1'
Assert-ActionAcceptedByValidateSet 'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1'
Assert-ActionAcceptedByValidateSet 'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1'
Assert-ActionAcceptedByValidateSet 'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1'
Assert-ActionAcceptedByValidateSet 'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1'

foreach ($relativeEntry in @(
    'Adapters\AxTools\Invoke-AxToolsAction.ps1',
    'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1',
    'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1',
    'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1')) {
    Assert-SourceMatches $relativeEntry "Configuration\s*=\s*'DebugX64'" '开发构建状态必须使用 DebugX64。'
    Assert-SourceMatches $relativeEntry "(?s)(-Configuration\s+'Debug'|'--configuration',\s*'Debug')" '开发构建命令必须使用 Debug。'
    Assert-SourceMatches $relativeEntry "ValidateSet\([^)]*'RunRelease'" '必须保留独立的正式版启动动作。'
}

foreach ($relativeEntry in @(
    'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1',
    'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1')) {
    Assert-SourceMatches $relativeEntry 'target\\debug' 'Tauri 开发产物必须位于 debug 目录。'
    Assert-SourceMatches $relativeEntry "'--debug'" 'Tauri 开发构建必须显式使用 --debug。'
    Assert-SourceMatches $relativeEntry "ValidateSet\([^)]*'RunRelease'" '必须保留独立的正式版启动动作。'
}

Assert-SourceMatches `
    'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1' `
    "'assembleDebug'" `
    'Android 开发构建必须使用 assembleDebug。'
Assert-SourceMatches `
    'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1' `
    "ValidateSet\([^)]*'RunRelease'" `
    '必须保留独立的正式版启动动作。'

Write-Output 'PASS: 所有开发启动动作都要求使用当前源码的 Debug 构建。'
exit 0
