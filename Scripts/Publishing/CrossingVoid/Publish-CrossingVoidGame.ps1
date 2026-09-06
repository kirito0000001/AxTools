[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Package','Upload','Publish')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('Windows','Android')][string]$Platform,
    [Parameter(Mandatory)][ValidateSet('Stable','Test')][string]$Channel,
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$ReleaseTitle,
    [string]$ReleaseNotes = '',
    [ValidateRange(1, [long]::MaxValue)][long]$ChunkSizeBytes = [int64](500MB),
    [string]$Bucket = 'download-server-xj',
    [string]$Endpoint = 'https://oss-cn-chengdu.aliyuncs.com',
    [string]$Region = 'cn-chengdu',
    [string]$ServerSshTarget = 'crossing-server',
    [string]$ServerProductsRoot = 'C:\Users\Administrator\Desktop\OSSAPI\ToolboxUpdateServer\app\Data\products',
    [string]$WebsiteGameManifestRoot = 'C:\inetpub\wwwroot\manifests\game',
    [string]$SignatureEndpointBaseUrl = 'https://www.crossingvoid.top/api/toolbox-updates',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Write-GameProgress {
    param([string]$Stage, [double]$Percent, [string]$Message, [string]$Detail = '')
    $payload = [ordered]@{ type='progress'; stage=$Stage; percent=$Percent; message=$Message }
    if (![string]::IsNullOrWhiteSpace($Detail)) { $payload.detail = $Detail }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress -Depth 6))
}

function Write-GameArtifact {
    param([string]$Kind, [string]$Path)
    $payload = [ordered]@{ type='artifact'; kind=$Kind; path=$Path }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress -Depth 6))
}

function Get-ProductKey {
    if ($Platform -eq 'Android') {
        return $(if ($Channel -eq 'Test') { 'crossingvoid-android-game-test' } else { 'crossingvoid-android-game' })
    }
    return $(if ($Channel -eq 'Test') { 'crossingvoid-game-test' } else { 'crossingvoid-game' })
}

function Get-ReleaseTag {
    $prefix = if ($Platform -eq 'Android') { 'Android' } else { 'PC' }
    if ($Channel -eq 'Test') { return "$prefix-Test-$script:Version" }
    return "$prefix-$script:Version"
}

function Get-ReleaseMetadata {
    $platformLabel = if ($Platform -eq 'Android') { 'Android' } else { 'PC' }
    $title = "$script:Version | $platformLabel"
    $defaultBody = @"
## 零境交错 $script:Version

平台：$platformLabel

本版本包含完整游戏资源分片，可由零境启动器自动下载、校验和合并。
"@.Trim()
    $body = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $defaultBody
    } else {
        $ReleaseNotes.Trim()
    }
    return [pscustomobject]@{ Title = $title; Body = $body }
}

function Get-OssutilCommand {
    foreach ($candidate in @('C:\Users\liuyu\Tools\ossutil\ossutil.exe','ossutil.exe','ossutil')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command) { return $command.Source }
    }
    throw '没有找到 ossutil。请先安装并配置阿里云 ossutil。'
}

function Test-IsExcludedWindowsFile {
    param([IO.FileInfo]$File)
    if ($File.Extension -ieq '.pdb' -or $File.Name -ieq 'Manifest_DebugFiles_Win64.txt') { return $true }
    $relative = [IO.Path]::GetRelativePath($script:GameRoot, $File.FullName).Replace('\','/')
    $segments = $relative.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if ($segments | Where-Object { $_ -ieq '.git' -or $_ -ieq '_download' }) { return $true }
    for ($index = 0; $index -lt $segments.Length - 1; $index++) {
        if ($segments[$index] -ieq 'Saved' -and $segments[$index + 1] -in @('Logs','Crashes')) { return $true }
    }
    return $false
}

function Format-AndroidFileCandidates {
    param(
        [IO.FileInfo[]]$ApkFiles,
        [IO.FileInfo[]]$ObbFiles
    )

    $lines = @("检测到 APK：$($ApkFiles.Count)")
    $lines += @($ApkFiles | ForEach-Object { "  $($_.FullName)" })
    $lines += "检测到 OBB：$($ObbFiles.Count)"
    $lines += @($ObbFiles | ForEach-Object { "  $($_.FullName)" })
    return $lines -join [Environment]::NewLine
}

function Get-UnrealGameApkAbis {
    param([Parameter(Mandatory)][IO.FileInfo]$File)

    if ($File.Name.StartsWith('AFS_', [StringComparison]::OrdinalIgnoreCase)) {
        return @()
    }

    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($File.FullName)
        try {
            return @($archive.Entries |
                Where-Object FullName -Match '^lib/([^/]+)/libUnreal\.so$' |
                ForEach-Object {
                    if ($_.FullName -match '^lib/([^/]+)/libUnreal\.so$') { $Matches[1] }
                } |
                Sort-Object -Unique)
        }
        finally {
            $archive.Dispose()
        }
    }
    catch [IO.InvalidDataException] {
        throw "无法读取 APK 文件：$($File.FullName)$([Environment]::NewLine)$($_.Exception.Message)"
    }
}

