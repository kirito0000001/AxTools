[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FragmentDirectory,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [string]$ProductSegment = 'crossingvoid',
    [string]$GameShortName = '零境',
    [string]$FileSyncTarget = 'yecaoyun-hk',
    [string]$PublicBaseUrl = 'https://dl.crossingvoid.top',
    # GitHub 兜底：只传 fragments.json，不传 zip；固定 latest Release 给启动器回退。
    [string]$GitHubRepository = '',
    [switch]$GitHubOnly,
    [switch]$SkipSyncScriptDeploy,
    [switch]$SkipVerify,
    [switch]$DryRun
)

# 碎片清单发布（只发 fragments.json，**不发 zip**）
#
# 干什么：把《生成碎片》产出的 fragments.json 传到下载站，让启动器/页面提前知道
#   "这一版有几个包、每个包多大、装了多少文件"。
# 碎片包本身走网盘/QQ 群分发，**不上服务器** —— 这里只发布描述它们的清单。
#
# 落位：
#   /srv/downloads/games/<产品段>/fragments/<版本>.json
#   /srv/downloads/games/<产品段>/latest.json 里加 fragmentsUrl
#
# 为什么上传前还要核对本地 zip：清单是玩家核对包的唯一依据，包被换过/清单写错了，
#   比不写清单更糟。大小和包内条目数都是几十毫秒能算出来的，先对一遍再传。
#
# 为什么默认更新服务端 game-file-sync.sh：游戏包发布 finalize 会重写 latest.json，
#   旧脚本不带 fragmentsUrl，会把这里加上的链接冲掉。上传一次新脚本后两边就一致了。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$ProductSegment = $ProductSegment.Trim().Trim('/')
$GameShortName = $GameShortName.Trim()
if ($ProductSegment -notmatch '^[A-Za-z0-9._-]+$') { throw "产品目录名不合法：$ProductSegment" }
if ([string]::IsNullOrWhiteSpace($GameShortName)) { throw '碎片包名短名（GameShortName）不能为空。' }
if ($ReleaseVersion -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { throw "版本号格式不对：$ReleaseVersion（应形如 0.6.0）" }
if ($PublicBaseUrl -notmatch '^https?://') { throw "PublicBaseUrl 不合法：$PublicBaseUrl" }
if ($GitHubOnly -and [string]::IsNullOrWhiteSpace($GitHubRepository)) {
    throw '选择了只发 GitHub，但没有提供 GitHubRepository。'
}
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository) -and
    $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "GitHubRepository 不合法：$GitHubRepository（应形如 owner/repo）"
}

# 服务器是公网机器，SSH 常年被扫描/暴力尝试，偶尔会出现连接被拖住、密码提示挂起。
# 统一加 BatchMode（绝不等待交互输入）+ 连接/握手超时 + 保活，避免整个发布任务无限挂起。
$script:SshConnectionOptions = @(
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=15',
    '-o', 'ConnectionAttempts=3',
    '-o', 'ServerAliveInterval=10',
    '-o', 'ServerAliveCountMax=3'
)
$script:NetworkAttempts = 3

function Assert-NotCancelled {
    if (Test-AxTaskCancellation) {
        throw [OperationCanceledException]::new('用户已停止碎片发布。')
    }
}

