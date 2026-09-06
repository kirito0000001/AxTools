[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$entry = Join-Path (Split-Path -Parent $PSScriptRoot) 'Adapters\AxTools\Invoke-AxToolsAction.ps1'
if (!(Test-Path -LiteralPath $entry -PathType Leaf)) { throw "AxTools 适配入口不存在：$entry" }
$source = Get-Content -LiteralPath $entry -Raw
if (!$source.Contains('Invoke-AxDotNetIncrementalBuild', [StringComparison]::Ordinal)) {
    throw 'AxTools 构建没有使用统一的 .NET 增量构建策略。'
}

$lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
    -Action CheckEnvironment -ProjectRoot (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 2>&1 |
    ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) { throw "AxTools 环境检查失败：$($lines -join [Environment]::NewLine)" }
if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
    throw 'AxTools 环境检查缺少成功 result。'
}

$dryRunOutputRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'AxTools-Adapter-PackageDryRun-' + [Guid]::NewGuid().ToString('N'))
try {
    $packageLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $entry `
        -Action PackageStable `
        -ProjectRoot (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) `
        -OutputRoot $dryRunOutputRoot `
        -Version '1.2.0' `
        -Channel 'stable' `
        -ReleaseNotes '适配器发布演练' `
        -DryRun 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "AxTools 打包 DryRun 失败：$($packageLines -join [Environment]::NewLine)"
    }
    if (!($packageLines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
        throw 'AxTools 打包 DryRun 缺少成功 result。'
    }
    if (Test-Path -LiteralPath $dryRunOutputRoot) {
        throw 'AxTools 打包 DryRun 不应创建发布目录。'
    }
}
finally {
    if (Test-Path -LiteralPath $dryRunOutputRoot) {
        Remove-Item -LiteralPath $dryRunOutputRoot -Recurse -Force
    }
}

Write-Output 'PASS: AxTools 适配器契约通过。'