function Save-GameUpdateManifest {
    param([Parameter(Mandatory)][object]$Manifest)

    $Manifest |
        ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath $script:ManifestPath -Encoding UTF8
}

function Get-InputFiles {
    $allFiles = @(Get-ChildItem -LiteralPath $script:GameRoot -Recurse -File -Force)
    if ($Platform -eq 'Android') {
        $allApk = @($allFiles | Where-Object Extension -IEQ '.apk')
        $allObb = @($allFiles | Where-Object Extension -IEQ '.obb')
        $apkInfo = @($allApk | ForEach-Object {
            $abis = @(Get-UnrealGameApkAbis -File $_)
            if ($abis.Count -gt 0) {
                [pscustomobject]@{ File = $_; Abis = $abis }
            }
        })
        if ($apkInfo.Count -eq 0) {
            $detail = Format-AndroidFileCandidates -ApkFiles $allApk -ObbFiles $allObb
            throw "Android 打包目录没有找到包含 libUnreal.so 的正式游戏 APK。$([Environment]::NewLine)$detail"
        }

        $abiOwners = @{}
        foreach ($info in $apkInfo) {
            foreach ($abi in $info.Abis) {
                if ($abiOwners.ContainsKey($abi)) {
                    throw "Android 打包目录中 ABI $abi 对应多个正式游戏 APK，无法自动选择。$([Environment]::NewLine)  $($abiOwners[$abi])$([Environment]::NewLine)  $($info.File.FullName)"
                }
                $abiOwners[$abi] = $info.File.FullName
            }
        }

        $mainObb = @($allObb | Where-Object Name -Match '^main\.([0-9]+)\.(.+)\.obb$')
        if ($mainObb.Count -ne 1) {
            $detail = Format-AndroidFileCandidates -ApkFiles $allApk -ObbFiles $allObb
            throw "Android 打包目录必须有且只有一个 main.<版本号>.<包名>.obb；可额外包含匹配的 patch OBB。$([Environment]::NewLine)$detail"
        }
        $null = $mainObb[0].Name -match '^main\.([0-9]+)\.(.+)\.obb$'
        $obbVersion = $Matches[1]
        $obbPackage = $Matches[2]
        $matchingObbPattern = "^(main|patch)\.$([regex]::Escape($obbVersion))\.$([regex]::Escape($obbPackage))\.obb$"
        $matchingObb = @($allObb | Where-Object Name -Match $matchingObbPattern)

        $selectedFiles = @(@($apkInfo.File) + $matchingObb)
        $duplicateNames = @($selectedFiles | Group-Object Name | Where-Object Count -GT 1)
        if ($duplicateNames.Count -gt 0) {
            $detail = @($duplicateNames | ForEach-Object {
                "文件名 $($_.Name)：$([Environment]::NewLine)$(@($_.Group.FullName | ForEach-Object { "  $_" }) -join [Environment]::NewLine)"
            }) -join [Environment]::NewLine
            throw "Android 待发布文件存在同名冲突，无法放入同一归档。$([Environment]::NewLine)$detail"
        }
        return $selectedFiles
    }
    return @($allFiles | Where-Object { !(Test-IsExcludedWindowsFile $_) })
}