function Format-Bytes {
    param([int64]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Invoke-Ssh {
    param([Parameter(Mandatory)][string]$Command)

    $attempts = $script:NetworkAttempts
    $sshOptions = $script:SshConnectionOptions
    $lastError = $null
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        Assert-NotCancelled
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & ssh @sshOptions $FileSyncTarget $Command 2>&1
            $code = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        $lines = @($output | ForEach-Object { [string]$_ })
        if ($code -eq 0) {
            return $lines
        }

        $lastError = "服务端命令失败（退出码 {0}）：{1}`n{2}" -f `
            $code, $Command, (($lines | Out-String).Trim())
        if ($attempt -lt $attempts) {
            Write-Output ("SSH 失败，{0} 秒后重试（{1}/{2}）：{3}" -f `
                (2 * $attempt), $attempt, $attempts, $Command)
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    throw $lastError
}

function Invoke-SftpBatch {
    param(
        [Parameter(Mandatory)][string[]]$Commands,
        [string]$Detail = ''
    )

    $attempts = $script:NetworkAttempts
    $sshOptions = $script:SshConnectionOptions
    $batchFile = Join-Path ([IO.Path]::GetTempPath()) ("axtools-fragments-sftp-" + [guid]::NewGuid().ToString('N') + ".txt")
    [IO.File]::WriteAllText($batchFile, ($Commands -join "`n"), [Text.UTF8Encoding]::new($false))
    $shown = if ([string]::IsNullOrWhiteSpace($Detail)) { 'sftp' } else { $Detail }
    try {
        $lastError = $null
        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            Assert-NotCancelled
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & sftp -q @sshOptions -b $batchFile $FileSyncTarget 2>&1
                $code = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previous
            }

            if ($code -eq 0) {
                return
            }

            $lastError = "sftp 失败（退出码 {0}）：{1}`n{2}" -f `
                $code, $shown, (($output | ForEach-Object { [string]$_ } | Out-String).Trim())
            if ($attempt -lt $attempts) {
                Write-Output ("SFTP 失败，{0} 秒后重试（{1}/{2}）：{3}" -f `
                    (2 * $attempt), $attempt, $attempts, $shown)
                Start-Sleep -Seconds (2 * $attempt)
            }
        }

        throw $lastError
    }
    finally {
        Remove-Item -LiteralPath $batchFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-GitHubCli {
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

# 只上传 fragments.json：latest 给启动器固定 URL 兜底，版本标签保留历史。
function Publish-GitHubFragmentManifest {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ManifestPath
    )

    if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
        throw '没有找到 GitHub CLI（gh），无法发布 GitHub 兜底清单。'
    }

    $auth = Invoke-GitHubCli -Arguments @('auth', 'status')
    if ($auth.ExitCode -ne 0) {
        $detail = (($auth.Lines | Out-String).Trim())
        throw ("GitHub CLI 未登录或 token 已失效，先执行 gh auth login。" +
            $(if ($detail) { "`n$detail" } else { '' }))
    }

    $latestTag = 'fragments-latest'
    $versionTag = "fragments-v$Version"
    $releases = @(
        [ordered]@{
            Tag   = $versionTag
            Title = "碎片清单 V$Version"
            Notes = "碎片清单 V$Version（GitHub 兜底；只含 fragments.json，碎片 zip 走网盘/群）"
        },
        [ordered]@{
            Tag   = $latestTag
            Title = '碎片清单 latest'
            Notes = '最新碎片清单（GitHub 兜底；只含 fragments.json，碎片 zip 走网盘/群）'
        }
    )

    foreach ($release in $releases) {
        Assert-NotCancelled
        $tag = [string]$release.Tag
        $view = Invoke-GitHubCli -Arguments @('release', 'view', $tag, '--repo', $Repository)
        if ($view.ExitCode -ne 0) {
            Write-AxTaskStage -Stage 'github' -Message "GitHub 上还没有 $tag，正在创建 Release" -Detail $Repository
            $created = Invoke-GitHubCli -Arguments @(
                'release', 'create', $tag,
                '--repo', $Repository,
                '--title', [string]$release.Title,
                '--notes', [string]$release.Notes)
            if ($created.ExitCode -ne 0) {
                throw ("GitHub Release 创建失败（{0}）：{1}" -f $tag, (($created.Lines | Out-String).Trim()))
            }
        }

        Write-AxTaskProgress -Stage 'github' -Percent 96 -Message "正在上传 GitHub 兜底清单：$tag"
        $upload = Invoke-GitHubCli -Arguments @(
            'release', 'upload', $tag, $ManifestPath,
            '--repo', $Repository,
            '--clobber')
        if ($upload.ExitCode -ne 0) {
            throw ("GitHub 清单上传失败（{0}）：{1}" -f $tag, (($upload.Lines | Out-String).Trim()))
        }
    }

    return "https://github.com/$Repository/releases/download/$latestTag/fragments.json"
}

