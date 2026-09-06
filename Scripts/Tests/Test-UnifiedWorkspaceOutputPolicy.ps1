[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$adapterPaths = @(
    'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1',
    'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1',
    'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1',
    'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1'
)

foreach ($relativePath in $adapterPaths) {
    $path = Join-Path $scriptsRoot $relativePath
    $source = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ($source.Contains('D:\启动器新包', [StringComparison]::OrdinalIgnoreCase)) {
        throw "适配器仍包含旧的全局输出目录：$relativePath"
    }
    if ($source.Contains("Join-Path `$root 'ReleaseAssets'", [StringComparison]::Ordinal)) {
        throw "适配器仍把发布产物回退到源码目录：$relativePath"
    }
}

$axPackagePath = Join-Path $scriptsRoot 'Release\Package-AxTools.ps1'
$axPackageSource = Get-Content -LiteralPath $axPackagePath -Raw -Encoding UTF8
if (!$axPackageSource.Contains('$workRoot = Join-Path $outputFull ".work\$Version"', [StringComparison]::Ordinal)) {
    throw 'AxTools 打包工作目录没有位于统一工作区输出根。'
}

Write-Output 'PASS: AxTools 统一工作区输出合同通过。'
exit 0
