[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseAssetRoot,
    [string]$ExpectedVersion = ""
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($ReleaseAssetRoot)
$manifestPath = Join-Path $root "toolbox-update.json"
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少 toolbox-update.json" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.toolboxStableKey -ne 'AxTools') { throw "toolboxStableKey 不匹配。" }
if ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion) { throw "版本不匹配：$($manifest.version)" }
$asset = @($manifest.assets)[0]
if ($null -eq $asset -or $asset.runtime -ne 'win-x64') { throw "缺少 win-x64 资产。" }
$zipPath = Join-Path $root $asset.fileName
$shaPath = Join-Path $root ([IO.Path]::GetFileNameWithoutExtension($asset.fileName) + '.sha256.txt')
foreach ($path in @($zipPath, $shaPath)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发布资产：$path" } }
$actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne ([string]$asset.sha256).ToLowerInvariant()) { throw "ZIP SHA-256 与清单不一致。" }
if ((Get-Item -LiteralPath $zipPath).Length -ne [long]$asset.sizeBytes) { throw "ZIP 大小与清单不一致。" }
$shaText = (Get-Content -LiteralPath $shaPath -Raw).Trim().Split()[0].ToLowerInvariant()
if ($shaText -ne $actual) { throw "SHA 文件与 ZIP 不一致。" }
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('AxTools.exe', 'App.xbf', 'MainWindow.xbf', 'AxTools.pri', 'update-package.json', 'Scripts/Release/Update-AxTools.ps1')) {
        if ($required -notin $names) { throw "ZIP 缺少：$required" }
    }
    $entry = $archive.GetEntry('update-package.json')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $package = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($package.toolboxStableKey -ne 'AxTools' -or $package.version -ne $manifest.version -or $package.runtime -ne 'win-x64' -or $package.entryExe -ne 'AxTools.exe') {
        throw "包内清单身份信息无效。"
    }
}
finally { $archive.Dispose() }
Write-Host "PASS: AxTools 发布包校验通过。"
$global:LASTEXITCODE = 0
