[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePackageRoot,
    [Parameter(Mandatory)][string]$ProductSegment,
    [string]$Channel = 'Stable',
    [string]$GitHubRepo = '',
    [string]$Version = '',
    [string]$VersionManifestPath = '',
    [string]$BaseManifestUrl = '',
    [string]$DownloadTarget = 'yecaoyun-hk',
    [string]$DownloadRemoteRoot = '/srv/downloads/games',
    [string]$DownloadPublicBaseUrl = 'https://dl.crossingvoid.top',
    [string]$GiteeRepo = 'xiaojie578/CrossingVoid-patch-transfer',
    [string]$FragmentOutputDirectory = '',
    [long]$MaxFragmentBytes = 1GB,
    [switch]$FragmentsOnly,
    [switch]$UseGiteeTransfer,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force

function Assert-NotCancelled {
    if (Test-AxTaskCancellation) {
        throw [OperationCanceledException]::new('用户已停止 Windows 补丁发布。')
    }
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function New-PatchFragments {
    param(
        [Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][string]$ProductSegment,
        [long]$SliceBytes = 1GB
    )

    $shortName = switch ($ProductSegment.ToLowerInvariant()) {
        'crossingvoid' { '零境' }
        'fantasyproject' { '幻杀' }
        default { $ProductSegment }
    }
    $outputDirectory = Join-Path $OutputRoot ("补丁V$Version")
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $prefix = "${shortName}V${Version}PC补丁碎片"
    $slices = [System.Collections.Generic.List[object]]::new()
    $current = [System.Collections.Generic.List[object]]::new()
    $currentBytes = [int64]0
    foreach ($entry in ($Entries | Sort-Object Path)) {
        Assert-NotCancelled
        $size = [int64]$entry.SizeBytes
        if ($current.Count -gt 0 -and ($currentBytes + $size) -gt $SliceBytes) {
            $slices.Add([pscustomobject]@{ Entries = @($current); Bytes = $currentBytes })
            $current = [System.Collections.Generic.List[object]]::new()
            $currentBytes = [int64]0
        }
        $current.Add($entry)
        $currentBytes += $size
    }
    if ($current.Count -gt 0) {
        $slices.Add([pscustomobject]@{ Entries = @($current); Bytes = $currentBytes })
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archives = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($slice in $slices) {
        $index++
        Assert-NotCancelled
        $fileName = "{0}{1:D3}.zip" -f $prefix, $index
        $zipPath = Join-Path $outputDirectory $fileName
        $zip = [System.IO.Compression.ZipFile]::Open(
            $zipPath,
            [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $slice.Entries) {
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip,
                    $entry.SourcePath,
                    $entry.Path,
                    [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $zip.Dispose()
        }
        $item = Get-Item -LiteralPath $zipPath
        $archives.Add([ordered]@{
            fileName = $fileName
            sizeBytes = [int64]$item.Length
            fileCount = @($slice.Entries).Count
        })
        Write-AxTaskProgress `
            -Stage 'fragments' `
            -Percent ([Math]::Min(99, 100 * $index / [Math]::Max(1, $slices.Count))) `
            -Message ("已生成补丁碎片 {0}/{1}：{2}" -f $index, $slices.Count, $fileName) `
            -Detail (Format-Size -Bytes $item.Length)
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        kind = 'patch-fragments'
        productSegment = $ProductSegment
        platform = 'Windows'
        version = $Version
        baseVersion = $BaseVersion
        generatedAt = [DateTimeOffset]::Now.ToString('o')
        slices = @($archives)
        files = @($Entries | ForEach-Object {
            [ordered]@{ path = $_.Path; sizeBytes = $_.SizeBytes; sha256 = $_.Sha256 }
        })
    }
    $manifestPath = Join-Path $outputDirectory 'patch-fragments.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        OutputDirectory = $outputDirectory
        ArchiveCount = $archives.Count
        FileCount = @($Entries).Count
        ManifestPath = $manifestPath
    }
}

function Read-BuildManifest {
    param([string]$PackageRoot, [string]$ExplicitPath = '')
    $root = [IO.Path]::GetFullPath($PackageRoot)
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }
    $candidates += @(
        (Join-Path $root 'Version.json'),
        (Join-Path (Split-Path -Parent $root) 'Version-Windows.json'),
        (Join-Path (Split-Path -Parent $root) 'Version.json')
    ) | Select-Object -Unique
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [pscustomobject]@{
                Path = $candidate
                Root = Split-Path -Parent $candidate
                Data = (Get-Content -LiteralPath $candidate -Raw -Encoding UTF8 | ConvertFrom-Json)
            }
        }
    }
    throw "找不到打包插件 Version.json：$PackageRoot"
}

function Invoke-Gh {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & gh @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{
        ExitCode = $code
        Lines = @($output | ForEach-Object { [string]$_ })
    }
}

function Ensure-Release {
    param([string]$Repository, [string]$Tag, [string]$Title, [string]$Notes)
    $view = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $Repository)
    if ($view.ExitCode -eq 0) { return }
    $created = Invoke-Gh -Arguments @(
        'release', 'create', $Tag, '--repo', $Repository,
        '--title', $Title, '--notes', $Notes)
    if ($created.ExitCode -ne 0) {
        throw ("GitHub Release 创建失败（{0}）：{1}" -f $Tag, (($created.Lines | Out-String).Trim()))
    }
}

