[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1'
if (!(Test-Path -LiteralPath $entry -PathType Leaf)) { throw "FantasyTools 适配入口不存在：$entry" }
$source = Get-Content -LiteralPath $entry -Raw
if (!$source.Contains('Invoke-AxDotNetIncrementalBuild', [StringComparison]::Ordinal)) {
    throw 'FantasyTools 构建没有使用统一的 .NET 增量构建策略。'
}
$projectRoot = 'D:\UnrealMap\FantasyTools'

foreach ($action in @('CheckEnvironment', 'ValidatePackage')) {
    $lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action $action -ProjectRoot $projectRoot -OutputRoot (Join-Path $projectRoot 'ReleaseAssets') 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "FantasyTools $action 失败：$($lines -join [Environment]::NewLine)" }
    if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
        throw "FantasyTools $action 缺少成功 result。"
    }
}

Write-Output 'PASS: FantasyTools 适配器契约通过。'