function New-GamePackage {
    Write-GameProgress 'cleanup' 1 '正在清理旧的本地游戏分片' $script:ReleaseRoot
    if (Test-Path -LiteralPath $script:ReleaseRoot) {
        Remove-Item -LiteralPath $script:ReleaseRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $script:StagingRoot -Force | Out-Null
    Write-GameProgress 'inspect' 3 "正在检查 $Platform 游戏包" $script:GameRoot
    $files = @(Get-InputFiles)
    if ($files.Count -eq 0) { throw '游戏打包目录没有可发布文件。' }

    $position = 0
    foreach ($file in $files) {
        $position++
        $relative = if ($Platform -eq 'Android') {
            $file.Name
        } else {
            [IO.Path]::GetRelativePath($script:GameRoot, $file.FullName)
        }
        $target = Join-Path $script:StagingRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        Write-GameProgress 'scan' (5 + 15 * $position / $files.Count) "正在整理文件 $position / $($files.Count)" $relative
    }

    [ordered]@{ version=$script:Version; platform=$Platform; channel=$Channel } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:StagingRoot 'CrossingVoid.version.json') -Encoding UTF8
    $stagedFiles = @(Get-ChildItem -LiteralPath $script:StagingRoot -Recurse -File -Force | Sort-Object FullName)
    $manifestFiles = @()
    $position = 0
    foreach ($file in $stagedFiles) {
        $position++
        $relative = [IO.Path]::GetRelativePath($script:StagingRoot, $file.FullName).Replace('\','/')
        $manifestFiles += [ordered]@{
            path=$relative
            sizeBytes=$file.Length
            sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        Write-GameProgress 'hash-files' (20 + 15 * $position / $stagedFiles.Count) "正在生成文件校验 $position / $($stagedFiles.Count)" $relative
    }
    [ordered]@{ schemaVersion=1; productKey=(Get-ProductKey); runtime=$Platform; version=$script:Version; files=$manifestFiles } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:StagingRoot 'CrossingVoid.manifest.json') -Encoding UTF8

    Write-GameProgress 'archive' 38 '正在压缩游戏包'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $script:StagingRoot,
        $script:ArchivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false,
        [Text.Encoding]::UTF8)
    $archive = Get-Item -LiteralPath $script:ArchivePath
    $archiveHash = (Get-FileHash -LiteralPath $script:ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $chunkCount = [int][Math]::Ceiling($archive.Length / [double]$ChunkSizeBytes)
    $chunks = @()
    $input = [IO.File]::OpenRead($script:ArchivePath)
    try {
        for ($index = 1; $index -le $chunkCount; $index++) {
            $name = "$script:ChunkPrefix$($index.ToString('D3'))"
            $path = Join-Path $script:ReleaseRoot $name
            $remaining = [Math]::Min($ChunkSizeBytes, $input.Length - $input.Position)
            $output = [IO.File]::Create($path)
            $buffer = [byte[]]::new(8MB)
            try {
                while ($remaining -gt 0) {
                    $read = $input.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($read -le 0) { throw "读取分片 $index 时意外结束。" }
                    $output.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            } finally { $output.Dispose() }
            $part = Get-Item -LiteralPath $path
            $objectKey = "Akege304/CrossingVoid/channels/$($Channel.ToLowerInvariant())/$Platform/releases/$script:SafeVersion/$name"
            $githubFileName = if ($Platform -eq 'Windows') {
                "CrossingVoid.$($index.ToString('D3'))"
            } else {
                $name
            }
            $chunks += [ordered]@{
                index=$index; count=$chunkCount; fileName=$name; githubFileName=$githubFileName; objectKey=$objectKey
                sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                sizeBytes=$part.Length; contentType='application/octet-stream'
            }
            Write-GameProgress 'split' (50 + 35 * $index / $chunkCount) "正在切分并校验第 $index / $chunkCount 片" $name
        }
    } finally { $input.Dispose() }

    $archiveObjectKey = "Akege304/CrossingVoid/channels/$($Channel.ToLowerInvariant())/$Platform/releases/$script:SafeVersion/$script:ArchiveName"
    $asset = [ordered]@{
        runtime=$Platform; fileName=$script:ArchiveName; objectKey=$archiveObjectKey
        sha256=$archiveHash; sizeBytes=$archive.Length; contentType='application/zip'; chunks=$chunks
    }
    $update = [ordered]@{
        schemaVersion=2; productKey=(Get-ProductKey); releaseTag=(Get-ReleaseTag); downloadReleaseTag=(Get-ReleaseTag)
        latest=[ordered]@{ version=$script:Version; channel=$Channel.ToLowerInvariant(); title=(Get-ReleaseMetadata).Title; publishedAt=[DateTimeOffset]::UtcNow.ToString('o'); assets=@($asset) }
    }
    Save-GameUpdateManifest -Manifest $update
    Remove-Item -LiteralPath $script:ArchivePath -Force
    Remove-Item -LiteralPath $script:StagingRoot -Recurse -Force
    Assert-ReleaseArtifacts | Out-Null
    Write-GameArtifact 'game-package' $script:ReleaseRoot
    Write-GameArtifact 'manifest' $script:ManifestPath
    Write-GameProgress 'packaged' 100 '游戏分片制作完成' $script:ReleaseRoot
}

function Assert-ReleaseArtifacts {
    if (!(Test-Path -LiteralPath $script:ManifestPath -PathType Leaf)) { throw "缺少本地游戏清单：$script:ManifestPath" }
    $manifest = Get-Content -LiteralPath $script:ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest.schemaVersion = 2
    if ($manifest.PSObject.Properties.Name -contains 'downloadReleaseTag') {
        $manifest.downloadReleaseTag = Get-ReleaseTag
    }
    else {
        $manifest | Add-Member -NotePropertyName downloadReleaseTag -NotePropertyValue (Get-ReleaseTag)
    }
    Save-GameUpdateManifest -Manifest $manifest
    if ([string]$manifest.productKey -ne (Get-ProductKey)) { throw '本地游戏清单的平台或通道不匹配。' }
    if ([string]$manifest.latest.version -ne $script:Version) { throw '本地游戏清单版本不匹配。' }
    $asset = @($manifest.latest.assets | Where-Object runtime -EQ $Platform | Select-Object -First 1)
    if ($asset.Count -ne 1) { throw "本地游戏清单缺少 $Platform 资源。" }
    $chunks = @($asset[0].chunks | Sort-Object index)
    if ($chunks.Count -eq 0) { throw '本地游戏清单没有分片。' }
    $total = [int64]0
    foreach ($chunk in $chunks) {
        $path = Join-Path $script:ReleaseRoot ([string]$chunk.fileName)
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少分片：$($chunk.fileName)" }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [int64]$chunk.sizeBytes) { throw "分片大小已变化：$($chunk.fileName)" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne [string]$chunk.sha256) { throw "分片 SHA-256 已变化：$($chunk.fileName)" }
        $total += $file.Length
    }
    if ($total -ne [int64]$asset[0].sizeBytes) { throw '全部分片总大小与完整包不一致。' }
    return [pscustomobject]@{ Manifest=$manifest; Asset=$asset[0]; Chunks=$chunks }
}

function Get-GiteeToken {
    foreach ($name in @('CROSSINGVOID_GITEE_TOKEN','AXTOOLS_GITEE_TOKEN','FANTASYTOOLS_GITEE_TOKEN','GITEE_TOKEN','GITEE_ACCESS_TOKEN')) {
        foreach ($scope in @('Process','User','Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if (![string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        }
    }
    throw '未找到 Gitee 访问令牌。'
}

function Publish-GiteeManifest {
    param([object]$Manifest)
    $repository = if ($Platform -eq 'Android') { 'xiaojie578/CrossingVoid-Downloader-Android' } else { 'xiaojie578/CrossingVoid-Downloader-PC' }
    $baseName = if ($Platform -eq 'Android') { 'android' } else { 'windows' }
    $repositoryPath = if ($Channel -eq 'Test') { "game/$baseName-test-latest.json" } else { "game/$baseName-latest.json" }
    if ($DryRun) {
        Write-Host "DryRun：跳过 Gitee 清单发布：$repository/$repositoryPath"
        return
    }
    $token = Get-GiteeToken
    $escapedPath = ($repositoryPath -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    $baseUri = "https://gitee.com/api/v5/repos/$repository/contents/$escapedPath"
    $current = $null
    try { $current = Invoke-RestMethod -Method Get -Uri "$baseUri`?access_token=$([uri]::EscapeDataString($token))&ref=master" }
    catch { if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw } }
    $json = $Manifest | ConvertTo-Json -Depth 16
    $body = @{
        access_token=$token; branch='master'; message="Update $Platform $Channel game metadata to $script:Version"
        content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    }
    if ($null -ne $current -and ![string]::IsNullOrWhiteSpace([string]$current.sha)) {
        $body.sha = [string]$current.sha
        Invoke-RestMethod -Method Put -Uri $baseUri -ContentType 'application/x-www-form-urlencoded; charset=utf-8' -Body $body | Out-Null
    } else {
        Invoke-RestMethod -Method Post -Uri $baseUri -ContentType 'application/x-www-form-urlencoded; charset=utf-8' -Body $body | Out-Null
    }
}

function Get-OssChunkSize {
    param(
        [string]$OssutilPath,
        [object]$Chunk,
        [switch]$AllowMissing
    )

    $ossUri = "oss://$Bucket/$($Chunk.objectKey)"
    $output = @(& $OssutilPath stat $ossUri -e $Endpoint --region $Region 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        if ($AllowMissing) { return $null }
        throw "OSS 远端对象读取失败：$($Chunk.fileName)，ossutil exit code $LASTEXITCODE$([Environment]::NewLine)$($output -join [Environment]::NewLine)"
    }

    $match = [regex]::Match(
        ($output -join [Environment]::NewLine),
        '(?im)^\s*Content-Length\s*:\s*(\d+)\s*$')
    if (!$match.Success) {
        throw "OSS stat 没有返回 Content-Length：$($Chunk.fileName)$([Environment]::NewLine)$($output -join [Environment]::NewLine)"
    }

    return [int64]$match.Groups[1].Value
}

function Assert-OssChunkSize {
    param(
        [string]$OssutilPath,
        [object]$Chunk
    )

    $remoteSize = Get-OssChunkSize -OssutilPath $OssutilPath -Chunk $Chunk
    if ($remoteSize -ne [int64]$Chunk.sizeBytes) {
        throw "OSS 分片大小核对失败：$($Chunk.fileName)，预期 $($Chunk.sizeBytes)，实际 $remoteSize。"
    }
}

function Publish-ServerManifest {
    param(
        [string]$ManifestPath,
        [string]$ExpectedProductKey,
        [string]$ExpectedVersion
    )

    if (!(Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "服务器白名单清单不存在：$ManifestPath"
    }
    if ([string]::IsNullOrWhiteSpace($ServerSshTarget)) {
        throw '服务器 SSH 目标不能为空。'
    }
    if ([string]::IsNullOrWhiteSpace($ServerProductsRoot)) {
        throw '服务器产品清单根目录不能为空。'
    }
    if ($ExpectedProductKey -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw "服务器产品键不安全：$ExpectedProductKey"
    }

    $remoteTargetPath = "$($ServerProductsRoot.TrimEnd('\'))\$ExpectedProductKey\update.json"
    $remoteTempPath = "C:\Windows\Temp\$ExpectedProductKey-update.$([Guid]::NewGuid().ToString('N')).json"
    & scp $ManifestPath "${ServerSshTarget}:$remoteTempPath"
    if ($LASTEXITCODE -ne 0) {
        throw "上传服务器白名单清单失败：$ExpectedProductKey，scp exit code $LASTEXITCODE"
    }

    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$temp = '$remoteTempPath'
`$target = '$remoteTargetPath'
`$expectedProductKey = '$ExpectedProductKey'
`$expectedVersion = '$ExpectedVersion'
try {
    if (!(Test-Path -LiteralPath `$temp -PathType Leaf)) {
        throw "临时清单不存在：`$temp"
    }
    `$parsed = Get-Content -LiteralPath `$temp -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]`$parsed.productKey -ne `$expectedProductKey) {
        throw "productKey 不正确：`$(`$parsed.productKey)"
    }
    if ([string]`$parsed.latest.version -ne `$expectedVersion) {
        throw "latest.version 不正确：`$(`$parsed.latest.version)"
    }
    `$assets = @(`$parsed.latest.assets)
    if (`$assets.Count -lt 1) {
        throw 'latest.assets 为空'
    }
    `$chunks = @(`$assets | ForEach-Object { @(`$_.chunks) })
    if (`$chunks.Count -lt 1) {
        throw 'latest.assets.chunks 为空'
    }
    foreach (`$chunk in `$chunks) {
        if ([string]::IsNullOrWhiteSpace([string]`$chunk.objectKey) -or [int64]`$chunk.sizeBytes -le 0) {
            throw "分片白名单无效：`$(`$chunk.fileName)"
        }
    }
    `$targetDir = Split-Path -Parent `$target
    New-Item -ItemType Directory -Path `$targetDir -Force | Out-Null
    if (Test-Path -LiteralPath `$target -PathType Leaf) {
        `$backup = "`$target.bak-`$(Get-Date -Format 'yyyyMMddHHmmss')"
        Copy-Item -LiteralPath `$target -Destination `$backup -Force
    }
    Move-Item -LiteralPath `$temp -Destination `$target -Force
    `$published = Get-Content -LiteralPath `$target -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]`$published.productKey -ne `$expectedProductKey -or
        [string]`$published.latest.version -ne `$expectedVersion) {
        throw '服务器清单写入后复核失败'
    }
    [ordered]@{
        success = `$true
        productKey = `$published.productKey
        version = `$published.latest.version
        chunks = @(`$published.latest.assets | ForEach-Object { @(`$_.chunks) }).Count
    } | ConvertTo-Json -Compress
}

catch {
    Remove-Item -LiteralPath `$temp -Force -ErrorAction SilentlyContinue
    throw
}
"@

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    $output = @(& ssh $ServerSshTarget "powershell -NoProfile -EncodedCommand $encoded" 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "服务器白名单清单发布失败：$ExpectedProductKey，ssh exit code $LASTEXITCODE$([Environment]::NewLine)$($output -join [Environment]::NewLine)"
    }
    $resultLine = @($output | Where-Object {
        $_.TrimStart().StartsWith('{') -and $_.TrimEnd().EndsWith('}')
    } | Select-Object -Last 1)
    if ($resultLine.Count -ne 1) {
        throw "服务器白名单清单没有返回有效结果：$ExpectedProductKey$([Environment]::NewLine)$($output -join [Environment]::NewLine)"
    }
    $result = $resultLine[0] | ConvertFrom-Json
    if (!$result.success -or [string]$result.productKey -ne $ExpectedProductKey -or [string]$result.version -ne $ExpectedVersion) {
        throw "服务器白名单清单发布结果无效：$ExpectedProductKey"
    }
}

function Publish-PublicGameManifest {
    param(
        [string]$ManifestPath,
        [string]$ExpectedProductKey,
        [string]$ExpectedVersion
    )

    $platformName = if ($Platform -eq 'Android') { 'android' } else { 'windows' }
    $fileName = if ($Platform -eq 'Android') {
        $(if ($Channel -eq 'Test') { 'android-test-latest.json' } else { 'android-latest.json' })
    }
    else {
        $(if ($Channel -eq 'Test') { 'windows-test-latest.json' } else { 'windows-latest.json' })
    }
    $remoteTargetPath = "$($WebsiteGameManifestRoot.TrimEnd('\'))\$fileName"
    $remoteTempPath = "C:\Windows\Temp\crossingvoid-$platformName-$Channel-$([Guid]::NewGuid().ToString('N')).json"
    & scp $ManifestPath "${ServerSshTarget}:$remoteTempPath"
    if ($LASTEXITCODE -ne 0) { throw "上传官网游戏清单失败：$fileName，scp exit code $LASTEXITCODE" }

    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$temp = '$remoteTempPath'
`$target = '$remoteTargetPath'
try {
    `$manifest = Get-Content -LiteralPath `$temp -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]`$manifest.schemaVersion -ne 2 -or
        [string]`$manifest.productKey -ne '$ExpectedProductKey' -or
        [string]`$manifest.latest.version -ne '$ExpectedVersion') {
        throw '官网游戏清单内容不匹配'
    }
    `$targetDir = Split-Path -Parent `$target
    New-Item -ItemType Directory -Path `$targetDir -Force | Out-Null
    if (Test-Path -LiteralPath `$target -PathType Leaf) {
        Copy-Item -LiteralPath `$target -Destination "`$target.bak" -Force
    }
    Move-Item -LiteralPath `$temp -Destination `$target -Force
    & icacls.exe `$target /reset | Out-Null
    if (`$LASTEXITCODE -ne 0) { throw '无法恢复官网清单的 IIS 读取权限' }
}
catch {
    Remove-Item -LiteralPath `$temp -Force -ErrorAction SilentlyContinue
    throw
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    & ssh $ServerSshTarget "powershell -NoProfile -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) { throw "发布官网游戏清单失败：$fileName，ssh exit code $LASTEXITCODE" }
}

function Assert-ServerChunkSignatures {
    param(
        [object[]]$Chunks,
        [string]$ProductKey,
        [string]$Version,
        [string]$Runtime
    )

    if ([string]::IsNullOrWhiteSpace($SignatureEndpointBaseUrl)) {
        throw '签名接口地址不能为空。'
    }
    $endpoint = "$($SignatureEndpointBaseUrl.Trim().TrimEnd('/'))/sign-download"
    $index = 0
    foreach ($chunk in $Chunks) {
        $index++
        Write-GameProgress 'verify-signature-chunk' (92 + 4 * $index / $Chunks.Count) `
            "正在验证服务器签名：第 $index / $($Chunks.Count) 片" ([string]$chunk.fileName)
        $body = [ordered]@{
            productKey = $ProductKey
            version = $Version
            runtime = $Runtime
            objectKey = [string]$chunk.objectKey
            clientId = 'axtools-publish-check'
            launcherVersion = '999.0.0'
        } | ConvertTo-Json -Compress
        try {
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri $endpoint `
                -ContentType 'application/json; charset=utf-8' `
                -Body $body
        }
        catch {
            throw "服务器拒绝分片签名：$($chunk.fileName)$([Environment]::NewLine)$($_.Exception.Message)"
        }
        if (!$response.success -or [string]::IsNullOrWhiteSpace([string]$response.url)) {
            throw "服务器没有返回有效签名地址：$($chunk.fileName)"
        }
        if ([int64]$response.sizeBytes -ne [int64]$chunk.sizeBytes) {
            throw "服务器签名结果大小不一致：$($chunk.fileName)，预期 $($chunk.sizeBytes)，实际 $($response.sizeBytes)。"
        }
    }
}