function Invoke-DownloadHostSsh {
    param([Parameter(Mandatory)][string]$Command)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & ssh -o BatchMode=yes -o ConnectTimeout=15 `
            -o ServerAliveInterval=10 -o ServerAliveCountMax=3 `
            $DownloadTarget $Command 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) {
        throw ("下载站命令失败（{0}）：{1}" -f $code, (($output | Out-String).Trim()))
    }
    return @($output | ForEach-Object { [string]$_ })
}

function Invoke-DownloadHostScp {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$RemotePath
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & scp -O -q -o BatchMode=yes -o ConnectTimeout=15 `
            -o ServerAliveInterval=10 -o ServerAliveCountMax=3 `
            $LocalPath ("{0}:{1}" -f $DownloadTarget, $RemotePath) 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) {
        throw ("下载站上传失败（{0}）：{1}" -f $code, (($output | Out-String).Trim()))
    }
}

function Get-GiteeCredential {
    $lines = ("protocol=https`nhost=gitee.com`n`n" | git credential fill 2>$null)
    $values = @{}
    foreach ($line in $lines) {
        $index = ([string]$line).IndexOf('=')
        if ($index -gt 0) {
            $values[([string]$line).Substring(0, $index)] = ([string]$line).Substring($index + 1)
        }
    }
    if (-not $values.ContainsKey('username') -or -not $values.ContainsKey('password')) {
        throw '本机没有 Gitee 凭据，无法走 Gitee 中转。'
    }
    return [pscustomobject]@{ User = [string]$values['username']; Token = [string]$values['password'] }
}