try {
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止；清单是原子写入的，停止不会留下半截文件。'
    Write-AxTaskStage -Stage 'scan' -Message '正在检查碎片目录与 fragments.json...'

    $fragmentRoot = [IO.Path]::GetFullPath($FragmentDirectory).TrimEnd('\')
    if (!(Test-Path -LiteralPath $fragmentRoot -PathType Container)) {
        throw "碎片目录不存在：$fragmentRoot"
    }

    $manifestPath = Join-Path $fragmentRoot 'fragments.json'
    $manifestRebuilt = $false
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        # 兜底：包已经生成过（旧版代码不会写清单），按现有 zip 重建一份，
        # 省掉为了一个几 KB 的 JSON 重新压 2.5 GB。
        $versionParts = $ReleaseVersion.Split('.')
        $displayVersion = if ($versionParts.Count -ge 2) {
            "$($versionParts[0]).$($versionParts[1])"
        }
        else {
            $versionParts[0]
        }
        # 命名里已经带游戏短名和平台字样（零境V0.6PC… / 幻杀V0.1安卓…），所以不能再写死
        # "零境V0.6基础碎片*"；安卓还多一个"安装包"片，重建时也要带上，否则清单少一个包。
        $packagePattern = "${GameShortName}V${displayVersion}*基础碎片*.zip"
        $baseFiles = @(Get-ChildItem -LiteralPath $fragmentRoot -File -Filter $packagePattern |
            Sort-Object Name)
        $installerFiles = @()
        if ($ProductSegment -like '*-android') {
            $installerPattern = "${GameShortName}V${displayVersion}*安装包*.zip"
            $installerFiles = @(Get-ChildItem -LiteralPath $fragmentRoot -File -Filter $installerPattern |
                Sort-Object Name)
            if ($installerFiles.Count -gt 1) {
                throw "碎片目录里有多个安卓安装包，无法确定用哪个：$($installerFiles.Name -join '; ')"
            }
        }

        # 安装包排在基础碎片前面，和《生成碎片》写出来的清单顺序一致。
        $packageFiles = @($installerFiles) + @($baseFiles)
        if ($packageFiles.Count -eq 0) {
            throw "碎片目录里没有 fragments.json，也没有匹配 $packagePattern 的碎片包：$fragmentRoot"
        }

        $rebuiltFragments = [System.Collections.Generic.List[object]]::new()
        $rebuiltTotalBytes = [int64]0
        foreach ($packageFile in $packageFiles) {
            Assert-NotCancelled
            $zip = [IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
            try {
                $entryCount = @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }).Count
            }
            finally {
                $zip.Dispose()
            }

            $rebuiltFragments.Add([ordered]@{
                fileName  = $packageFile.Name
                sizeBytes = [int64]$packageFile.Length
                fileCount = $entryCount
            })
            $rebuiltTotalBytes += [int64]$packageFile.Length
        }

        $rebuiltManifest = [ordered]@{
            schemaVersion = 1
            version       = $ReleaseVersion
            totalBytes    = $rebuiltTotalBytes
            fragments     = @($rebuiltFragments)
        }
        [IO.File]::WriteAllText(
            $manifestPath,
            ($rebuiltManifest | ConvertTo-Json -Depth 5) + "`n",
            [Text.UTF8Encoding]::new($false))
        $manifestRebuilt = $true
        Write-AxTaskProgress -Stage 'scan' -Percent 4 -Message (
            "没有 fragments.json，已按现有包重建：{0} 个 / {1}" -f `
                $packageFiles.Count, (Format-Bytes $rebuiltTotalBytes))
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    if ([int]$manifest.schemaVersion -ne 1) {
        throw "fragments.json 的 schemaVersion 不是 1：$($manifest.schemaVersion)"
    }

    $manifestVersion = [string]$manifest.version
    if ($manifestVersion -ne $ReleaseVersion) {
        throw "fragments.json 的版本（$manifestVersion）和要发布的版本（$ReleaseVersion）不一致。"
    }

    if (-not $manifest.Contains('fragments')) {
        throw 'fragments.json 里没有 fragments 数组。'
    }

    $fragmentItems = @($manifest.fragments)
    if ($fragmentItems.Count -eq 0) {
        throw 'fragments.json 里一个碎片包都没有。'
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    $totalBytes = [int64]0
    $index = 0
    foreach ($fragment in $fragmentItems) {
        Assert-NotCancelled
        $index++
        $name = [string]$fragment.fileName
        if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[\\/]') {
            throw "包名不合法：$name"
        }

        $path = Join-Path $fragmentRoot $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "缺少碎片包：$name"
        }

        $file = Get-Item -LiteralPath $path -Force
        if ([int64]$fragment.sizeBytes -ne $file.Length) {
            throw ("大小不一致：{0}（清单 {1} / 实际 {2}）" -f $name, $fragment.sizeBytes, $file.Length)
        }

        # 只读 zip 中央目录，几十毫秒；包被换过时 fileCount 会对不上。
        $zip = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entryCount = @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }).Count
        }
        finally {
            $zip.Dispose()
        }

        if ([int]$fragment.fileCount -ne $entryCount) {
            throw ("包内条目数不一致：{0}（清单 {1} / 实际 {2}）" -f $name, $fragment.fileCount, $entryCount)
        }

        $entries.Add([pscustomobject]@{ Name = $name; Size = [int64]$file.Length })
        $totalBytes += [int64]$file.Length
        Write-AxTaskProgress `
            -Stage 'scan' `
            -Percent (5 + [int](10.0 * $index / $fragmentItems.Count)) `
            -Message ("已核对 {0} / {1}：{2}" -f $index, $fragmentItems.Count, $name)
    }

    $remoteBase = "/srv/downloads/games/$ProductSegment"
    $remoteManifest = "$remoteBase/fragments/$ReleaseVersion.json"
    $remoteLatest = "$remoteBase/latest.json"
    $publicFragmentsUrl = "$PublicBaseUrl/games/$ProductSegment/fragments/$ReleaseVersion.json"

    # GitHub 兜底：project 暂不上下载站时，只发 fragments.json 到 GitHub Release。
    if ($GitHubOnly) {
        if ($DryRun) {
            Write-AxTaskStage `
                -Stage 'github' `
                -Message '演练模式：跳过 GitHub 清单上传' `
                -Detail $GitHubRepository
            Write-AxTaskResult -Status 'success' -ExitCode 0 -Message (
                "演练通过：{0} 个包 / {1}；GitHub 目标 {2}" -f `
                    $entries.Count, (Format-Bytes $totalBytes), $GitHubRepository)
            exit 0
        }

        Write-AxTaskStage -Stage 'github' -Message '正在发布 GitHub 兜底清单' -Detail $GitHubRepository
        $gitHubUrl = Publish-GitHubFragmentManifest `
            -Repository $GitHubRepository `
            -Version $ReleaseVersion `
            -ManifestPath $manifestPath
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message (
            "GitHub 兜底清单已发布：{0} · {1} 个包 / {2} · {3}" -f `
                $ReleaseVersion, $entries.Count, (Format-Bytes $totalBytes), $gitHubUrl)
        exit 0
    }

    if ($DryRun) {
        Write-AxTaskStage `
            -Stage 'upload' `
            -Message '演练模式：跳过清单上传与 latest.json 更新' `
            -Detail ("sftp → {0}:{1}" -f $FileSyncTarget, $remoteManifest)
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message (
            "演练通过：{0} 个包 / {1}；清单目标 {2}" -f $entries.Count, (Format-Bytes $totalBytes), $remoteManifest)
        exit 0
    }

    # 0) 确保服务端 finalize 会保留 fragmentsUrl（旧脚本重写 latest.json 时会冲掉）
    if (-not $SkipSyncScriptDeploy) {
        $localSyncScript = Join-Path $PSScriptRoot 'game-file-sync.sh'
        if (Test-Path -LiteralPath $localSyncScript -PathType Leaf) {
            $localHash = (Get-FileHash -LiteralPath $localSyncScript -Algorithm SHA256).Hash.ToLowerInvariant()
            $remoteHash = ''
            try {
                $line = @(Invoke-Ssh -Command 'sha256sum /srv/scripts/game-file-sync.sh 2>/dev/null') |
                    Select-Object -First 1
                if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                    $remoteHash = (([string]$line) -split '\s+')[0].ToLowerInvariant()
                }
            }
            catch {
                $remoteHash = ''
            }

            if ($remoteHash -ne $localHash) {
                Write-AxTaskProgress -Stage 'upload' -Percent 18 -Message '正在更新服务端 game-file-sync.sh（保留 fragmentsUrl）'
                Invoke-SftpBatch -Commands @(
                    '-mkdir /srv/scripts',
                    ("put `"{0}`" `"/srv/scripts/game-file-sync.sh.axtmp`"" -f $localSyncScript),
                    'rename "/srv/scripts/game-file-sync.sh.axtmp" "/srv/scripts/game-file-sync.sh"'
                ) -Detail 'game-file-sync.sh'
                Invoke-Ssh -Command 'chmod 755 /srv/scripts/game-file-sync.sh' | Out-Null
                Write-AxTaskProgress -Stage 'upload' -Percent 20 -Message '服务端脚本已更新。'
            }
            else {
                Write-AxTaskProgress -Stage 'upload' -Percent 20 -Message '服务端脚本已是最新。'
            }
        }
    }

    # 1) 碎片包不上传：它们走网盘/QQ群，这里只把描述它们的清单传上去
    Write-AxTaskProgress -Stage 'upload' -Percent 25 -Message (
        "碎片包只在本地核对，不上传：{0} 个 / {1}" -f $entries.Count, (Format-Bytes $totalBytes))

    # 2) 上传 fragments.json（先临时名再原子改名）
    Write-AxTaskProgress -Stage 'upload' -Percent 80 -Message '正在上传 fragments.json'
    Invoke-SftpBatch -Commands @(
        ("-mkdir `"{0}`"" -f "$remoteBase/fragments"),
        ("put `"{0}`" `"{1}`"" -f $manifestPath, "$remoteManifest.axtmp"),
        ("rename `"{0}`" `"{1}`"" -f "$remoteManifest.axtmp", $remoteManifest)
    ) -Detail 'fragments.json'

    # 3) latest.json 加/改 fragmentsUrl（读服务端的，不用 CDN 缓存）
    Assert-NotCancelled
    Write-AxTaskProgress -Stage 'manifest' -Percent 90 -Message '正在更新 latest.json 的 fragmentsUrl'
    $latestWarning = $null
    $latestLines = Invoke-Ssh -Command (
        "if [ -f '{0}' ]; then cat '{0}'; else printf '%s\n' '__AXTOOLS_LATEST_MISSING__'; fi" -f $remoteLatest)
    $latestText = ($latestLines | Out-String).Trim()
    if ($latestText -eq '__AXTOOLS_LATEST_MISSING__' -or [string]::IsNullOrWhiteSpace($latestText)) {
        # 还没有游戏包清单时，fragments 本身已经可单独访问；不要为了缺 latest 把整次发布判失败。
        $latestWarning = "未找到 latest.json（$remoteLatest），碎片清单已发布，但未写入 fragmentsUrl；先发布一次游戏包后再发布清单即可挂上链接。"
        Write-Output $latestWarning
        Write-AxTaskProgress -Stage 'manifest' -Percent 94 -Message '未找到 latest.json，已跳过 fragmentsUrl 更新' -Detail $latestWarning
    }
    else {
        $latest = $latestText | ConvertFrom-Json -AsHashtable
        $latest['fragmentsUrl'] = $publicFragmentsUrl
        $latestJson = $latest | ConvertTo-Json -Compress -Depth 10
        $latestTemp = Join-Path ([IO.Path]::GetTempPath()) ("axtools-latest-" + [guid]::NewGuid().ToString('N') + ".json")
        [IO.File]::WriteAllText($latestTemp, $latestJson + "`n", [Text.UTF8Encoding]::new($false))
        try {
            Invoke-SftpBatch -Commands @(
                ("put `"{0}`" `"{1}`"" -f $latestTemp, "$remoteLatest.axtmp"),
                ("rename `"{0}`" `"{1}`"" -f "$remoteLatest.axtmp", $remoteLatest)
            ) -Detail 'latest.json'
        }
        finally {
            Remove-Item -LiteralPath $latestTemp -Force -ErrorAction SilentlyContinue
        }
    }

    # 4) 公网校验：清单能读到，且条目与本地一致（zip 本身不在服务器上，不查包）
    # 这一步是只读复核；服务端写入已经成功，不能因为本机 PowerShell 偶发 TLS 失败把整次发布判失败。
    $verifyWarning = $null
    if (-not $SkipVerify) {
        Assert-NotCancelled
        Write-AxTaskProgress -Stage 'verify' -Percent 95 -Message '正在校验公网碎片清单'
        $online = $null
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Assert-NotCancelled
            try {
                $cacheBuster = [DateTimeOffset]::Now.ToUnixTimeSeconds() + $attempt
                $response = Invoke-WebRequest -Uri "${publicFragmentsUrl}?t=$cacheBuster" -UseBasicParsing -TimeoutSec 30
                $online = $response.Content | ConvertFrom-Json
                break
            }
            catch {
                if ($attempt -ge 3) {
                    $verifyWarning = "公网校验未完成（{0}）；服务端清单已写入" -f $_.Exception.Message
                    Write-Output $verifyWarning
                    break
                }

                Write-Output ("公网校验失败，{0} 秒后重试（{1}/3）：{2}" -f `
                    (2 * $attempt), $attempt, $_.Exception.Message)
                Start-Sleep -Seconds (2 * $attempt)
            }
        }

        if ($null -ne $online) {
            if ([string]$online.version -ne $ReleaseVersion) {
                throw "公网 fragments.json 版本不符：$($online.version)"
            }

            $onlineFragments = @($online.fragments)
            if ($onlineFragments.Count -ne $entries.Count) {
                throw ("公网 fragments.json 包数不符：{0} / {1}" -f $onlineFragments.Count, $entries.Count)
            }

            for ($fragmentIndex = 0; $fragmentIndex -lt $entries.Count; $fragmentIndex++) {
                $entry = $entries[$fragmentIndex]
                $onlineEntry = $onlineFragments[$fragmentIndex]
                if ([string]$onlineEntry.fileName -ne $entry.Name -or
                    [int64]$onlineEntry.sizeBytes -ne $entry.Size) {
                    throw ("公网清单条目不符：本地 {0} / 公网 {1}（{2} 字节）" -f `
                        $entry.Name, $onlineEntry.fileName, $onlineEntry.sizeBytes)
                }
            }
        }
    }

    $rebuildNote = if ($manifestRebuilt) { '（按现有包重建）' } else { '' }
    $verifyNote = if ([string]::IsNullOrWhiteSpace($verifyWarning)) { '' } else { " · $verifyWarning" }
    $latestNote = if ([string]::IsNullOrWhiteSpace($latestWarning)) { '' } else { ' · latest.json 缺失，fragmentsUrl 未写入' }
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message (
        "碎片清单已发布{0}：{1} · {2} 个包 / {3} · {4}{5}{6}" -f `
            $rebuildNote, $ReleaseVersion, $entries.Count, (Format-Bytes $totalBytes), $publicFragmentsUrl, $verifyNote, $latestNote)
    exit 0
}
catch [OperationCanceledException] {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message $_.Exception.Message
    exit 130
}
catch {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