function Send-GamePackage {
    $verified = Assert-ReleaseArtifacts
    Write-GameProgress 'verify-upload' 5 '待上传分片校验完成' "$($verified.Chunks.Count) 片"
    if ($DryRun) {
        Write-GameProgress 'dry-run' 95 '上传演练完成，未写入 GitHub、OSS、服务器或 Gitee'
        return
    }
    $gh = (Get-Command gh.exe -ErrorAction SilentlyContinue) ?? (Get-Command gh -ErrorAction Stop)
    $ossutil = Get-OssutilCommand
    $tag = Get-ReleaseTag
    $repository = 'kirito0000001/CrossingVoid'
    $releaseMetadata = Get-ReleaseMetadata
    $existingRelease = & $gh.Source release view $tag --repo $repository --json tagName 2>$null
    if ($LASTEXITCODE -ne 0) {
        & $gh.Source release create $tag --repo $repository --title $releaseMetadata.Title --notes $releaseMetadata.Body
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release 创建失败：$tag" }
    }
    else {
        & $gh.Source release edit $tag --repo $repository --title $releaseMetadata.Title --notes $releaseMetadata.Body
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release 标题或说明更新失败：$tag" }
    }

    $releaseJson = & $gh.Source release view $tag --repo $repository --json assets
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release 附件列表读取失败：$tag" }
    $oldAssets = @((ConvertFrom-Json ($releaseJson -join [Environment]::NewLine)).assets)
    $matchedRemoteNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $missingChunks = @()
    foreach ($chunk in $verified.Chunks) {
        $expectedDigest = "sha256:$($chunk.sha256)"
        $match = @($oldAssets | Where-Object {
            !$matchedRemoteNames.Contains([string]$_.name) -and
            [int64]$_.size -eq [int64]$chunk.sizeBytes -and
            [string]$_.digest -ieq $expectedDigest
        } | Select-Object -First 1)
        if ($match.Count -eq 1) {
            $matchedRemoteNames.Add([string]$match[0].name) | Out-Null
        }
        else {
            $missingChunks += $chunk
        }
    }

    $staleAssets = @($oldAssets | Where-Object {
        !$matchedRemoteNames.Contains([string]$_.name)
    })
    Write-GameProgress 'resume-github' 8 'GitHub 分片断点检查完成' `
        "保留 $($matchedRemoteNames.Count) 片；删除 $($staleAssets.Count) 个旧附件；补传 $($missingChunks.Count) 片"
    if ($missingChunks.Count -gt 0) {
        $index = 0
        foreach ($chunk in $missingChunks) {
            $index++
            $path = Join-Path $script:ReleaseRoot ([string]$chunk.fileName)
            Write-GameProgress 'upload-github' (10 + 35 * $index / $missingChunks.Count) `
                "正在续传 GitHub：第 $index / $($missingChunks.Count) 片" ([string]$chunk.fileName)
            & $gh.Source release upload $tag $path --repo $repository --clobber
            if ($LASTEXITCODE -ne 0) { throw "GitHub 分片上传失败：$($chunk.fileName)" }
        }
    }
    $remoteJson = & $gh.Source release view $tag --repo $repository --json assets
    if ($LASTEXITCODE -ne 0) { throw "GitHub 上传结果读取失败：$tag" }
    $remoteAssets = @((ConvertFrom-Json ($remoteJson -join [Environment]::NewLine)).assets)
    $verifiedRemoteNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($chunk in $verified.Chunks) {
        $expectedDigest = "sha256:$($chunk.sha256)"
        $remote = @($remoteAssets | Where-Object {
            !$verifiedRemoteNames.Contains([string]$_.name) -and
            [int64]$_.size -eq [int64]$chunk.sizeBytes -and
            [string]$_.digest -ieq $expectedDigest
        } | Select-Object -First 1)
        if ($remote.Count -ne 1) {
            throw "GitHub 附件核对失败：$($chunk.fileName)"
        }
        $verifiedRemoteNames.Add([string]$remote[0].name) | Out-Null
        if (![string]::Equals(
            [string]$remote[0].name,
            [string]$chunk.githubFileName,
            [StringComparison]::Ordinal)) {
            Write-GameProgress 'verify-github' 44 'GitHub 实际附件名不一致，已按远端修正清单' `
                "$($chunk.githubFileName) -> $($remote[0].name)"
        }
        $chunk.githubFileName = [string]$remote[0].name
        if (![string]::Equals(
            [string]$remote[0].name,
            [string]$chunk.fileName,
            [StringComparison]::Ordinal)) {
            Write-GameProgress 'verify-github' 44 'GitHub 已调整附件名称，内容校验通过' `
                "$($chunk.fileName) -> $($remote[0].name)"
        }
    }
    $remainingStaleAssets = @($remoteAssets | Where-Object {
        !$verifiedRemoteNames.Contains([string]$_.name)
    })
    Write-GameProgress 'verify-github' 45 'GitHub 全部分片核对完成' "$($verified.Chunks.Count) 片"
    Save-GameUpdateManifest -Manifest $verified.Manifest
    $index = 0
    foreach ($chunk in $verified.Chunks) {
        $index++
        $path = Join-Path $script:ReleaseRoot ([string]$chunk.fileName)
        $existingOssSize = Get-OssChunkSize -OssutilPath $ossutil -Chunk $chunk -AllowMissing
        if ($null -ne $existingOssSize -and [int64]$existingOssSize -eq [int64]$chunk.sizeBytes) {
            Write-GameProgress 'resume-oss' (45 + 30 * $index / $verified.Chunks.Count) `
                'OSS 分片已存在，跳过上传' ([string]$chunk.fileName)
        }
        else {
            Write-GameProgress 'upload-oss' (45 + 30 * $index / $verified.Chunks.Count) "正在上传到 OSS：第 $index / $($verified.Chunks.Count) 片" ([string]$chunk.fileName)
            & $ossutil cp $path "oss://$Bucket/$($chunk.objectKey)" -e $Endpoint --region $Region -f
            if ($LASTEXITCODE -ne 0) { throw "OSS 分片上传失败：$($chunk.fileName)" }
        }
    }
    $index = 0
    foreach ($chunk in $verified.Chunks) {
        $index++
        Write-GameProgress 'verify-oss' (75 + 12 * $index / $verified.Chunks.Count) `
            "正在核对 OSS：第 $index / $($verified.Chunks.Count) 片" ([string]$chunk.fileName)
        Assert-OssChunkSize -OssutilPath $ossutil -Chunk $chunk
    }
    Write-GameProgress 'server-manifest' 92 '正在同步服务器下载白名单' (Get-ProductKey)
    Publish-ServerManifest `
        -ManifestPath $script:ManifestPath `
        -ExpectedProductKey (Get-ProductKey) `
        -ExpectedVersion $script:Version
    Write-GameProgress 'public-manifest' 93 '正在更新官网游戏版本清单' $Platform
    Publish-PublicGameManifest `
        -ManifestPath $script:ManifestPath `
        -ExpectedProductKey (Get-ProductKey) `
        -ExpectedVersion $script:Version
    Write-GameProgress 'verify-signatures' 92 '正在验证全部 OSS 分片签名权限' "$($verified.Chunks.Count) 片"
    Assert-ServerChunkSignatures `
        -Chunks $verified.Chunks `
        -ProductKey (Get-ProductKey) `
        -Version $script:Version `
        -Runtime $Platform
    foreach ($staleAsset in $remainingStaleAssets) {
        & $gh.Source release delete-asset $tag ([string]$staleAsset.name) --repo $repository --yes
        if ($LASTEXITCODE -ne 0) { throw "GitHub 旧附件删除失败：$($staleAsset.name)" }
    }
    $finalRemoteJson = & $gh.Source release view $tag --repo $repository --json assets
    if ($LASTEXITCODE -ne 0) { throw "GitHub 最终附件列表读取失败：$tag" }
    $finalRemoteAssets = @((ConvertFrom-Json ($finalRemoteJson -join [Environment]::NewLine)).assets)
    if ($finalRemoteAssets.Count -ne $verified.Chunks.Count) {
        throw "GitHub 最终附件数量不一致：预期 $($verified.Chunks.Count)，实际 $($finalRemoteAssets.Count)。"
    }
    foreach ($chunk in $verified.Chunks) {
        $expectedDigest = "sha256:$($chunk.sha256)"
        $remote = @($finalRemoteAssets | Where-Object {
            [int64]$_.size -eq [int64]$chunk.sizeBytes -and
            [string]$_.digest -ieq $expectedDigest
        })
        if ($remote.Count -ne 1) {
            throw "GitHub 最终附件核对失败：$($chunk.fileName)"
        }
    }
    Write-GameProgress 'gitee-manifest' 97 '正在发布 Gitee 游戏校验清单'
    Publish-GiteeManifest -Manifest $verified.Manifest
    Write-GameProgress 'published' 100 '游戏发布完成'
}

