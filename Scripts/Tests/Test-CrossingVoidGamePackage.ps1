$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function New-TestApk {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Abi
    )

    $contentRoot = Join-Path ([IO.Path]::GetTempPath()) ("AxTools-TestApk-" + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $contentRoot 'AndroidManifest.xml') -Value 'manifest' -NoNewline
        if (-not [string]::IsNullOrWhiteSpace($Abi)) {
            $libraryPath = Join-Path $contentRoot "lib\$Abi\libUnreal.so"
            New-Item -ItemType Directory -Path (Split-Path -Parent $libraryPath) -Force | Out-Null
            Set-Content -LiteralPath $libraryPath -Value $Abi -NoNewline
        }
        [IO.Compression.ZipFile]::CreateFromDirectory($contentRoot, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $contentRoot) {
            Remove-Item -LiteralPath $contentRoot -Recurse -Force
        }
    }
}

$packageScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Publishing\CrossingVoid\Publish-CrossingVoidGame.ps1'
Assert-True (Test-Path -LiteralPath $packageScript -PathType Leaf) "缺少游戏分片脚本：$packageScript"
$packageSource = Get-Content -LiteralPath $packageScript -Raw -Encoding UTF8
Assert-True ($packageSource -match '\[int64\]\(500MB\)') '游戏分片默认大小不是 500 MiB。'
Assert-True ($packageSource -match '\[string\]\$ReleaseNotes') '零境游戏发布脚本没有接收更新说明。'
Assert-True ($packageSource -match 'CROSSINGVOID_GITEE_TOKEN') '零境游戏发布脚本没有读取专用 Gitee 令牌。'
Assert-True ($packageSource -match '\$script:Version \| \$platformLabel') '零境 Release 标题没有使用版本在前格式。'
Assert-True ($packageSource -notmatch '## 更新说明') '零境 Release 正文仍在额外追加更新说明结构。'
$githubDeleteIndex = $packageSource.IndexOf('release delete-asset', [StringComparison]::Ordinal)
$githubUploadIndex = $packageSource.IndexOf('release upload', [StringComparison]::Ordinal)
Assert-True ($githubUploadIndex -ge 0 -and $githubUploadIndex -lt $githubDeleteIndex) `
    'GitHub 旧附件清理发生在新分片上传前，发布中断会让旧版本不可用。'
Assert-True ($packageSource -match 'digest -ieq \$expectedDigest') 'GitHub 附件核对没有使用远端 SHA-256。'
Assert-True ($packageSource -notmatch 'Where-Object name -CEQ') 'GitHub 附件核对仍错误要求中文文件名完全一致。'
Assert-True ($packageSource -match '\$missingChunks' -and
    $packageSource -match '\$staleAssets' -and
    $packageSource -match 'GitHub 分片断点检查完成') 'GitHub 上传没有实现逐片保留、清理和断点续传。'
$githubVerifyIndex = $packageSource.IndexOf("Write-GameProgress 'verify-github'", [StringComparison]::Ordinal)
$ossUploadIndex = $packageSource.IndexOf("Write-GameProgress 'upload-oss'", [StringComparison]::Ordinal)
$ossVerifyIndex = $packageSource.IndexOf("Write-GameProgress 'verify-oss'", [StringComparison]::Ordinal)
$serverManifestIndex = $packageSource.IndexOf("Write-GameProgress 'server-manifest'", [StringComparison]::Ordinal)
$signatureVerifyIndex = $packageSource.IndexOf("Write-GameProgress 'verify-signatures'", [StringComparison]::Ordinal)
$giteeManifestIndex = $packageSource.IndexOf("Write-GameProgress 'gitee-manifest'", [StringComparison]::Ordinal)
Assert-True ($githubVerifyIndex -ge 0 -and
    $githubVerifyIndex -lt $ossUploadIndex -and
    $ossUploadIndex -lt $ossVerifyIndex -and
    $ossVerifyIndex -lt $serverManifestIndex -and
    $serverManifestIndex -lt $signatureVerifyIndex -and
    $signatureVerifyIndex -lt $githubDeleteIndex -and
    $githubDeleteIndex -lt $giteeManifestIndex -and
    $signatureVerifyIndex -lt $giteeManifestIndex) `
    '发布顺序必须是 GitHub 核对、OSS 上传、OSS 大小核对、服务器白名单清单、逐片签名验证、旧附件清理、Gitee 最终指针。'
