# 出包产物校验：清单 ↔ zip ↔ sha256.txt ↔ 分卷 ↔ 增量包 ↔ 文件索引，全部逐项对账，只读不写。
#
# 这是发布前的最后一道本地闸门（GUI 的「校验产物」动作直接调它）：
# 任何一项对不上都抛错并说清是哪一项，绝不带着可疑产物去上传。

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseAssetRoot,
    [string]$ExpectedVersion = "",
    [switch]$SkipZipInspection
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($null -ne (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue)) { $PSStyle.OutputRendering = 'PlainText' }
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$ToolboxStableKey = 'AxTools'
$ManifestFileName = 'toolbox-update.json'
$RecordFileName = 'artifacts.json'
$IndexFileName = 'file-index.json'

function Get-AxJsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Get-AxFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-AxJsonProperty {
    # 安全读一个**可能不存在**的 JSON 字段：本脚本开着 StrictMode，
    # 缺字段时要报"清单缺少 X"，而不是「在此对象上找不到属性 X」。
    param(
        [Parameter(Mandatory)][AllowNull()]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Assert-AxFileIdentity {
    # 逐项核对「线上会读到的那份记录」与磁盘上的真实文件。
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label,
        [AllowNull()]$Sha256,
        [AllowNull()]$SizeBytes,
        [switch]$SkipHash
    )
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "缺少发布资产（$Label）：$Path" }
    $item = Get-Item -LiteralPath $Path
    if ($null -ne $SizeBytes -and [int64]$item.Length -ne [int64]$SizeBytes) {
        throw "$Label 大小与清单不一致：本地 $($item.Length) / 清单 $([int64]$SizeBytes) 字节。"
    }
    if (!$SkipHash -and ![string]::IsNullOrWhiteSpace([string]$Sha256)) {
        $actual = Get-AxFileSha256 -Path $Path
        if (![string]::Equals($actual, [string]$Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label 的 SHA-256 与清单不一致：本地 $actual / 清单 $([string]$Sha256)。"
        }
    }
}

$root = [IO.Path]::GetFullPath($ReleaseAssetRoot)
$manifestPath = Join-Path $root $ManifestFileName
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少 $ManifestFileName：$root" }
$manifest = Get-AxJsonFile -Path $manifestPath
$stableKey = [string](Get-AxJsonProperty -InputObject $manifest -Name 'toolboxStableKey')
if ($stableKey -ne $ToolboxStableKey) {
    throw "toolboxStableKey 不匹配：$stableKey"
}
$version = [string](Get-AxJsonProperty -InputObject $manifest -Name 'version')
if ([string]::IsNullOrWhiteSpace($version)) { throw '清单缺少 version。' }
if (![string]::IsNullOrWhiteSpace($ExpectedVersion) -and $version -ne $ExpectedVersion.Trim().TrimStart('v')) {
    throw "版本不匹配：清单 $version / 期望 $ExpectedVersion"
}
$channel = if ($version.Contains('-')) { 'beta' } else { 'stable' }
$manifestChannel = [string](Get-AxJsonProperty -InputObject $manifest -Name 'channel')
if ($manifestChannel -ne $channel) {
    throw "清单通道（$manifestChannel）与版本号判定的 $channel 不一致。"
}
$assets = @(Get-AxJsonProperty -InputObject $manifest -Name 'assets')
if ($assets.Count -eq 0) { throw '清单里没有 assets。' }
$asset = $assets[0]
$assetRuntime = [string](Get-AxJsonProperty -InputObject $asset -Name 'runtime')
if ($assetRuntime -ne 'win-x64') { throw "缺少 win-x64 资产：$assetRuntime" }
if (![string]::IsNullOrWhiteSpace([string](Get-AxJsonProperty -InputObject $asset -Name 'downloadUrl'))) {
    throw '清单里的 downloadUrl 必须留空字符串。'
}
$zipName = [string](Get-AxJsonProperty -InputObject $asset -Name 'fileName')
if ([string]::IsNullOrWhiteSpace($zipName)) { throw '清单里的资产缺少 fileName。' }
$zipPath = Join-Path $root $zipName
$shaName = [IO.Path]::GetFileNameWithoutExtension($zipName) + '.sha256.txt'
$shaPath = Join-Path $root $shaName
Assert-AxFileIdentity -Path $zipPath -Label '整包 zip' `
    -Sha256 (Get-AxJsonProperty -InputObject $asset -Name 'sha256') `
    -SizeBytes (Get-AxJsonProperty -InputObject $asset -Name 'sizeBytes')
$zipItem = Get-Item -LiteralPath $zipPath
$zipSha = Get-AxFileSha256 -Path $zipPath
if (!(Test-Path -LiteralPath $shaPath -PathType Leaf)) { throw "缺少发布资产：$shaPath" }

# 分卷：清单里的每一卷都要在、大小与哈希都对，且各卷字节数之和必须等于整包。
$parts = @()
$partsProperty = $asset.PSObject.Properties['parts']
if ($null -ne $partsProperty -and $null -ne $partsProperty.Value) { $parts = @($partsProperty.Value) }
if ($parts.Count -gt 0) {
    $partSum = [int64]0
    foreach ($part in $parts) {
        $partName = [string]$part.fileName
        $partPath = Join-Path $root $partName
        Assert-AxFileIdentity -Path $partPath -Label "分卷 $partName" -Sha256 $part.sha256 -SizeBytes $part.sizeBytes
        if ((Get-Item -LiteralPath $partPath).Length -gt 100MB) {
            throw "分卷 $partName 超过 Gitee 单附件上限 100 MiB。"
        }
        $partSum += [int64](Get-Item -LiteralPath $partPath).Length
    }
    if ($partSum -ne [int64]$zipItem.Length) {
        throw "各分卷字节数之和（$partSum）与整包（$($zipItem.Length)）不一致。"
    }
}
elseif ([int64]$zipItem.Length -gt 100MB) {
    throw ("整包 $([Math]::Round($zipItem.Length / 1MB, 2)) MB 超过 Gitee 单附件上限 100 MiB，" +
        "而清单里没有 parts —— 重新出包（出包脚本会自动分卷）。")
}

# sha256.txt：第一行逐字是「整包sha + 两个空格 + 整包名」，其后每行对应一卷。
$shaLines = @((([IO.File]::ReadAllText($shaPath, [Text.Encoding]::UTF8)).TrimEnd("`r", "`n")) -split "`r?`n")
if ($shaLines.Count -ne ($parts.Count + 1)) {
    throw "sha256.txt 行数（$($shaLines.Count)）应当是「整包 1 行 + 每卷 1 行」= $($parts.Count + 1) 行。"
}
if ($shaLines[0] -ne "$zipSha  $zipName") {
    throw "sha256.txt 第一行与整包对不上：$($shaLines[0])"
}
for ($i = 1; $i -lt $shaLines.Count; $i++) {
    $expected = "$($parts[$i - 1].sha256)  $($parts[$i - 1].fileName)"
    if ($shaLines[$i] -ne $expected) {
        throw "sha256.txt 第 $($i + 1) 行与清单里的分卷对不上：$($shaLines[$i])"
    }
}

# 增量包：清单声明了就必须在，且大小 / 哈希一致。
$deltaNode = Get-AxJsonProperty -InputObject $manifest -Name 'delta'
if ($null -ne $deltaNode) {
    $deltaName = [string]$deltaNode.fileName
    if ([string]::IsNullOrWhiteSpace($deltaName)) { throw '清单里的 delta 段没有 fileName。' }
    if ([int64]$deltaNode.sizeBytes -gt 100MB) { throw "增量包 $deltaName 超过单附件 100 MiB。" }
    Assert-AxFileIdentity -Path (Join-Path $root $deltaName) -Label "增量包 $deltaName" `
        -Sha256 $deltaNode.sha256 -SizeBytes $deltaNode.sizeBytes
}

# 文件索引：下一版算增量时的唯一依据，这里把它的文件表与程序目录对一遍。
$indexPath = Join-Path $root $IndexFileName
if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
    $index = Get-AxJsonFile -Path $indexPath
    $indexVersion = [string](Get-AxJsonProperty -InputObject $index -Name 'version')
    if ($indexVersion -ne $version) {
        throw "file-index.json 版本（$indexVersion）与清单（$version）不一致。"
    }
    $programDirectory = [string](Get-AxJsonProperty -InputObject $index -Name 'programDirectory')
    $indexFiles = Get-AxJsonProperty -InputObject $index -Name 'files'
    $listed = if ($null -eq $indexFiles) { @() } else { @($indexFiles.PSObject.Properties) }
    $indexCount = [int](Get-AxJsonProperty -InputObject $index -Name 'fileCount')
    if ($indexCount -ne $listed.Count) {
        throw "file-index.json 的 fileCount（$indexCount）与实际条目数（$($listed.Count)）不一致。"
    }
    if (Test-Path -LiteralPath $programDirectory -PathType Container) {
        foreach ($property in $listed) {
            $filePath = Join-Path $programDirectory $property.Name
            if (!(Test-Path -LiteralPath $filePath -PathType Leaf)) {
                throw "file-index.json 里的文件在程序目录里不存在：$($property.Name)"
            }
            $item = Get-Item -LiteralPath $filePath
            if ([int64]$item.Length -ne [int64]$property.Value.sizeBytes) {
                throw "file-index.json 里 $($property.Name) 的大小与实际不符：索引 $($property.Value.sizeBytes) / 实际 $($item.Length)。"
            }
            if ((Get-AxFileSha256 -Path $filePath) -ne [string]$property.Value.sha256) {
                throw "file-index.json 里 $($property.Name) 的 SHA-256 与实际不符。"
            }
        }
        Write-Host "  文件索引核对通过：$($listed.Count) 个文件（与 $programDirectory 逐文件一致）"
    }
    else {
        Write-Host "  提示：程序目录不在了（$programDirectory），file-index.json 只核对了版本与条目数。" -ForegroundColor Yellow
    }
}

# 台账：存在就必须与三件产物对得上（发布脚本靠它判断"能不能复用已有包"）。
$recordPath = Join-Path $root $RecordFileName
if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
    $record = Get-AxJsonFile -Path $recordPath
    $recordVersion = [string](Get-AxJsonProperty -InputObject $record -Name 'version')
    if ($recordVersion -ne $version) { throw "artifacts.json 版本（$recordVersion）与清单不一致。" }
    if (![string]::Equals([string](Get-AxJsonProperty -InputObject $record -Name 'sha256'), $zipSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'artifacts.json 里的 sha256 与整包实际哈希不一致。'
    }
    if ([int64](Get-AxJsonProperty -InputObject $record -Name 'sizeBytes') -ne [int64]$zipItem.Length) {
        throw 'artifacts.json 里的 sizeBytes 与整包实际大小不一致。'
    }
}

if (!$SkipZipInspection) {
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('AxTools.exe', 'App.xbf', 'MainWindow.xbf', 'AxTools.pri', 'update-package.json', 'Scripts/Release/Update-AxTools.ps1')) {
            if ($required -notin $names) { throw "ZIP 缺少：$required" }
        }
        if ($parts.Count -eq 0 -and $names.Count -eq 0) { throw 'ZIP 是空包。' }
        $entry = $archive.GetEntry('update-package.json')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $package = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ([string](Get-AxJsonProperty -InputObject $package -Name 'toolboxStableKey') -ne $ToolboxStableKey -or
            [string](Get-AxJsonProperty -InputObject $package -Name 'version') -ne $version -or
            [string](Get-AxJsonProperty -InputObject $package -Name 'runtime') -ne 'win-x64' -or
            [string](Get-AxJsonProperty -InputObject $package -Name 'entryExe') -ne 'AxTools.exe') {
            throw '包内清单身份信息无效（toolboxStableKey / version / runtime / entryExe）。'
        }
    }
    finally { $archive.Dispose() }
}

Write-Host ("PASS: AxTools 发布包校验通过（v$version / $channel / $([Math]::Round($zipItem.Length / 1MB, 2)) MB" +
    $(if ($parts.Count -gt 0) { " / $($parts.Count) 卷" } else { '' }) +
    $(if ($null -ne $deltaNode) { " / 增量 $([Math]::Round([int64]$deltaNode.sizeBytes / 1MB, 2)) MB" } else { '' }) + '）。')
$global:LASTEXITCODE = 0