if (!(Test-Path -LiteralPath $GameDirectory -PathType Container)) { throw "游戏打包目录不存在：$GameDirectory" }
if ($ReleaseVersion -notmatch '^V?\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.-]+)?$') { throw "游戏版本号格式不正确：$ReleaseVersion" }
$script:Version = if ($ReleaseVersion.StartsWith('V')) { $ReleaseVersion } else { "V$ReleaseVersion" }
$script:SafeVersion = $script:Version -replace '[^A-Za-z0-9.-]', '_'
$script:GameRoot = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if ($resolvedOutput.StartsWith($script:GameRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $script:GameRoot.StartsWith($resolvedOutput + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '游戏包目录和输出目录不能互相包含。'
}
$script:ReleaseRoot = Join-Path $resolvedOutput "$Platform-$Channel-$script:SafeVersion"
$script:StagingRoot = Join-Path $script:ReleaseRoot 'staging'
$script:ArchiveName = if ($Platform -eq 'Android') { 'CrossingVoid-Android-Package.zip' } else { 'CrossingVoid.zip' }
$script:ArchivePath = Join-Path $script:ReleaseRoot $script:ArchiveName
$script:ChunkPrefix = if ($Platform -eq 'Android') { 'CrossingVoid手机端.碎片' } else { 'CrossingVoid电脑端.碎片' }
$script:ManifestPath = Join-Path $script:ReleaseRoot $(if ($Platform -eq 'Android') { 'crossingvoid-android-update.json' } else { 'CrossingVoid-PC-update.json' })

switch ($Mode) {
    'Package' { New-GamePackage }
    'Upload' { Send-GamePackage }
    'Publish' { New-GamePackage; Send-GamePackage }
}
exit 0