Assert-True ($packageSource -match '& \$OssutilPath stat') 'OSS 上传后没有通过 stat 核对远端对象大小。'
Assert-True ($packageSource -match 'OSS 分片已存在，跳过上传' -and $packageSource -match '\$existingOssSize') `
    'OSS 上传没有按远端对象大小实现断点跳过。'
Assert-True ($packageSource -match 'ServerProductsRoot') '零境游戏发布脚本没有配置服务器产品清单根目录。'
Assert-True ($packageSource -match 'Move-Item -LiteralPath `\$temp -Destination `\$target -Force') `
    '服务器白名单清单没有使用临时文件原子替换。'
Assert-True ($packageSource -match 'sign-download' -and $packageSource -match 'foreach \(\$chunk in \$Chunks\)') `
    '服务器清单同步后没有逐片验证 OSS 签名权限。'
Assert-True ($packageSource -match 'githubFileName') 'PC 游戏分片清单没有记录 GitHub 实际附件名。'
Assert-True ($packageSource -match 'GitHub 实际附件名不一致') `
    'GitHub 上传核对没有确认远端附件名与 githubFileName 一致。'
Assert-True ($packageSource -match 'schemaVersion=2') '零境游戏更新清单没有使用协议 2。'
Assert-True ($packageSource -match 'downloadReleaseTag=\(Get-ReleaseTag\)') '零境游戏更新清单缺少下载 Release 标签。'
Assert-True ($packageSource -match '\$chunk\.githubFileName\s*=\s*\[string\]\$remote\[0\]\.name') `
    'GitHub 核对后没有把远端真实附件名写回游戏清单。'
$saveVerifiedManifestIndex = $packageSource.IndexOf('Save-GameUpdateManifest -Manifest $verified.Manifest', [StringComparison]::Ordinal)
Assert-True ($saveVerifiedManifestIndex -ge 0 -and $saveVerifiedManifestIndex -lt $serverManifestIndex) `
    '包含 GitHub 真实附件名的清单没有在服务器白名单发布前保存。'
