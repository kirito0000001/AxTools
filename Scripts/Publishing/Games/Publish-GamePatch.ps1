[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePackageRoot,
    [Parameter(Mandatory)][string]$ProductSegment,
    [string]$Channel = 'Stable',
    [string]$GitHubRepo = '',
    [string]$Version = '',
    [string]$BaseManifestUrl = '',
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

function Read-BuildManifest {
    param([string]$PackageRoot)
    $root = [IO.Path]::GetFullPath($PackageRoot)
    $candidates = @(
        (Join-Path $root 'Version.json'),
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
    $build = Read-BuildManifest -PackageRoot $GamePackageRoot
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
    if ([string]::IsNullOrWhiteSpace($GitHubRepo)) { throw '缺少 GitHubRepo。' }

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