# 补丁先推到 Gitee 私有中转仓，再由野草云从 Gitee 拉取。
# Gitee 只做传输，不写进 patch-latest 的 mirrors。
function Publish-PatchViaGitee {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$PatchJsonPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$MirrorRoot
    )

    $credential = Get-GiteeCredential
    $repository = $GiteeRepo.Trim().TrimEnd('/')
    if ($repository -notmatch '^[^/]+/[^/]+$') { throw "GiteeRepo 格式应为 owner/repo：$repository" }
    $encodedUser = [uri]::EscapeDataString($credential.User)
    $encodedToken = [uri]::EscapeDataString($credential.Token)
    $remoteUrl = "https://${encodedUser}:${encodedToken}@gitee.com/${repository}.git"
    $branch = 'axtools-transfer'
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('axtools-gitee-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        & git -C $temp init -q 2>&1 | Out-Null
        & git -C $temp config user.name 'AxTools' 2>&1 | Out-Null
        & git -C $temp config user.email 'axtools@localhost' 2>&1 | Out-Null
        Copy-Item -LiteralPath $ZipPath -Destination (Join-Path $temp (Split-Path -Leaf $ZipPath)) -Force
        Copy-Item -LiteralPath $PatchJsonPath -Destination (Join-Path $temp 'patch.json') -Force
        & git -C $temp checkout -b $branch 2>&1 | Out-Null
        & git -C $temp add --all 2>&1 | Out-Null
        & git -C $temp commit -q -m "patch $Version transfer" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Gitee 中转提交失败。' }
        & git -C $temp remote add origin $remoteUrl 2>&1 | Out-Null
        & git -C $temp push -f origin $branch 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Gitee 推送失败（$repository）。" }

        $remoteTemp = "/tmp/axtools-gitee-" + [guid]::NewGuid().ToString('N')
        $remoteZip = "$MirrorRoot/" + (Split-Path -Leaf $ZipPath)
        $remoteJson = "$MirrorRoot/patch.json"
        $command = @"
rm -rf '$remoteTemp'
git clone --depth 1 --branch '$branch' '$remoteUrl' '$remoteTemp'
mkdir -p '$MirrorRoot'
cp -f '$remoteTemp/$(Split-Path -Leaf $ZipPath)' '$remoteZip.axtmp'
cp -f '$remoteTemp/patch.json' '$remoteJson.axtmp'
mv -f '$remoteZip.axtmp' '$remoteZip'
mv -f '$remoteJson.axtmp' '$remoteJson'
rm -rf '$remoteTemp'
sha256sum '$remoteZip'
"@
        $result = Invoke-DownloadHostSsh -Command $command
        $remoteHash = (@($result) | Where-Object { $_ -match '^[0-9a-fA-F]{64}\s' } | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($remoteHash)) { throw '野草云从 Gitee 拉取后没有返回补丁包 SHA256。' }
        $actualHash = (($remoteHash -split '\s+')[0]).ToLowerInvariant()
        $expectedHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "野草云补丁包校验失败：本地 $expectedHash / 远端 $actualHash"
        }
        Write-AxTaskStage -Stage 'gitee' -Message '野草云已从 Gitee 中转拉取补丁包并校验通过'
    }
    catch {
        throw ($_.Exception.Message.Replace($encodedToken, '***').Replace($remoteUrl, "https://${encodedUser}:***@gitee.com/${repository}.git"))
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 补丁是相对“当前线上基线”的差量：插件 Version.json 只列内容补丁，
# 这里再和线上全量 manifest 对比，把变化的程序文件（尤其 Shipping.exe）补进来。
function Get-BaseFileMap {
    param([string]$Segment, [string]$ExplicitUrl)

    $manifestUrl = $ExplicitUrl
    if ([string]::IsNullOrWhiteSpace($manifestUrl)) {
        $latestUrl = "https://dl.crossingvoid.top/games/$Segment/latest.json"
        $latest = Invoke-RestMethod -Uri "${latestUrl}?t=$([DateTimeOffset]::Now.ToUnixTimeSeconds())" -TimeoutSec 30
        $manifestUrl = [string]$latest.manifestUrl
    }
    if ([string]::IsNullOrWhiteSpace($manifestUrl)) {
        throw "无法确定基线 manifest URL：$Segment"
    }

    Write-AxTaskStage -Stage 'base' -Message '正在读取线上基线 manifest' -Detail $manifestUrl
    $manifest = Invoke-RestMethod -Uri "${manifestUrl}?t=$([DateTimeOffset]::Now.ToUnixTimeSeconds())" -TimeoutSec 30
    $map = @{}
    foreach ($file in @($manifest.files)) {
        $map[([string]$file.path -replace '\\', '/')] = [pscustomobject]@{
            Size = [int64]$file.sizeBytes
            Sha256 = ([string]$file.sha256).ToLowerInvariant()
        }
    }
    return $map
}

try {
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前 Windows 补丁发布任务。'
    $build = Read-BuildManifest -PackageRoot $GamePackageRoot -ExplicitPath $VersionManifestPath
    $data = $build.Data
    if ([string]$data.mode -ne 'Patch') {
        throw "当前 Version.json 不是 Patch 模式：$($data.mode)"
    }
    if (-not [string]::IsNullOrWhiteSpace($Version) -and [string]$data.version -ne $Version) {
        throw "打包产物版本（$($data.version)）与 AX 读取版本（$Version）不一致。"
    }
    $Version = [string]$data.version
    if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Version.json 缺少 version。' }
    if ([string]$data.platform -notmatch 'Win') { throw "补丁平台不是 Windows：$($data.platform)" }
    if (-not $FragmentsOnly -and [string]::IsNullOrWhiteSpace($GitHubRepo)) { throw '缺少 GitHubRepo。' }

    $artifactDir = [string]$data.artifactDir
    if ([string]::IsNullOrWhiteSpace($artifactDir)) { throw 'Version.json 缺少 artifactDir。' }
    $files = @($data.files)
    if ($files.Count -eq 0) { throw 'Version.json 没有 files。' }

    Write-AxTaskStage -Stage 'scan' -Message "正在校验补丁产物：$($files.Count) 个文件"
    $entries = [System.Collections.Generic.List[object]]::new()
    $archiveRoot = $build.Root
    foreach ($file in $files) {
        Assert-NotCancelled
        $sourceRelative = ([string]$file.path) -replace '/', '\'
        $prefix = $artifactDir.TrimEnd('\') + '\'
        if (-not $sourceRelative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "补丁路径不在 $artifactDir 下：$sourceRelative"
        }
        $deployRelative = $sourceRelative.Substring($prefix.Length)
        if ([string]::IsNullOrWhiteSpace($deployRelative) -or $deployRelative -match '(^|\\)\.\.(\\|$)') {
            throw "补丁路径不安全：$sourceRelative"
        }
        $sourcePath = Join-Path $archiveRoot $sourceRelative
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "补丁文件不存在：$sourcePath"
        }
        $item = Get-Item -LiteralPath $sourcePath
        if ([int64]$file.size -ne $item.Length) {
            throw "补丁文件大小不符：$deployRelative（清单 $($file.size) / 实际 $($item.Length)）"
        }
        $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $entries.Add([pscustomobject]@{
            Path = ($deployRelative -replace '\\', '/')
            SizeBytes = [int64]$item.Length
            Sha256 = $hash
            SourcePath = $sourcePath
        })
    }

    # 插件只列内容补丁；和线上基线再比一次，补上变化的程序文件（如 Shipping.exe）。
    $baseMap = @{}
    try {
        $baseMap = Get-BaseFileMap -Segment $ProductSegment -ExplicitUrl $BaseManifestUrl
    }
    catch {
        throw ("读取线上基线失败，不能生成可用的补丁包：" + $_.Exception.Message)
    }
    if ($baseMap.Count -eq 0) {
        throw '线上基线 manifest 没有文件，不能判断程序文件差量。'
    }

    $known = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) { [void]$known.Add([string]$entry.Path) }
    if ($baseMap.Count -gt 0) {
        foreach ($localFile in @(Get-ChildItem -LiteralPath $GamePackageRoot -Recurse -File -ErrorAction SilentlyContinue)) {
            Assert-NotCancelled
            $relative = $localFile.FullName.Substring($GamePackageRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            if ($relative -match '\.(pdb|axtmp)$' -or $relative -match '(^|/)(Saved|\.git|_download)/') { continue }
            if ($known.Contains($relative)) { continue }

            $base = $baseMap[$relative]
            $needsPatch = $true
            if ($null -ne $base -and [int64]$base.Size -eq $localFile.Length) {
                $localHash = (Get-FileHash -LiteralPath $localFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $needsPatch = $localHash -ne $base.Sha256
            }
            if (-not $needsPatch) { continue }

            $entries.Add([pscustomobject]@{
                Path = $relative
                SizeBytes = [int64]$localFile.Length
                Sha256 = (Get-FileHash -LiteralPath $localFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                SourcePath = $localFile.FullName
            })
            [void]$known.Add($relative)
        }
    }

    if ($FragmentsOnly) {
        $outputRoot = if ([string]::IsNullOrWhiteSpace($FragmentOutputDirectory)) {
            Join-Path ([Environment]::GetFolderPath('Desktop')) 'CrossingVoidPC碎片'
        }
        else {
            $FragmentOutputDirectory
        }
        $fragmentResult = New-PatchFragments `
            -Entries @($entries) `
            -OutputRoot $outputRoot `
            -Version $Version `
            -BaseVersion ([string]$data.releaseVersion) `
            -ProductSegment $ProductSegment `
            -SliceBytes $MaxFragmentBytes
        $message = "补丁碎片已生成：{0} 个碎片 / {1} 个文件；{2}" -f `
            $fragmentResult.ArchiveCount, $fragmentResult.FileCount, $fragmentResult.OutputDirectory
        Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message $message
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message $message
        exit 0
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('axtools-patch-' + [guid]::NewGuid().ToString('N'))
    $staging = Join-Path $tempRoot 'staging'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $patch = [ordered]@{
        schemaVersion = 1
        kind = 'patch'
        productSegment = $ProductSegment
        platform = 'Windows'
        version = $Version
        baseVersion = [string]$data.releaseVersion
        channel = $Channel
        generatedAt = [DateTimeOffset]::Now.ToString('o')
        files = @($entries | ForEach-Object {
            [ordered]@{ path = $_.Path; sizeBytes = $_.SizeBytes; sha256 = $_.Sha256 }
        })
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'patch.json'),
        (($patch | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $build.Path -Destination (Join-Path $staging 'Version.json') -Force

    foreach ($entry in $entries) {
        Assert-NotCancelled
        $destination = Join-Path $staging ($entry.Path -replace '/', '\')
        $directory = Split-Path -Parent $destination
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        Copy-Item -LiteralPath $entry.SourcePath -Destination $destination -Force
    }

    $zipName = "$ProductSegment-Windows-Patch-$Version.zip"
    $zipPath = Join-Path $tempRoot $zipName
    if (-not ($entries | Where-Object { $_.Path -match 'Binaries/Win64/CrossingVoid-Win64-Shipping\.exe$' })) {
        Write-AxTaskStage `
            -Stage 'warning' `
            -Message '补丁包不含 CrossingVoid-Win64-Shipping.exe；如果本次改了 C++，还需要另外发布新 exe' `
            -Detail '插件 Version.json 只列了补丁内容文件。'
    }
    Write-AxTaskStage -Stage 'package' -Message '正在生成 Windows 补丁包'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $tag = "PC-Patch-V$Version"
    $patchUrl = "https://github.com/$GitHubRepo/releases/download/$tag/patch.json"
    $packageUrl = "https://github.com/$GitHubRepo/releases/download/$tag/$zipName"
    $pointerUrl = "https://github.com/$GitHubRepo/releases/download/PC-Patch-Latest/patch-latest.json"
    $mirrorRoot = "$DownloadRemoteRoot/$ProductSegment/patches/$Version"
    $mirrorManifestUrl = "$DownloadPublicBaseUrl/games/$ProductSegment/patches/$Version/patch.json"
    $mirrorPackageUrl = "$DownloadPublicBaseUrl/games/$ProductSegment/patches/$Version/$zipName"
    $mirrorPointerUrl = "$DownloadPublicBaseUrl/games/$ProductSegment/patches/latest.json"

    if (-not $DryRun) {
        if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
            throw '没有找到 GitHub CLI（gh），无法上传 Windows 补丁。'
        }
        $auth = Invoke-Gh -Arguments @('auth', 'status')
        if ($auth.ExitCode -ne 0) {
            throw ("GitHub CLI 未登录或 token 已失效，先执行 gh auth login。`n" +
                (($auth.Lines | Out-String).Trim()))
        }

        Write-AxTaskStage -Stage 'github' -Message "正在发布补丁 Release：$tag" -Detail $GitHubRepo
        Ensure-Release -Repository $GitHubRepo -Tag $tag `
            -Title "Windows 补丁 V$Version" `
            -Notes "Windows 内容补丁 V$Version（基线 $($data.releaseVersion)）"
        foreach ($asset in @((Join-Path $staging 'patch.json'), $zipPath)) {
            $upload = Invoke-Gh -Arguments @('release', 'upload', $tag, $asset, '--repo', $GitHubRepo, '--clobber')
            if ($upload.ExitCode -ne 0) {
                throw ("GitHub 补丁附件上传失败：{0}`n{1}" -f $asset, (($upload.Lines | Out-String).Trim()))
            }
        }

        # 默认本机直传野草云；Gitee 只保留为显式可选项，正常发布不再绕路。
        Write-AxTaskStage -Stage 'download' -Message '正在同步补丁到野草云' -Detail $mirrorRoot
        if ($UseGiteeTransfer) {
            Publish-PatchViaGitee `
                -ZipPath $zipPath `
                -PatchJsonPath (Join-Path $staging 'patch.json') `
                -Version $Version `
                -MirrorRoot $mirrorRoot
        }
        else {
            Invoke-DownloadHostSsh -Command ("mkdir -p '{0}'" -f $mirrorRoot) | Out-Null
            foreach ($asset in @((Join-Path $staging 'patch.json'), $zipPath)) {
                $name = Split-Path -Leaf $asset
                $remote = "$mirrorRoot/$name"
                $lastError = $null
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    try {
                        Invoke-DownloadHostScp -LocalPath $asset -RemotePath "$remote.axtmp"
                        Invoke-DownloadHostSsh -Command ("mv -f -- '{0}.axtmp' '{0}'" -f $remote) | Out-Null
                        $lastError = $null
                        break
                    }
                    catch {
                        $lastError = $_
                        if ($attempt -lt 3) { Start-Sleep -Seconds 5 }
                    }
                }
                if ($null -ne $lastError) { throw $lastError }
            }
        }

        $pointer = [ordered]@{
            schemaVersion = 1
            kind = 'patch'
            productSegment = $ProductSegment
            platform = 'Windows'
            version = $Version
            baseVersion = [string]$data.releaseVersion
            channel = $Channel
            generatedAt = [DateTimeOffset]::Now.ToString('o')
            manifestUrl = $patchUrl
            packageUrl = $packageUrl
            packageSizeBytes = (Get-Item -LiteralPath $zipPath).Length
            packageSha256 = $zipHash
            mirrors = @(
                [ordered]@{
                    name = 'yecaoyun'
                    manifestUrl = $mirrorManifestUrl
                    packageUrl = $mirrorPackageUrl
                }
            )
        }
        $pointerPath = Join-Path $tempRoot 'patch-latest.json'
        [IO.File]::WriteAllText(
            $pointerPath,
            (($pointer | ConvertTo-Json -Depth 5) + "`n"),
            [Text.UTF8Encoding]::new($false))
        Ensure-Release -Repository $GitHubRepo -Tag 'PC-Patch-Latest' `
            -Title 'Windows 补丁 latest' `
            -Notes 'Windows 补丁渠道最新指针（只含 patch-latest.json）'
        $pointerUpload = Invoke-Gh -Arguments @(
            'release', 'upload', 'PC-Patch-Latest', $pointerPath,
            '--repo', $GitHubRepo, '--clobber')
        if ($pointerUpload.ExitCode -ne 0) {
            throw ("GitHub 补丁指针上传失败：{0}" -f (($pointerUpload.Lines | Out-String).Trim()))
        }
        if (-not [string]::IsNullOrWhiteSpace($DownloadTarget)) {
            $remotePointer = "$DownloadRemoteRoot/$ProductSegment/patches/latest.json"
            Invoke-DownloadHostScp -LocalPath $pointerPath -RemotePath "$remotePointer.axtmp"
            Invoke-DownloadHostSsh -Command ("mv -f -- '{0}.axtmp' '{0}'" -f $remotePointer) | Out-Null
        }
    }

    $summary = if ($DryRun) {
        "Windows 补丁演练通过：V$Version，基线 $($data.releaseVersion)，$($entries.Count) 个文件；包 $([Math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB"
    }
    else {
        "Windows 补丁已发布：V$Version，基线 $($data.releaseVersion)，$($entries.Count) 个文件；$pointerUrl"
    }
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message $summary
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message $summary
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
catch [System.OperationCanceledException] {
    Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message $_.Exception.Message
    exit 130
}
catch {
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