Assert-True ($packageSource -match "StartsWith\('\{'\)") `
    '服务器同步结果没有过滤 SSH PowerShell 返回的 CLIXML 噪声。'
$bandizip = 'C:\Program Files\Bandizip\bz.exe'
Assert-True (Test-Path -LiteralPath $bandizip -PathType Leaf) "缺少 Bandizip 测试依赖：$bandizip"

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("AxTools-CrossingVoidPackage-" + [Guid]::NewGuid().ToString('N'))
$gameRoot = Join-Path $testRoot 'Game'
$outputRoot = Join-Path $testRoot 'Output'
$extractRoot = Join-Path $testRoot 'Extracted'
New-Item -ItemType Directory -Force -Path $gameRoot,$outputRoot,$extractRoot | Out-Null

try {
    $included = @{
        'CrossingVoid.exe' = ('A' * 41)
        'Content\Paks\pakchunk0.pak' = ('B' * 97)
        '.github\workflow.yml' = ('C' * 23)
        'Saved\LogsArchive\keep.log' = ('D' * 19)
    }
    $excluded = @{
        'Symbols\game.pdb' = 'debug'
        'Saved\Logs\latest.log' = 'log'
        'Saved\Crashes\crash.dmp' = 'crash'
        '_download\partial.bin' = 'partial'
        '.git\config' = 'git'
    }
    foreach ($entry in ($included.GetEnumerator() + $excluded.GetEnumerator())) {
        $path = Join-Path $gameRoot $entry.Key
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        Set-Content -LiteralPath $path -Value $entry.Value -NoNewline -Encoding UTF8
    }
    $sourceHashes = Get-ChildItem -LiteralPath $gameRoot -File -Recurse | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName; Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }

    $windowsOutput = @(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $packageScript -Mode Package -Platform Windows -Channel Stable -GameDirectory $gameRoot -OutputRoot $outputRoot -ReleaseVersion 'V0.5.14' -ReleaseTitle '测试发布' -ChunkSizeBytes 64 2>&1 |
        ForEach-Object { $_.ToString() })
    Assert-True ($LASTEXITCODE -eq 0) "分片脚本退出码错误：$LASTEXITCODE"
    $progressEvents = @($windowsOutput |
        Where-Object { $_.StartsWith('::axtools ', [StringComparison]::Ordinal) } |
        ForEach-Object { $_.Substring('::axtools '.Length) | ConvertFrom-Json } |
        Where-Object type -EQ 'progress')
    Assert-True ($progressEvents.Count -gt 0) '游戏分片脚本没有输出进度事件。'
    Assert-True (-not ($progressEvents | Where-Object { $_.PSObject.Properties.Name -contains 'data' })) '进度字段错误地嵌套在 data 中。'
    Assert-True (-not ($progressEvents | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.stage) -or
        $null -eq $_.percent -or
        [string]::IsNullOrWhiteSpace([string]$_.message)
    })) '进度事件缺少顶层 stage、percent 或 message。'
    Assert-True ($progressEvents[0].stage -eq 'cleanup') '首个进度阶段应立即说明正在清理旧分片。'

    $releaseRoot = Join-Path $outputRoot 'Windows-Stable-V0.5.14'
    $manifestPath = Join-Path $releaseRoot 'CrossingVoid-PC-update.json'
    $artifactEvents = @($windowsOutput |
        Where-Object { $_.StartsWith('::axtools ', [StringComparison]::Ordinal) } |
        ForEach-Object { $_.Substring('::axtools '.Length) | ConvertFrom-Json } |
        Where-Object type -EQ 'artifact')
    Assert-True ($artifactEvents.Count -eq 2) '游戏分片脚本没有输出版本目录和清单两个产物事件。'
    Assert-True ($artifactEvents.kind -contains 'game-package') '游戏分片脚本缺少版本目录产物事件。'
    Assert-True ($artifactEvents.kind -contains 'manifest') '游戏分片脚本缺少清单产物事件。'
    Assert-True ($artifactEvents.path -contains $releaseRoot) '游戏分片脚本记录的版本目录不正确。'
    Assert-True ($artifactEvents.path -contains $manifestPath) '游戏分片脚本记录的清单路径不正确。'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) '没有生成 update.json。'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $asset = $manifest.latest.assets[0]
    Assert-True ($asset.fileName -eq 'CrossingVoid.zip') '完整包文件名不兼容启动器。'
    Assert-True ($asset.chunks.Count -gt 1) '测试包没有按测试尺寸切成多个分片。'
    Assert-True ($asset.chunks[0].fileName -eq 'CrossingVoid电脑端.碎片001') '首个分片命名错误。'
    Assert-True ($asset.chunks[0].githubFileName -eq 'CrossingVoid.001') '首个分片的 GitHub 实际附件名错误。'
    Assert-True ($manifest.releaseTag -eq 'PC-V0.5.14') 'PC Release 标签必须使用 PC-V<版本>。'
    Assert-True ($manifest.schemaVersion -eq 2) 'PC 游戏清单没有使用协议 2。'
    Assert-True ($manifest.downloadReleaseTag -eq 'PC-V0.5.14') 'PC 游戏清单缺少下载 Release 标签。'
    Assert-True ($asset.chunks[0].index -eq 1) '首个分片序号应从 1 开始。'
    Assert-True ($asset.chunks[0].count -eq $asset.chunks.Count) '分片总数元数据不一致。'

    $mergedPath = Join-Path $testRoot 'CrossingVoid.rebuilt.zip'
    $merged = [IO.File]::Create($mergedPath)
    try {
        foreach ($chunk in $asset.chunks) {
            $partPath = Join-Path $releaseRoot $chunk.fileName
            Assert-True (Test-Path -LiteralPath $partPath -PathType Leaf) "缺少分片：$($chunk.fileName)"
            $part = Get-Item -LiteralPath $partPath
            Assert-True ($part.Length -eq [int64]$chunk.sizeBytes) "分片大小不一致：$($chunk.fileName)"
            Assert-True ((Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $chunk.sha256) "分片哈希不一致：$($chunk.fileName)"
            $input = [IO.File]::OpenRead($partPath)
            try { $input.CopyTo($merged) } finally { $input.Dispose() }
        }
    }
    finally { $merged.Dispose() }
    Assert-True ((Get-Item -LiteralPath $mergedPath).Length -eq [int64]$asset.sizeBytes) '重组 ZIP 大小不一致。'
    Assert-True ((Get-FileHash -LiteralPath $mergedPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $asset.sha256) '重组 ZIP 哈希不一致。'

    & $bandizip x -y "-o:$extractRoot" $mergedPath | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Bandizip 无法解压重组归档。'
    foreach ($relativePath in $included.Keys) {
        Assert-True (Test-Path -LiteralPath (Join-Path $extractRoot $relativePath) -PathType Leaf) "归档缺少应保留文件：$relativePath"
    }
    foreach ($relativePath in $excluded.Keys) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $extractRoot $relativePath))) "归档包含应排除文件：$relativePath"
    }
    $version = Get-Content -LiteralPath (Join-Path $extractRoot 'CrossingVoid.version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($version.version -eq 'V0.5.14') '生成的版本文件版本错误。'
    $fileManifest = Get-Content -LiteralPath (Join-Path $extractRoot 'CrossingVoid.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($fileManifest.files.path -contains 'CrossingVoid.version.json') '文件清单缺少版本文件。'
    Assert-True (-not ($fileManifest.files.path -contains 'CrossingVoid.manifest.json')) '文件清单不应递归包含自身。'

    foreach ($source in $sourceHashes) {
        Assert-True ((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash -eq $source.Hash) "源文件被修改：$($source.Path)"
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $releaseRoot 'CrossingVoid.zip'))) '成功后仍保留临时完整 ZIP。'

    $androidRoot = Join-Path $testRoot 'Android'
    $androidNestedRoot = Join-Path $androidRoot 'nested'
    New-Item -ItemType Directory -Path $androidNestedRoot -Force | Out-Null
    New-TestApk -Path (Join-Path $androidNestedRoot 'AFS_CrossingVoid-Android-Shipping-arm64.apk')
    New-TestApk -Path (Join-Path $androidNestedRoot 'CrossingVoid-Android-Shipping-arm64.apk') -Abi 'arm64-v8a'
    New-TestApk -Path (Join-Path $androidNestedRoot 'CrossingVoid-Android-Shipping-x86_64.apk') -Abi 'x86_64'
    Set-Content -LiteralPath (Join-Path $androidNestedRoot 'main.512.com.TFAC.CorssingVoid.obb') -Value 'obb' -NoNewline
    & $packageScript -Mode Package -Platform Android -Channel Test -GameDirectory $androidRoot -OutputRoot $outputRoot -ReleaseVersion 'V0.5.12.1' -ReleaseTitle '安卓测试发布' -ChunkSizeBytes 64
    Assert-True ($LASTEXITCODE -eq 0) "Android 分片脚本退出码错误：$LASTEXITCODE"
    $androidManifestPath = Join-Path $outputRoot 'Android-Test-V0.5.12.1\crossingvoid-android-update.json'
    $androidManifest = Get-Content -LiteralPath $androidManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($androidManifest.productKey -eq 'crossingvoid-android-game-test') 'Android 测试服产品标识错误。'
    Assert-True ($androidManifest.schemaVersion -eq 2) 'Android 游戏清单没有使用协议 2。'
    Assert-True ($androidManifest.downloadReleaseTag -eq 'Android-Test-V0.5.12.1') 'Android 游戏清单缺少下载 Release 标签。'
    Assert-True ($androidManifest.latest.assets[0].chunks[0].fileName -eq 'CrossingVoid手机端.碎片001') 'Android 中文分片命名错误。'

    $legacyAndroidManifest = $androidManifest | ConvertTo-Json -Depth 16 | ConvertFrom-Json
    $legacyAndroidManifest.schemaVersion = 1
    $legacyAndroidManifest.PSObject.Properties.Remove('downloadReleaseTag')
    $legacyAndroidManifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $androidManifestPath -Encoding UTF8
    & $packageScript -Mode Upload -Platform Android -Channel Test -GameDirectory $androidRoot -OutputRoot $outputRoot -ReleaseVersion 'V0.5.12.1' -ReleaseTitle '安卓测试发布' -ChunkSizeBytes 64 -DryRun
    Assert-True ($LASTEXITCODE -eq 0) "Android 旧清单迁移演练退出码错误：$LASTEXITCODE"
    $migratedAndroidManifest = Get-Content -LiteralPath $androidManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($migratedAndroidManifest.schemaVersion -eq 2) 'Upload 没有把旧 Android 游戏清单迁移到协议 2。'
    Assert-True ($migratedAndroidManifest.downloadReleaseTag -eq 'Android-Test-V0.5.12.1') 'Upload 迁移后缺少下载 Release 标签。'
    $androidManifest = $migratedAndroidManifest

    $androidPackagePath = Join-Path $testRoot 'CrossingVoid-Android.rebuilt.zip'
    $androidPackage = [IO.File]::Create($androidPackagePath)
    try {
        foreach ($chunk in $androidManifest.latest.assets[0].chunks) {
            $chunkPath = Join-Path (Split-Path -Parent $androidManifestPath) $chunk.fileName
            $chunkInput = [IO.File]::OpenRead($chunkPath)
            try { $chunkInput.CopyTo($androidPackage) } finally { $chunkInput.Dispose() }
        }
    }
    finally { $androidPackage.Dispose() }
    $androidExtractRoot = Join-Path $testRoot 'AndroidExtracted'
    New-Item -ItemType Directory -Path $androidExtractRoot -Force | Out-Null
    & $bandizip x -y "-o:$androidExtractRoot" $androidPackagePath | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Bandizip 无法解压 Android 重组归档。'
    Assert-True (Test-Path -LiteralPath (Join-Path $androidExtractRoot 'CrossingVoid-Android-Shipping-arm64.apk')) 'Android 归档缺少 ARM64 游戏 APK。'
    Assert-True (Test-Path -LiteralPath (Join-Path $androidExtractRoot 'CrossingVoid-Android-Shipping-x86_64.apk')) 'Android 归档缺少 x86_64 游戏 APK。'
    Assert-True (Test-Path -LiteralPath (Join-Path $androidExtractRoot 'main.512.com.TFAC.CorssingVoid.obb')) 'Android 归档缺少 OBB。'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $androidExtractRoot 'AFS_CrossingVoid-Android-Shipping-arm64.apk'))) 'Android 归档不应包含 AFS 辅助 APK。'

    $invalidAndroidRoot = Join-Path $testRoot 'InvalidAndroid'
    New-Item -ItemType Directory -Path $invalidAndroidRoot -Force | Out-Null
    New-TestApk -Path (Join-Path $invalidAndroidRoot 'AFS_First.apk')
    New-TestApk -Path (Join-Path $invalidAndroidRoot 'AFS_Second.apk')
    Set-Content -LiteralPath (Join-Path $invalidAndroidRoot 'main.512.com.TFAC.CorssingVoid.obb') -Value 'obb' -NoNewline
    $invalidOutput = @(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $packageScript -Mode Package -Platform Android -Channel Test -GameDirectory $invalidAndroidRoot -OutputRoot $outputRoot -ReleaseVersion 'V0.5.12.2' -ReleaseTitle '无效安卓测试发布' -ChunkSizeBytes 64 2>&1 |
        ForEach-Object { $_.ToString() })
    $invalidExitCode = $LASTEXITCODE
    Assert-True ($invalidExitCode -ne 0) '没有正式游戏 APK 时 Android 分片脚本应失败。'
    Assert-True (($invalidOutput -join [Environment]::NewLine) -notmatch 'System\.Object\[\]') 'Android 文件诊断仍输出 System.Object[]。'
    Assert-True (($invalidOutput -join [Environment]::NewLine) -match 'AFS_First\.apk') 'Android 文件诊断没有逐行列出 APK 候选。'
    $global:LASTEXITCODE = 0
    Write-Output 'CrossingVoid game package script tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
