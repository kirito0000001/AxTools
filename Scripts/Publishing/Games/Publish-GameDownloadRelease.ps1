[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Check', 'Publish', 'RebuildRepo')]
    [string]$Mode,

    [Parameter(Mandatory)]
    [ValidateSet('Windows', 'Android')]
    [string]$Platform,

    [Parameter(Mandatory)]
    [string]$GameDirectory,

    [Parameter(Mandatory)]
    [string]$ReleaseVersion,

    [ValidateSet('Stable', 'Test')]
    [string]$Channel = 'Stable',

    [string]$ReleaseNotes = '',

    # 本机暂存仓库的根目录（<根>\<产品段> 就是一个 git 仓库）。
    # 2026-09-20 用户把游戏产物和下载站从 D: 挪到 F:（D: 会吃紧、可能被清），
    # 两边同盘才能用硬链接做镜像，所以这里的默认值跟着改成 F:。
    [string]$StagingRoot = 'F:\DaBaoV\下载站',

    # 下载站上的裸仓库（ssh 别名 + 路径）。留空则按产品段推导 yecaoyun-upload:/srv/repos/game-<产品段>.git
    [string]$UploadTarget = '',

    # 传输方式：
    #   files = 逐文件同步（默认，2026-09-20 起）：只传"和上一版不一样的文件"，
    #           其余由服务端从上一版硬链接（内容没变 ⇒ 0 字节）。不再需要本机 git 仓库。
    #   git   = 老路子：提交本机暂存仓库再 push（保留作兜底/对照）。
    [ValidateSet('files', 'git')]
    [string]$TransferMode = 'files',

    # files 模式下用来执行服务端脚本/接收文件的 ssh 目标（走 root 别名，和服务器更新那条链路一致）
    [string]$FileSyncTarget = 'yecaoyun-hk',

    # 对外下载基址。清单里的 baseUrl = <这个>/games/<产品段>/<版本>/
    [string]$PublicBaseUrl = 'https://dl.crossingvoid.top',

    # dl 上的产品目录名。留空则按平台推导（Windows → crossingvoid，Android → crossingvoid-android）
    [string]$ProductSegment = '',

    # 版本标记文件名。这个文件由脚本生成（打包目录里没有），跟着产物一起上线，
    # 给外部工具读版本用。零境沿用旧名字，别的游戏传自己工程名即可。
    [string]$VersionMarkerName = 'CrossingVoid.version.json',

    # GitHub 备用渠道：把这一版按文件清单**原样**再发一份到 GitHub Release（留空 = 跳过）。
    # 附件名 = 相对路径里的 `/` 换成 `__`（GitHub 附件名不允许带斜杠）；tag 沿用既有习惯
    # PC → PC-V<版本>、安卓 → Android-V<版本>。和 dl 那条互不依赖：dl 是主线，GitHub 是备用源。
    [string]$GitHubRepo = '',

    # 两个独立发布目标由宿主按钮控制：
    #   SkipGitHub = 只发下载站，不再顺带发 GitHub 备用渠道；
    #   GitHubOnly = 跳过下载站/SFTP，只生成清单后发 GitHub Release。
    [switch]$SkipGitHub,
    [switch]$GitHubOnly,

    [switch]$SkipVerify,
    [switch]$DryRun
)

# 游戏包发布内核（P3 重写，2026-09-20）
#
# 干什么：把"文件夹形态的打包产物"变成"下载站上的一个版本"，全程不压缩、不切片：
#   1) 检查产物（按平台过滤：PC 排除 pdb/Saved 日志；安卓发主 APK + **OBB 里的每个条目**
#      —— OBB 本体不发，它就是一个不压缩 ZIP，拆开发文件级清单才能给出"只下变化的 chunk"）
#   2) 生成 v1 文件级清单（schemaVersion=1 + baseUrl + 每个文件的 sha256）
#   3) 把产物同步进本机暂存仓库（树里只保留这一个版本，更早的版本物理删掉）
#   4) 提交（**在上一次提交之上**；树上仍只保留这一个版本）→ git push --force（只传变化的文件、带百分比）
#   5) 服务端 post-receive hook 负责：把上一版硬链接归档到 games/<产品段>-archive/<版本>/
#      → checkout -f main 落到 /srv/downloads → 省内存 gc 回收旧对象 → 回报体积
#   6) 公网校验：拉一次清单、HEAD 抽样文件，核对版本与字节数
#   7) GitHub 备用渠道（-GitHubRepo 非空时）：把同一份文件清单**原样**再发一份到
#      GitHub Release（PC → PC-V<版本>，安卓 → Android-V<版本>，附件名 = 相对路径的 / 换成 __），
#      传完最后传清单 manifest.json。和 dl 那条互不依赖，也不看"这次有没有真传字节"。
#
# 为什么提交必须带父提交（2026-09-20 实测踩出来的）：
#   曾经为了"仓库里只有一版"用**无父提交**（commit-tree 不带 -p）。结果 git 推送时找不到共同祖先：
#   客户端只能沿着"这次要推的提交"的历史往上找 haves，孤儿提交没有祖先 ⇒ 服务端一个对象都 ACK 不了
#   ⇒ 每次都全量重传。实测：0.5.13 → 0.5.14 内容一字未改（86 个文件 sha256 全同，只有清单和版本标记
#   是新的），2.53 GB 又完整传了一遍。本地最小复现：同样内容只换目录名，无父提交传 20 MB，
#   带父提交只传 254 字节。
#   现在的取舍：父提交链会让**变化过的**对象留在两侧仓库里（未变的文件按哈希复用，不额外占空间），
#   换来的是"没改东西 = 几乎 0 上传"。历史涨得太大了就跑一次「重整仓库（压平历史）」——
#   那会换来一次全量上传，之后重新开始增量。
#   服务端工作树仍然永远只有最新版；上一版由 hook 硬链接兜底（同盘同 inode，归档不额外占空间）。
#
# 进度走 AxTools 任务协议（stdout 的 ::axtools 行，由 AxTaskProtocol.psm1 直写 Console）：
# 阶段 scan（扫描+哈希）→ stage（同步+暂存）→ commit → push（百分比）→ verify。
#
# 退出码：0 = 成功；非 0 = 失败（失败原因会连同 git/stderr 一起打进日志）。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force

# ---------------------------------------------------------------------------
# 派生值
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ProductSegment)) {
    $ProductSegment = if ($Platform -eq 'Android') { 'crossingvoid-android' } else { 'crossingvoid' }
}
$ProductSegment = $ProductSegment.Trim().Trim('/')
if ($ProductSegment -notmatch '^[A-Za-z0-9._-]+$') {
    throw "产品目录名不合法：$ProductSegment"
}

$repoName = "game-$ProductSegment.git"
if ([string]::IsNullOrWhiteSpace($UploadTarget)) {
    $UploadTarget = "yecaoyun-upload:/srv/repos/$repoName"
}

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $StagingRoot $ProductSegment))
$VersionSegmentPath = "games/$ProductSegment/$ReleaseVersion"
$VersionDirectory = Join-Path $RepoRoot ($VersionSegmentPath -replace '/', '\')
$ManifestDirectory = Join-Path $RepoRoot (("games/$ProductSegment/manifests") -replace '/', '\')
$ManifestPath = Join-Path $ManifestDirectory "$ReleaseVersion.json"
$BaseUrl = '{0}/games/{1}/{2}/' -f $PublicBaseUrl.TrimEnd('/'), $ProductSegment, $ReleaseVersion
$ManifestProductKey = if ($Channel -eq 'Test') { "$ProductSegment-game-test" } else { "$ProductSegment-game" }
$ChannelLabel = if ($Channel -eq 'Test') { 'test' } else { 'stable' }
$PlatformLabel = if ($Platform -eq 'Android') { 'Android' } else { 'Windows' }
if ([string]::IsNullOrWhiteSpace($VersionMarkerName)) { $VersionMarkerName = 'CrossingVoid.version.json' }
# 安卓 OBB 的旁车文件名（从版本标记名派生：CrossingVoid.version.json → CrossingVoid.obb.json）。
# 它告诉启动器"要组装回哪个 OBB、由哪些条目组成"——OBB 本体不上传，只上传里面的条目。
$ObbSidecarName = if ($VersionMarkerName -like '*.version.json') {
    $VersionMarkerName.Substring(0, $VersionMarkerName.Length - 'version.json'.Length) + 'obb.json'
}
else {
    "$ProductSegment.obb.json"
}

# GitHub 渠道的派生值（只有在 -GitHubRepo 非空时才用得上）
$GitHubTagPrefix = if ($Platform -eq 'Android') { 'Android' } else { 'PC' }
$GitHubTag = "$GitHubTagPrefix-V$ReleaseVersion"
$GitHubAssetRoot = Join-Path $StagingRoot ("_github-" + $ProductSegment)
if ($GitHubOnly -and $SkipGitHub) {
    throw '发布目标不能同时选择“只发 GitHub”和“跳过 GitHub”。'
}
if ($GitHubOnly -and [string]::IsNullOrWhiteSpace($GitHubRepo)) {
    throw '选择了“发布到 GitHub”，但这个游戏没有配置 GitHub 发布仓库。'
}

$script:GitOutput = [System.Collections.Generic.List[string]]::new()
$script:RebuildBackup = ''

# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------
function Invoke-Git {
    param([string[]]$Arguments, [switch]$AllowFailure)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $lines = @($output | ForEach-Object { [string]$_ })
    if ($code -ne 0 -and -not $AllowFailure) {
        throw ("git {0} 失败（退出码 {1}）：{2}" -f ($Arguments -join ' '), $code, (($lines | Out-String).Trim()))
    }
    return [pscustomobject]@{ ExitCode = $code; Lines = $lines }
}

function Invoke-GitChecked {
    param([string[]]$Arguments)
    Invoke-Git -Arguments $Arguments | Out-Null
}

function Test-TaskCancelled {
    if (Test-AxTaskCancellation) {
        throw [OperationCanceledException]::new('用户已停止游戏发布任务。')
    }
}

# git push / gc 的进度写在 stderr 上，必须边跑边读，否则进度条要等整段上传结束才会动。
function Invoke-GitWithProgress {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Stage,
        [int]$PercentFrom = 0,
        [int]$PercentTo = 100,
        [string]$Message = ''
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 1
    $lastReported = -1
    try {
        & git @Arguments 2>&1 | ForEach-Object {
            $text = ([string]$_).Trim()
            if ([string]::IsNullOrWhiteSpace($text)) { return }
            if ($text -match '^(Enumerating|Counting|Compressing|Writing|Updating|Receiving|Resolving|remote:).*?(\d{1,3})%') {
                $gitPercent = [int]$Matches[2]
                # git 的每个阶段都各自从 0% 跑到 100%，直接映射会让进度条来回跳。
                # 给阶段分权重：枚举/打包 0-8%，压缩 8-28%，真正写数据 28-100%。
                $keyword = $Matches[1]
                $share = switch -Regex ($keyword) {
                    '^(Enumerating|Counting)$' { 0.0 + 0.08 * $gitPercent / 100.0; break }
                    '^Compressing$'           { 0.08 + 0.20 * $gitPercent / 100.0; break }
                    '^(Receiving|Resolving)$' { 0.90 + 0.10 * $gitPercent / 100.0; break }
                    default                   { $gitPercent / 100.0 }
                }
                $percent = [int]($PercentFrom + ($PercentTo - $PercentFrom) * $share)
                # 去重：git 每个阶段都会吐上百行，光是"0% 0% 1% 1%…"就会把日志刷爆。
                if ($percent -eq $lastReported -and $gitPercent -lt 100) { return }
                $lastReported = $percent
                Write-AxTaskProgress `
                    -Stage $Stage `
                    -Percent $percent `
                    -Message ("$Message" + $gitPercent + '%') `
                    -Detail $text
                return
            }

            $script:GitOutput.Add($text)
            [Console]::Out.WriteLine($text)
        }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return $code
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f ($Bytes / 1KB))
}

function Get-DirectorySize {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0L }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Sum Length).Sum
    if ($null -eq $sum) { return 0L }
    return [int64]$sum
}

# ---------------------------------------------------------------------------
# 产物过滤规则
# ---------------------------------------------------------------------------
function Test-ExcludedWindowsRelativePath {
    param([string]$Relative)

    $segments = $Relative -split '/'
    $name = $segments[-1]
    if ($name -like '*.pdb') { return $true }
    if ($name -ieq 'Manifest_DebugFiles_Win64.txt') { return $true }
    foreach ($segment in $segments) {
        if ($segment -ieq '.git' -or $segment -ieq '_download') { return $true }
    }
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        if ($segments[$index] -ieq 'Saved' -and
            ($segments[$index + 1] -ieq 'Logs' -or $segments[$index + 1] -ieq 'Crashes')) {
            return $true
        }
    }
    return $false
}

function Test-IsAndroidReleaseFile {
    param([string]$Name)

    # 只发"玩家真正需要"的东西：主 APK + OBB 里的条目（见 Expand-AndroidObb）。
    # 排除掉打包目录里的符号、AFS 调试工具、安装/卸载 bat（旧发布流程也是这么过滤的）。
    # OBB 本身【不发】：它就是一个不压缩 ZIP，把里面每个条目当独立文件发出去，
    # 文件级清单才能给出"只下变化的 chunk"（否则改一个 chunk = 重下整个 2 GB OBB）。
    if ($Name -match '^main\.\d+\..+\.obb$') { return $false }
    if ($Name.StartsWith('AFS_', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($Name -match '-Android-Shipping-.+\.apk$') { return $true }
    return $false
}

# 把安卓 OBB 里的每个条目解成一个独立文件（不压缩 ZIP，条目路径就是工程内路径）。
# 解到 $StagingRoot 下的临时目录（和暂存仓库同一个盘 → 后面能直接硬链接，不额外占空间）。
function Expand-AndroidObb {
    param(
        [Parameter(Mandatory)][string]$ObbPath,
        [Parameter(Mandatory)][string]$UnpackRoot
    )

    # 这一段以前是"静默"的：2 GB 的 OBB 展开要跑一两分钟，界面上却一直停在 0%，
    # 看起来像卡死（2026-09-20 用户报过一次）。现在把展开进度也上报（2% → 6%）。
    if (Test-Path -LiteralPath $UnpackRoot) {
        Write-AxTaskProgress -Stage 'scan' -Percent 1 -Message '正在清理上一次展开的 OBB 临时目录' -Detail $UnpackRoot
        Remove-Item -LiteralPath $UnpackRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $UnpackRoot -Force | Out-Null

    $entries = [System.Collections.Generic.List[object]]::new()
    $zip = [IO.Compression.ZipFile]::OpenRead($ObbPath)
    try {
        $totalEntries = @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }).Count
        $doneEntries = 0
        $lastPercent = -1
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }   # 目录条目
            if (($doneEntries % 5) -eq 0) { Test-TaskCancelled }
            $relative = $entry.FullName.Replace('\', '/')
            if ($relative.StartsWith('/') -or $relative -match '^[A-Za-z]:' -or ($relative -split '/') -contains '..') {
                throw "OBB 里的路径不合法，拒绝解包：$relative"
            }
            $target = Join-Path $UnpackRoot ($relative -replace '/', '\')
            $directory = Split-Path -Parent $target
            if (-not [string]::IsNullOrWhiteSpace($directory)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $entries.Add([pscustomobject]@{
                Relative = $relative
                FullName = $target
                Length   = [int64](Get-Item -LiteralPath $target -Force).Length
            })
            $doneEntries++
            $percent = if ($totalEntries -gt 0) { [int](2 + 4.0 * $doneEntries / $totalEntries) } else { 2 }
            if ($percent -ne $lastPercent) {
                $lastPercent = $percent
                Write-AxTaskProgress `
                    -Stage 'scan' `
                    -Percent $percent `
                    -Message "正在展开 OBB 条目 $doneEntries/$totalEntries" `
                    -Detail $relative
            }
        }
    }
    finally {
        $zip.Dispose()
    }
    return @($entries | Sort-Object -Property Relative -CaseSensitive)
}

# ---------------------------------------------------------------------------
# 扫描 + 哈希
# ---------------------------------------------------------------------------
function Get-ReleaseFileList {
    param([string]$Root, [string]$Platform)

    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -ieq $VersionMarkerName) { continue }
        if ($Platform -eq 'Android') {
            if (-not (Test-IsAndroidReleaseFile -Name $file.Name)) { continue }
        }
        elseif (Test-ExcludedWindowsRelativePath -Relative $relative) {
            continue
        }
        $files.Add([pscustomobject]@{
            Relative = $relative
            FullName = $file.FullName
            Length   = [int64]$file.Length
        })
    }

    return @($files | Sort-Object -Property Relative -Culture ([Globalization.CultureInfo]::InvariantCulture) -CaseSensitive)
}

function Get-HashedFileList {
    param([object[]]$Files)

    $result = [System.Collections.Generic.List[object]]::new()
    $total = $Files.Count
    $index = 0
    $lastPercent = 6
    foreach ($file in $Files) {
        if (($index % 20) -eq 0) { Test-TaskCancelled }
        $index++
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $result.Add([pscustomobject]@{
            path      = $file.Relative
            sizeBytes = $file.Length
            sha256    = $hash
            FullName  = $file.FullName
        })
        $percent = [int](6 + (26.0 * $index / [Math]::Max($total, 1)))
        if ($percent - $lastPercent -ge 1) {
            $lastPercent = $percent
            Write-AxTaskProgress `
                -Stage 'scan' `
                -Percent $percent `
                -Message "正在计算文件哈希 $index/$total" `
                -Detail $file.Relative
        }
    }
    return @($result)
}

function Get-ManifestSnapshot {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        $parsed = $raw | ConvertFrom-Json
        # generatedAt 必须按"原文"沿用：ConvertFrom-Json 会把它变成 DateTime，
        # 再转回字符串就成了本地化格式（2026/9/20 3:04:04），清单字节每次都变，
        # 幂等（无改动 = 0 字节）就永远不成立。
        $generatedAt = ''
        if ($raw -match '"generatedAt"\s*:\s*"([^"]*)"') { $generatedAt = $Matches[1] }
        return [pscustomobject]@{ Json = $parsed; GeneratedAt = $generatedAt }
    }
    catch {
        return $null
    }
}

function Get-ChangeSummary {
    param([object]$Previous, [object[]]$Files)

    $map = @{}
    foreach ($file in $Files) { $map[$file.path] = $file }

    $previousFiles = if ($null -eq $Previous) { $null } else { @($Previous.Json.files) }
    if ($null -eq $previousFiles -or $previousFiles.Count -eq 0) {
        $bytes = 0L
        foreach ($file in $Files) { $bytes += $file.sizeBytes }
        return [pscustomobject]@{
            HasBaseline = $false
            Added       = $Files.Count
            Updated     = 0
            Removed     = 0
            TransferBytes = $bytes
            Note        = "暂存仓库里还没有这个版本的清单，本次按全量处理。"
        }
    }

    $seen = @{}
    $added = 0
    $updated = 0
    $removed = 0
    $transferBytes = 0L
    foreach ($file in $Files) {
        $old = $null
        foreach ($candidate in $previousFiles) {
            if ([string]$candidate.path -eq $file.path) { $old = $candidate; break }
        }
        if ($null -eq $old) {
            $added++
            $transferBytes += $file.sizeBytes
            continue
        }
        $seen[$file.path] = $true
        if ([string]$old.sha256 -ne $file.sha256) {
            $updated++
            $transferBytes += $file.sizeBytes
        }
    }
    foreach ($candidate in $previousFiles) {
        if ([string]$candidate.path -eq $VersionMarkerName) { continue }
        if (-not $seen.ContainsKey([string]$candidate.path)) { $removed++ }
    }

    return [pscustomobject]@{
        HasBaseline   = $true
        Added         = $added
        Updated       = $updated
        Removed       = $removed
        TransferBytes = $transferBytes
        Note          = "与仓库里 $ReleaseVersion 的现有清单逐文件比对（只比 sha256）。"
    }
}

# 清单里只有 generatedAt 是"每次都不一样"的字段。内容完全没变时沿用它，
# 这样同一版重跑时清单字节不变 → 连提交都不需要做（幂等）。
function Test-ManifestEquivalent {
    param(
        [object]$Previous,
        [System.Collections.IDictionary]$Manifest
    )

    if ($null -eq $Previous) { return $false }
    $previous = $Previous.Json
    if ($null -eq $previous) { return $false }
    foreach ($field in @('schemaVersion', 'productKey', 'runtime', 'version', 'channel', 'baseUrl')) {
        if ([string]$previous.$field -ne [string]$Manifest[$field]) { return $false }
    }

    $previousFiles = @($previous.files)
    $newFiles = @($Manifest['files'])
    if ($previousFiles.Count -ne $newFiles.Count) { return $false }
    for ($index = 0; $index -lt $newFiles.Count; $index++) {
        if ([string]$previousFiles[$index].path -ne [string]$newFiles[$index]['path']) { return $false }
        if ([int64]$previousFiles[$index].sizeBytes -ne [int64]$newFiles[$index]['sizeBytes']) { return $false }
        if ([string]$previousFiles[$index].sha256 -ne [string]$newFiles[$index]['sha256']) { return $false }
    }
    return $true
}

function Test-IndexMatchesHead {
    $result = Invoke-Git -Arguments @('-C', $RepoRoot, 'diff', '--cached', '--quiet', 'HEAD') -AllowFailure
    return $result.ExitCode -eq 0
}

# ---------------------------------------------------------------------------
# 同步到本机暂存仓库
# ---------------------------------------------------------------------------
function Sync-VersionDirectory {
    param([object[]]$Files)

    if (-not (Test-Path -LiteralPath $VersionDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $VersionDirectory -Force | Out-Null
    }

    # 暂存目录里的那份文件优先用硬链接从打包产物"挂"过来：同一个数据块两个名字，
    # 等于不占额外空间，而 git add / 本地翻看照常能用（比复制省一整个版本的体积）。
    # 跨盘或文件系统不支持时自动退回复制。
    $stagingVolume = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($VersionDirectory))

    $existing = @{}
    foreach ($file in (Get-ChildItem -LiteralPath $VersionDirectory -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $relative = [IO.Path]::GetRelativePath($VersionDirectory, $file.FullName).Replace('\', '/')
        $existing[$relative] = $file
    }

    $total = $Files.Count
    $index = 0
    $copied = 0
    $linked = 0
    $skipped = 0
    $lastPercent = 36
    foreach ($file in $Files) {
        if (($index % 20) -eq 0) { Test-TaskCancelled }
        $index++
        $target = Join-Path $VersionDirectory ($file.path -replace '/', '\')
        $source = Get-Item -LiteralPath $file.FullName -Force
        $needsWrite = $true
        if ($existing.ContainsKey($file.path)) {
            $current = Get-Item -LiteralPath $target -Force
            # 已经是硬链接 → 内容就是打包产物本身，连哈希都不用算。
            if ($current.Length -eq $source.Length -and $current.LinkType -eq 'HardLink') {
                $needsWrite = $false
            }
            # 先比大小（快），大小一样再比 sha256。只看"大小 + 时间戳"会漏掉
            # "重新打包后大小没变、内容全变了"那种情况（这正是零境重新打包时发生过的）。
            elseif ($current.Length -eq $source.Length -and
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256) {
                # 内容一样，但只是普通副本：同盘就换成硬链接，把这份重复数据省掉；
                # 跨盘没法链接，保持原样（下次也不会重复搬）。
                $needsWrite = ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($source.FullName)) -eq $stagingVolume)
            }
        }
        if ($needsWrite) {
            $directory = Split-Path -Parent $target
            if (-not [string]::IsNullOrWhiteSpace($directory)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }
            $linkedThisFile = $false
            if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($source.FullName)) -eq $stagingVolume) {
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
                try {
                    New-Item -ItemType HardLink -Path $target -Target $source.FullName -Force -ErrorAction Stop | Out-Null
                    $linkedThisFile = $true
                }
                catch {
                    # 例如目标在 ReFS / 网络路径上不支持硬链接 → 退回复制。
                    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                }
            }
            if ($linkedThisFile) { $linked++ }
            else {
                Copy-Item -LiteralPath $source.FullName -Destination $target -Force
                $copied++
            }
        }
        else {
            $skipped++
        }

        $percent = [int](36 + (18.0 * $index / [Math]::Max($total, 1)))
        if ($percent - $lastPercent -ge 1) {
            $lastPercent = $percent
            Write-AxTaskProgress `
                -Stage 'stage' `
                -Percent $percent `
                -Message "正在同步到本机仓库 $index/$total（硬链接 $linked · 复制 $copied · 跳过 $skipped）" `
                -Detail $file.path
        }
    }

    # 产物里已经没有的文件要删掉：否则旧 pak / 旧 dll 会一直留在下载站上。
    $keep = @{}
    foreach ($file in $Files) { $keep[$file.path] = $true }
    $removed = 0
    foreach ($relative in $existing.Keys) {
        if ($keep.ContainsKey($relative)) { continue }
        # 版本标记文件紧接着会被重写，不算"产物里已删除"。
        if ($relative -ieq $VersionMarkerName) { continue }
        Remove-Item -LiteralPath $existing[$relative].FullName -Force -ErrorAction SilentlyContinue
        $removed++
    }
    foreach ($directory in (Get-ChildItem -LiteralPath $VersionDirectory -Recurse -Directory -Force -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending)) {
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    return [pscustomobject]@{ Linked = $linked; Copied = $copied; Skipped = $skipped; Removed = $removed }
}

# 树里只留当前版本：更早的版本目录和更早的清单都物理删掉（上一版在服务端有硬链接归档）。
function Remove-StaleVersions {
    $productDirectory = Join-Path $RepoRoot (("games/$ProductSegment") -replace '/', '\')
    if (-not (Test-Path -LiteralPath $productDirectory -PathType Container)) { return 0 }

    $removed = 0
    foreach ($directory in (Get-ChildItem -LiteralPath $productDirectory -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($directory.Name -ieq 'manifests') { continue }
        if ($directory.Name -ieq $ReleaseVersion) { continue }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
        Write-AxTaskStage -Stage 'stage' -Message "已从树里移除更早的版本：$($directory.Name)" -Detail $directory.FullName
        $removed++
    }

    if (Test-Path -LiteralPath $ManifestDirectory -PathType Container) {
        foreach ($file in (Get-ChildItem -LiteralPath $ManifestDirectory -File -Force -ErrorAction SilentlyContinue)) {
            if ($file.Name -ieq "$ReleaseVersion.json") { continue }
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
    return $removed
}

# ---------------------------------------------------------------------------
# git 仓库
# ---------------------------------------------------------------------------
function Initialize-StagingRepository {
    param([switch]$Reinitialize)

    if ($Reinitialize -and (Test-Path -LiteralPath (Join-Path $RepoRoot '.git') -PathType Container)) {
        # 备份必须放在仓库目录**外面**：放在里面会被紧接着的 `git add -A` 当成普通目录一起提交。
        $backup = Join-Path $StagingRoot (".$ProductSegment.git-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath (Join-Path $RepoRoot '.git') -Destination $backup -Force
        Write-AxTaskStage -Stage 'commit' -Message '已把旧的 .git 挪到仓库外面（推送成功后再删）' -Detail $backup
        $script:RebuildBackup = $backup
    }

    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $RepoRoot -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git') -PathType Container)) {
        Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'init', '-b', 'main')
    }

    # 身份固定写死，避免依赖这台机器的全局 git 配置。
    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'config', 'user.name', 'AxTools')
    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'config', 'user.email', 'axtools@crossingvoid.local')
    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'config', 'core.autocrlf', 'false')
    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'config', 'gc.auto', '0')
    # 不要再打开 push.negotiate：它要求 protocol v2，而 git 的 **推送**（receive-pack）走的是 v1，
    # 只会打出 "warning: --negotiate-only requires protocol v2" 然后什么也不做（实测 2026-09-20）。
    Invoke-Git -AllowFailure -Arguments @('-C', $RepoRoot, 'config', '--unset-all', 'push.negotiate') | Out-Null

    $remotes = Invoke-Git -Arguments @('-C', $RepoRoot, 'remote')
    $hasOrigin = @($remotes.Lines | Where-Object { $_.Trim() -eq 'origin' }).Count -gt 0
    if ($hasOrigin) {
        Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'remote', 'set-url', 'origin', $UploadTarget)
    }
    else {
        Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'remote', 'add', 'origin', $UploadTarget)
    }
}

function Get-HeadCommitMessage {
    $result = Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--verify', 'HEAD') -AllowFailure
    return $result.ExitCode -eq 0
}

# 推送前把"服务端当前那版"取到本地当协商基准（refs/remotes/origin/<分支>）。
# 为什么必须做：git 推送时的 haves 只从"客户端已有的引用"里出题，服务端能 ACK 的那些对象
# 才会被打包排除。本地暂存仓库如果连"服务端那条提交"都没有，git 一个对象都排除不掉 ⇒ 全量重传。
# 代价极小：客户端通常刚推过同一批内容，fetch 实际不下载任何东西（没有的东西才下载）。
# 只有刚重建过仓库（HEAD 还不存在）时才跳过——那次本来就是全量。
function Sync-RemoteTipForPush {
    $head = Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--verify', 'HEAD') -AllowFailure
    if ($head.ExitCode -ne 0) { return }

    $branch = (Invoke-Git -Arguments @('-C', $RepoRoot, 'symbolic-ref', '--short', 'HEAD')).Lines[0].Trim()
    $fetchArgs = @('-C', $RepoRoot, 'fetch', '--no-tags', 'origin', "+refs/heads/$branch`:refs/remotes/origin/$branch")
    $fetched = Invoke-Git -Arguments $fetchArgs -AllowFailure
    if ($fetched.ExitCode -ne 0) {
        Write-AxTaskProgress `
            -Stage 'commit' `
            -Percent 60 `
            -Message '未能预取服务端基准（不影响发布，只是这次可能全量上传）' `
            -Detail (($fetched.Lines | Out-String).Trim())
        return
    }

    $tip = (Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--short', "refs/remotes/origin/$branch")).Lines[0].Trim()
    Write-AxTaskProgress -Stage 'commit' -Percent 60 -Message "已对齐服务端基准：$tip（推送只发变化的文件）"
}

function Invoke-CommitVersion {
    param([string]$Message)

    # 用 `commit-tree` 写提交（不用 `git commit --amend`：amend 会把父提交链保留在历史里，
    # 而我们要的是"每版一个新提交、父提交就是上一版"）。
    # **必须带 -p**：无父提交会让 git 推送时找不到共同祖先，导致每次全量重传（详见文件头说明）。
    $parent = $null
    $head = Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--verify', 'HEAD') -AllowFailure
    if ($head.ExitCode -eq 0 -and $head.Lines.Count -gt 0) {
        $parent = $head.Lines[0].Trim()
    }

    $branch = (Invoke-Git -Arguments @('-C', $RepoRoot, 'symbolic-ref', '--short', 'HEAD')).Lines[0].Trim()
    $tree = (Invoke-Git -Arguments @('-C', $RepoRoot, 'write-tree')).Lines[0].Trim()

    # 提交信息走文件：中文和空格在命令行上容易被改写。
    $messagePath = Join-Path ([IO.Path]::GetTempPath()) ("axtools-game-commit-" + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        [IO.File]::WriteAllText($messagePath, $Message, [Text.UTF8Encoding]::new($false))
        $commitArguments = @('-C', $RepoRoot, 'commit-tree', $tree)
        if ($parent) { $commitArguments += @('-p', $parent) }
        $commitArguments += @('-F', $messagePath)
        $commit = (Invoke-Git -Arguments $commitArguments).Lines[-1].Trim()
    }
    finally {
        Remove-Item -LiteralPath $messagePath -Force -ErrorAction SilentlyContinue
    }
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        throw "创建提交失败，git commit-tree 返回了意外内容：$commit"
    }
    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'update-ref', "refs/heads/$branch", $commit)
    return $commit
}

# ---------------------------------------------------------------------------
# 公网校验
# ---------------------------------------------------------------------------
function Invoke-DownloadSiteRequest {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [switch]$Head,
        [int]$TimeoutSec = 30
    )

    # 下载站是公网 HTTPS，直连就能通。这台机器上"系统代理"经常是关着的
    # （用户的代理工具是开关型的），而 Invoke-WebRequest 默认会走系统代理 →
    # 代理一关就报 "The SSL connection could not be established"（2026-09-20 实测踩过）。
    # 所以先直连，直连失败再退回系统代理（万一所在网络必须走代理）。
    $lastError = $null
    foreach ($directFirst in @($true, $false)) {
        $request = @{ Uri = $Uri; TimeoutSec = $TimeoutSec }
        if ($Head) { $request.Method = 'Head' }
        if ($directFirst) { $request.NoProxy = $true }
        try {
            return (Invoke-WebRequest @request)
        }
        catch {
            $lastError = $_
        }
    }
    throw $lastError
}

function Invoke-PublicVerify {
    param(
        [object[]]$Files,
        [Parameter(Mandatory)][int]$ExpectedFileCount
    )

    $manifestUrl = '{0}/games/{1}/manifests/{2}.json' -f $PublicBaseUrl.TrimEnd('/'), $ProductSegment, $ReleaseVersion
    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $manifest = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $response = Invoke-DownloadSiteRequest -Uri "$manifestUrl`?t=$cacheBuster"
            $manifest = $response.Content | ConvertFrom-Json
            break
        }
        catch {
            if ($attempt -eq 5) { throw "公网清单拉取失败：$manifestUrl（$($_.Exception.Message)）" }
            Start-Sleep -Seconds 3
        }
    }
    if ([string]$manifest.version -ne $ReleaseVersion) {
        throw "公网清单版本不符：期望 $ReleaseVersion，实际 $($manifest.version)"
    }
    $remoteCount = @($manifest.files).Count
    if ($remoteCount -ne $ExpectedFileCount) {
        throw "公网清单文件数不符：期望 $ExpectedFileCount，实际 $remoteCount"
    }

    # 抽样：最大的 3 个 + 最小的 2 个（覆盖"大文件真的传上去了"和"小文件也没漏"）。
    $ordered = @($Files | Sort-Object -Property sizeBytes -Descending)
    $samples = [System.Collections.Generic.List[object]]::new()
    foreach ($file in ($ordered | Select-Object -First 3)) { $samples.Add($file) }
    foreach ($file in ($ordered | Select-Object -Last 2)) {
        if (-not ($samples | Where-Object { $_.path -eq $file.path })) { $samples.Add($file) }
    }

    $checked = 0
    foreach ($file in $samples) {
        $url = $BaseUrl + $file.path
        $ok = $false
        $detail = ''
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                $head = Invoke-DownloadSiteRequest -Uri $url -Head
                # HEAD 没有响应体，RawContentLength 恒为 0；字节数只能从响应头读。
                $lengthHeader = @($head.Headers['Content-Length'])
                $length = if ($lengthHeader.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$lengthHeader[0])) {
                    [int64]$lengthHeader[0]
                }
                else {
                    [int64]$head.RawContentLength
                }
                if ($length -ne $file.sizeBytes) {
                    throw "字节数不符：公网 $length，本地 $($file.sizeBytes)"
                }
                $ok = $true
                break
            }
            catch {
                $detail = $_.Exception.Message
                if ($attempt -lt 5) { Start-Sleep -Seconds 3 }
            }
        }
        if (-not $ok) { throw "公网抽样校验失败：$url（$detail）" }
        $checked++
        Write-AxTaskProgress `
            -Stage 'verify' `
            -Percent (95 + [int](4.0 * $checked / [Math]::Max($samples.Count, 1))) `
            -Message "公网抽样校验 $checked/$($samples.Count)" `
            -Detail $url
    }

    return [pscustomobject]@{ ManifestUrl = $manifestUrl; Files = $remoteCount; SampledFiles = $checked }
}

# ---------------------------------------------------------------------------
# 逐文件同步（files 模式，2026-09-20 起默认）
# ---------------------------------------------------------------------------
# 和 git 那条路的区别：不做对象打包，只把"和上一版不一样的文件"传上去，其余由服务端
# 从上一版硬链接过来（内容没变 ⇒ 0 字节）。对外契约不变：目录还是 games/<段>/<版本>/…，
# 清单还是 schemaVersion 1 —— 启动器那边一行都不用改。

function Invoke-FileSyncSsh {
    param([Parameter(Mandatory)][string]$Command, [string]$Detail = '')

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & ssh $FileSyncTarget $Command 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $lines = @($output | ForEach-Object { [string]$_ })
    if ($code -ne 0) {
        $shown = if ([string]::IsNullOrWhiteSpace($Detail)) { $Command } else { $Detail }
        throw ("服务端命令失败（退出码 {0}）：{1}`n{2}" -f $code, $shown, (($lines | Out-String).Trim()))
    }
    return $lines
}

# 这条香港线路的 sftp 会在 ~1.1 MB 处 broken pipe（2026-10-19 实测），
# 但 scp -O（旧协议）能完整上传。所有大文件改走这个函数，失败重试 3 次。
function Invoke-FileSyncScp {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$RemotePath,
        [string]$Detail = ''
    )

    $target = '{0}:{1}' -f $FileSyncTarget, $RemotePath
    # Windows PowerShell 5.1 调 scp 时，中文目录（F:\DaBaoV\下载站）按 ANSI 传参会乱码，
    # 所以切到源文件所在目录，只把 ASCII 文件名交给 scp。
    $sourceDirectory = Split-Path -Parent $LocalPath
    $sourceName = Split-Path -Leaf $LocalPath
    $lastError = ''
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        Push-Location -LiteralPath $sourceDirectory
        try {
            $output = & scp -O -q -o BatchMode=yes -o ConnectTimeout=15 `
                -o ServerAliveInterval=10 -o ServerAliveCountMax=3 $sourceName $target 2>&1
            $code = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $ErrorActionPreference = $previous
        }

        if ($code -eq 0) { return }
        $lastError = (($output | ForEach-Object { [string]$_ } | Out-String).Trim())
        if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
    }

    $shown = if ([string]::IsNullOrWhiteSpace($Detail)) { $RemotePath } else { $Detail }
    throw ("scp 上传失败：{0}`n{1}" -f $shown, $lastError)
}

# 远端已完成的文件大小表：只用于跳过上次中断前已经传完的文件。
# .axtmp 不参与跳过，半截临时文件会在这次重新覆盖。
function Get-RemoteFileSizeMap {
    param([Parameter(Mandatory)][string]$VersionRoot)

    $map = @{}
    $lines = Invoke-FileSyncSsh `
        -Command ("if [ -d '{0}' ]; then cd '{0}' && find . -type f ! -name '*.axtmp' -printf '%P\t%s\n'; fi" -f $VersionRoot) `
        -Detail '读取远端已完成文件大小'
    foreach ($line in $lines) {
        $parts = ([string]$line) -split "`t", 2
        if ($parts.Count -ne 2) { continue }
        $size = [int64]0
        if ([int64]::TryParse($parts[1], [ref]$size)) { $map[$parts[0]] = $size }
    }
    return $map
}

function Get-OnlinePublishedVersion {
    $cacheBuster = [DateTimeOffset]::Now.ToUnixTimeSeconds()
    try {
        $response = Invoke-DownloadSiteRequest -Uri "$PublicBaseUrl/games/$ProductSegment/latest.json?t=$cacheBuster"
        $json = $response.Content | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace([string]$json.version)) { return [string]$json.version }
    }
    catch {
        # 还没有 latest.json（迁移期的老数据）⇒ 退回读清单目录索引
    }

    try {
        $response = Invoke-DownloadSiteRequest -Uri "$PublicBaseUrl/games/$ProductSegment/manifests/?t=$cacheBuster"
        $versions = [System.Collections.Generic.List[string]]::new()
        foreach ($match in [regex]::Matches([string]$response.Content, 'href="(?<name>[^"/]+)\.json"')) {
            $versions.Add($match.Groups['name'].Value)
        }
        if ($versions.Count -eq 0) { return '' }
        return ($versions |
            Sort-Object -Property { try { [version]($_ -replace '[^0-9.].*$', '') } catch { [version]'0.0' } } -Descending |
            Select-Object -First 1)
    }
    catch {
        return ''
    }
}

function Get-OnlineManifest {
    param([Parameter(Mandatory)][string]$Version)

    try {
        $uri = "$PublicBaseUrl/games/$ProductSegment/manifests/$Version.json?t=$([DateTimeOffset]::Now.ToUnixTimeSeconds())"
        $response = Invoke-DownloadSiteRequest -Uri $uri
        return ($response.Content | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Invoke-FileSyncPublish {
    param([Parameter(Mandatory)][object]$ManifestFiles)

    $previousVersion = Get-OnlinePublishedVersion
    $previousManifest = if ([string]::IsNullOrWhiteSpace($previousVersion)) { $null } else { Get-OnlineManifest -Version $previousVersion }
    $previousHashes = @{}
    if ($null -ne $previousManifest -and $null -ne $previousManifest.files) {
        foreach ($file in $previousManifest.files) {
            $previousHashes[[string]$file.path] = ([string]$file.sha256).ToLowerInvariant()
        }
    }

    $changed = [System.Collections.Generic.List[object]]::new()
    $changedBytes = [int64]0
    foreach ($file in $ManifestFiles) {
        $path = [string]$file.path
        $hash = ([string]$file.sha256).ToLowerInvariant()
        if ($previousHashes.ContainsKey($path) -and $previousHashes[$path] -eq $hash) { continue }
        $changed.Add($file)
        $changedBytes += [int64]$file.sizeBytes
    }

    $previousLabel = if ([string]::IsNullOrWhiteSpace($previousVersion)) { '线上还没有上一版' } else { "上一版：$previousVersion" }
    Write-AxTaskProgress `
        -Stage 'stage' `
        -Percent 56 `
        -Message ("逐文件比对：要上传 {0} 个 / {1}；和上一版相同的 {2} 个由服务端硬链接（0 字节）" -f `
            $changed.Count, (Format-Size -Bytes $changedBytes), ($ManifestFiles.Count - $changed.Count)) `
        -Detail $previousLabel

    if ($DryRun) {
        Write-AxTaskStage `
            -Stage 'push' `
            -Message '演练模式：跳过上传与落位' `
            -Detail ("sftp → {0}:/srv/downloads/games/{1}/{2}/" -f $FileSyncTarget, $ProductSegment, $ReleaseVersion)
        return $false
    }

    # 1) 服务端先把这一版的目录准备好：从上一版硬链接（内容相同的文件一个字节都不用传）
    Write-AxTaskProgress -Stage 'stage' -Percent 57 -Message '正在让服务端准备版本目录（从上一版硬链接）'
    $prepareResult = Invoke-FileSyncSsh `
        -Command ("/srv/scripts/game-file-sync.sh prepare {0} {1} {2}" -f $ProductSegment, $ReleaseVersion, $previousVersion) `
        -Detail 'game-file-sync.sh prepare'
    foreach ($line in $prepareResult) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-AxTaskProgress -Stage 'stage' -Percent 57 -Message $line.Trim()
        }
    }

    # 2) 传变化的文件 + 清单：SCP 到临时名，最后原子改名（启动器不会读到半截文件）
    #    不能用 sftp -b：这条香港线路在 1.1 MB 左右会 broken pipe；scp -O 稳定。
    $versionRoot = "/srv/downloads/games/$ProductSegment/$ReleaseVersion"
    $manifestRemote = "/srv/downloads/games/$ProductSegment/manifests/$ReleaseVersion.json"
    $manifestTemp = "$manifestRemote.axtmp"

    # 目录一次建好；game-file-sync.sh prepare 通常已经建了，重复 mkdir -p 无害。
    $remoteDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$remoteDirs.Add("/srv/downloads/games/$ProductSegment/manifests")
    foreach ($file in $changed) {
        $directory = Split-Path -Parent ([string]$file.path)
        while (-not [string]::IsNullOrWhiteSpace($directory)) {
            [void]$remoteDirs.Add("$versionRoot/$($directory -replace '\\', '/')")
            $directory = Split-Path -Parent $directory
        }
    }
    $mkdirCommand = "mkdir -p " + (($remoteDirs | ForEach-Object { "'" + $_ + "'" }) -join ' ')
    [void](Invoke-FileSyncSsh -Command $mkdirCommand -Detail '创建远端目录')

    $remoteSizes = Get-RemoteFileSizeMap -VersionRoot $versionRoot
    $pairs = [System.Collections.Generic.List[object]]::new()
    $renameCommands = [System.Collections.Generic.List[string]]::new()
    $bytesDone = [int64]0
    $fileIndex = 0
    foreach ($file in $changed) {
        $fileIndex++
        $relativePath = ([string]$file.path) -replace '/', '\'
        $localPath = Join-Path $VersionDirectory $relativePath
        if (-not (Test-Path -LiteralPath $localPath -PathType Leaf)) {
            throw "清单里有文件但暂存目录里没有：$localPath"
        }

        $remotePath = "$versionRoot/$([string]$file.path)"
        $size = [int64](Get-Item -LiteralPath $localPath).Length
        $pair = [pscustomobject]@{
            Relative = [string]$file.path
            Sha256   = ([string]$file.sha256).ToLowerInvariant()
        }
        $pairs.Add($pair)

        if ($remoteSizes.ContainsKey([string]$file.path) -and [int64]$remoteSizes[[string]$file.path] -eq $size) {
            $bytesDone += $size
            $percent = 60 + [int](33 * $bytesDone / [Math]::Max(1, $changedBytes))
            Write-AxTaskProgress `
                -Stage 'push' `
                -Percent ([Math]::Min(93, $percent)) `
                -Message ("正在上传 {0}/{1}：已存在，跳过 {2}" -f $fileIndex, $changed.Count, $file.path) `
                -Detail $remotePath
            continue
        }

        $tempPath = "$remotePath.axtmp"
        Write-AxTaskProgress `
            -Stage 'push' `
            -Percent ([Math]::Min(93, 60 + [int](33 * $bytesDone / [Math]::Max(1, $changedBytes)))) `
            -Message ("正在上传 {0}/{1}：{2}（{3}）" -f $fileIndex, $changed.Count, $file.path, (Format-Size -Bytes $size)) `
            -Detail $remotePath
        Invoke-FileSyncScp -LocalPath $localPath -RemotePath $tempPath -Detail $file.path
        $renameCommands.Add(("mv -f -- '{0}' '{1}'" -f $tempPath, $remotePath))

        $bytesDone += $size
        $percent = 60 + [int](33 * $bytesDone / [Math]::Max(1, $changedBytes))
        Write-AxTaskProgress `
            -Stage 'push' `
            -Percent ([Math]::Min(93, $percent)) `
            -Message ("已上传 {0}/{1}：{2}" -f $fileIndex, $changed.Count, $file.path) `
            -Detail ("累计 {0} / {1}" -f (Format-Size -Bytes $bytesDone), (Format-Size -Bytes $changedBytes))
    }

    # 清单最后传，和文件一起改名；清单没落位前启动器不会认为这一版可用。
    Write-AxTaskProgress -Stage 'push' -Percent 93 -Message '正在上传清单（最后一步）' -Detail $manifestRemote
    Invoke-FileSyncScp -LocalPath $ManifestPath -RemotePath $manifestTemp -Detail 'fragments manifest'
    $renameCommands.Add(("mv -f -- '{0}' '{1}'" -f $manifestTemp, $manifestRemote))

    if ($renameCommands.Count -gt 0) {
        [void](Invoke-FileSyncSsh -Command ($renameCommands -join ' && ') -Detail '原子改名')
    }
    Write-AxTaskProgress `
        -Stage 'push' `
        -Percent 94 `
        -Message ("上传完成：{0} 个文件 / {1}" -f $changed.Count, (Format-Size -Bytes $changedBytes)) `
        -Detail $versionRoot

    # 3) 服务端收尾：归档上一版 → 树里只留当前版 → 写 latest.json
    $finalizeResult = Invoke-FileSyncSsh `
        -Command ("/srv/scripts/game-file-sync.sh finalize {0} {1} {2}" -f $ProductSegment, $ReleaseVersion, $previousVersion) `
        -Detail 'game-file-sync.sh finalize'
    foreach ($line in $finalizeResult) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-AxTaskProgress -Stage 'push' -Percent 94 -Message $line.Trim()
        }
    }

    # 4) 上传后核对 sha256（只查这次传过的文件，服务端算）
    if ($pairs.Count -gt 0) {
        $checkList = ($pairs | ForEach-Object { "'$versionRoot/$($_.Relative)'" }) -join ' '
        $actual = @{}
        foreach ($line in (Invoke-FileSyncSsh -Command "cd /srv/downloads && sha256sum $checkList" -Detail 'sha256sum 抽查')) {
            $parts = ([string]$line).Trim() -split '\s+', 2
            if ($parts.Count -eq 2) { $actual[$parts[1]] = $parts[0].ToLowerInvariant() }
        }
        $bad = [System.Collections.Generic.List[string]]::new()
        foreach ($pair in $pairs) {
            $key = "$versionRoot/$($pair.Relative)"
            if (-not $actual.ContainsKey($key) -or $actual[$key] -ne $pair.Sha256) { $bad.Add($pair.Relative) }
        }
        if ($bad.Count -gt 0) {
            throw ("上传后校验失败，这些文件和服务端不一致：`n  " + ($bad -join "`n  "))
        }
        Write-AxTaskProgress -Stage 'push' -Percent 95 -Message ("上传后校验通过：{0} 个文件的 sha256 与服务端一致" -f $pairs.Count)
    }

    return $true
}

# 本机仓库只留"当前这一版"的对象。发布成功后上一版的 blob 就不可达了，这里把它清掉，
# 否则 .git 会一版一版地翻倍长下去（用户明确要求：本地不要存两份）。
# 不能用 `git gc`：它走 `repack -A`，会把不可达对象留住（服务端踩过同一个坑）。
# 默认走便宜路径（prune 只删松散对象，几乎瞬间）；体积还是降不下来才做一次完整 repack。
function Invoke-LocalRepoMaintenance {
    param([string]$Reason)

    $before = Get-DirectorySize -Path (Join-Path $RepoRoot '.git')
    Write-AxTaskStage `
        -Stage 'cleanup' `
        -Message "正在回收本机仓库的旧对象（$Reason）" `
        -Detail "回收前 $([Math]::Round($before / 1GB, 2)) GB"

    try {
        Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'reflog', 'expire', '--expire=now', '--expire-unreachable=now', '--all')
        Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'prune', '--expire=now')
        $afterPrune = Get-DirectorySize -Path (Join-Path $RepoRoot '.git')

        $treeSize = Get-DirectorySize -Path (Join-Path $RepoRoot 'games')
        $threshold = [int64]($treeSize * 1.4) + 100MB
        $repacked = $false
        if ($treeSize -gt 0 -and $afterPrune -gt $threshold) {
            Write-AxTaskProgress `
                -Stage 'cleanup' `
                -Percent 97 `
                -Message "旧对象已经被打进包里，松散对象回收不掉；正在做一次完整整理（这一步要几分钟）…" `
                -Detail "当前 $([Math]::Round($afterPrune / 1GB, 2)) GB，内容 $([Math]::Round($treeSize / 1GB, 2)) GB"
            Invoke-GitChecked -Arguments @(
                '-C', $RepoRoot, '-c', 'pack.windowMemory=256m', '-c', 'pack.threads=4',
                'repack', '-a', '-d')
            Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'prune', '--expire=now')
            $repacked = $true
        }

        $after = Get-DirectorySize -Path (Join-Path $RepoRoot '.git')
        $suffix = if ($repacked) { '，含一次完整整理' } else { '' }
        Write-AxTaskProgress `
            -Stage 'cleanup' `
            -Percent 98 `
            -Message ("本机仓库回收完成：{0} GB → {1} GB{2}" -f [Math]::Round($before / 1GB, 2), [Math]::Round($after / 1GB, 2), $suffix) `
            -Detail '本机只留当前这一版的对象；上一版在下载站有硬链接归档。'
        return $after
    }
    catch {
        # 回收只是收尾工作，失败不能把已经成功的发布判成失败。
        Write-AxTaskEvent -Data ([ordered]@{
            type    = 'warning'
            code    = 'local-repo-maintenance-failed'
            message = "本机仓库回收失败（不影响本次发布）：$($_.Exception.Message)"
        })
        return (Get-DirectorySize -Path (Join-Path $RepoRoot '.git'))
    }
}

function Get-HookSummary {
    param([string[]]$Lines)

    $report = [ordered]@{ Repository = ''; WorkTree = ''; DiskFree = '' }
    foreach ($line in $Lines) {
        $text = $line.Trim()
        if ($text -match '仓库对象：(\S+)\s+工作树：(\S+)') {
            $report.Repository = $Matches[1]
            $report.WorkTree = $Matches[2]
        }
        elseif ($text -match '磁盘剩余：(\S+)') {
            $report.DiskFree = $Matches[1]
        }
    }
    return $report
}

# ---------------------------------------------------------------------------
# GitHub 备用渠道（把这一版的文件清单原样发一份到 GitHub Release）
# ---------------------------------------------------------------------------
# GitHub 附件名不能带 `/`：相对路径里的斜杠统一换成 `__`（可逆，也躲开文件名里
# 本来就有的字符）。玩家端"Github 源"按同一规则把 path 翻成附件名即可。
function Get-GitHubAssetName {
    param([Parameter(Mandatory)][string]$RelativePath)

    return $RelativePath.Replace('\', '/').Replace('/', '__')
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
    return [pscustomobject]@{ ExitCode = $code; Lines = @($output | ForEach-Object { [string]$_ }) }
}

# 一次发布里的 GitHub 收尾。三条要点：
#   1) 只传"本地有、GitHub 上没有（或 sha256 不同）"的附件 —— 同一版重跑 = 0 字节，
#      和 dl 那条的幂等语义一致（0.5.14 这种"dl 已是最新、GitHub 还没发"的局面靠这条兜住）。
#   2) gh 没有"另行指定附件名"的开关，附件名只能取文件名，所以先把文件**硬链接**成
#      `<相对路径换 __ 后>.名字` 再上传；同盘硬链接不额外占空间，跨盘才退回复制。
#   3) 清单 manifest.json **最后**上传：消费方拿到清单就会按 path 去找附件，晚一步才不会扑空。
function Invoke-GitHubGameRelease {
    param(
        [Parameter(Mandatory)][object[]]$Files,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Title,
        [string]$Notes = '',
        [string]$ManifestPath = '',
        [Parameter(Mandatory)][string]$AssetRoot
    )

    if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
        throw '没有找到 GitHub CLI（gh），无法上传 GitHub 备用渠道。'
    }

    $release = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $Repository, '--json', 'assets')
    if ($release.ExitCode -ne 0) {
        Write-AxTaskStage -Stage 'github' -Message "GitHub 上还没有 $Tag，正在创建 Release" -Detail $Repository
        $notes = if ([string]::IsNullOrWhiteSpace($Notes)) { $Title } else { $Notes.Trim() }
        $created = Invoke-Gh -Arguments @('release', 'create', $Tag, '--repo', $Repository, '--title', $Title, '--notes', $notes)
        if ($created.ExitCode -ne 0) {
            throw ("GitHub Release 创建失败：" + (($created.Lines | Out-String).Trim()))
        }
        $release = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $Repository, '--json', 'assets')
    }
    if ($release.ExitCode -ne 0) {
        throw "GitHub 读取 Release 失败：$Tag（$Repository）"
    }

    $existing = @{}
    try {
        $parsed = (($release.Lines -join [Environment]::NewLine) | ConvertFrom-Json)
        foreach ($asset in @($parsed.assets)) {
            if ($null -ne $asset) { $existing[[string]$asset.name] = $asset }
        }
    }
    catch {
        throw ("GitHub 附件列表解析失败：" + $_.Exception.Message)
    }

    $pending = [System.Collections.Generic.List[object]]::new()
    $seenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $skipped = 0
    foreach ($file in $Files) {
        $relative = [string]$file.path
        $assetName = Get-GitHubAssetName -RelativePath $relative
        if (-not $seenNames.Add($assetName)) {
            throw "两个文件会落到同一个 GitHub 附件名，先改命名规则：$relative"
        }
        if ($existing.ContainsKey($assetName)) {
            $asset = $existing[$assetName]
            $digest = if ($null -eq $asset.digest) { '' } else { ([string]$asset.digest).ToLowerInvariant() }
            $expected = ([string]$file.sha256).ToLowerInvariant()
            $same = ($digest -eq "sha256:$expected")
            if (-not $same -and [string]::IsNullOrWhiteSpace($digest)) {
                $same = ([int64]$asset.size -eq [int64]$file.sizeBytes)
            }
            if ($same) { $skipped++; continue }
        }
        $pending.Add([pscustomobject]@{
            Name   = $assetName
            Source = [string]$file.FullName
            Size   = [int64]$file.sizeBytes
        })
    }

    if (Test-Path -LiteralPath $AssetRoot) { Remove-Item -LiteralPath $AssetRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $AssetRoot -Force | Out-Null

    $totalBytes = [int64]0
    foreach ($item in $pending) { $totalBytes += $item.Size }
    $doneBytes = [int64]0
    $index = 0
    $uploaded = [System.Collections.Generic.List[string]]::new()
    $removedAssets = [System.Collections.Generic.List[string]]::new()
    try {
        Write-AxTaskStage `
            -Stage 'github' `
            -Message ("GitHub 渠道：要上传 {0} 个 / {1}，已是最新跳过 {2} 个" -f `
                $pending.Count, (Format-Size -Bytes $totalBytes), $skipped) `
            -Detail "$Repository · $Tag"

        foreach ($item in $pending) {
            Test-TaskCancelled
            $index++

            $staged = Join-Path $AssetRoot $item.Name
            if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue }
            try {
                New-Item -ItemType HardLink -Path $staged -Target $item.Source -ErrorAction Stop | Out-Null
            }
            catch {
                Copy-Item -LiteralPath $item.Source -Destination $staged -Force
            }

            $percent = [int](94 + 4.0 * $doneBytes / [Math]::Max($totalBytes, 1))
            Write-AxTaskProgress `
                -Stage 'github' `
                -Percent $percent `
                -Message ("正在上传到 GitHub：{0}/{1} {2}（{3}）" -f $index, $pending.Count, $item.Name, (Format-Size -Bytes $item.Size)) `
                -Detail $Repository

            $result = Invoke-Gh -Arguments @('release', 'upload', $Tag, $staged, '--repo', $Repository, '--clobber')
            if ($result.ExitCode -ne 0) {
                throw ("GitHub 附件上传失败：{0}`n{1}" -f $item.Name, (($result.Lines | Out-String).Trim()))
            }
            $uploaded.Add($item.Name)
            $doneBytes += $item.Size
        }

        if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
            if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
                throw "GitHub 渠道要发的清单不存在：$ManifestPath"
            }
            # 清单也要走同一套"内容没变就跳过"的判断，否则同一版重跑总会白传一次清单
            # （内容没变时内核会沿用上次的 generatedAt，所以清单字节不变 ⇒ sha256 相同）。
            $manifestItem = Get-Item -LiteralPath $ManifestPath -Force
            $manifestHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $manifestSame = $false
            if ($existing.ContainsKey('manifest.json')) {
                $knownManifest = $existing['manifest.json']
                $digest = if ($null -eq $knownManifest.digest) { '' } else { ([string]$knownManifest.digest).ToLowerInvariant() }
                $manifestSame = ($digest -eq "sha256:$manifestHash")
                if (-not $manifestSame -and [string]::IsNullOrWhiteSpace($digest)) {
                    $manifestSame = ([int64]$knownManifest.size -eq [int64]$manifestItem.Length)
                }
            }
            if ($manifestSame) {
                $skipped++
            }
            else {
                $stagedManifest = Join-Path $AssetRoot 'manifest.json'
                if (Test-Path -LiteralPath $stagedManifest) { Remove-Item -LiteralPath $stagedManifest -Force -ErrorAction SilentlyContinue }
                Copy-Item -LiteralPath $ManifestPath -Destination $stagedManifest -Force
                $result = Invoke-Gh -Arguments @('release', 'upload', $Tag, $stagedManifest, '--repo', $Repository, '--clobber')
                if ($result.ExitCode -ne 0) {
                    throw ("GitHub 清单上传失败：`n" + (($result.Lines | Out-String).Trim()))
                }
                $uploaded.Add('manifest.json')
            }
        }

        # 对账（用户 2026-09-20 提出）：GitHub 是"玩家直接下载整包"的渠道，**每个版本必须是全量的**，
        # 不能只放这一版变的那几个文件。所以这里不信"我们传过什么"，传完再读一次 Release 现场，
        # 附件集合必须和这一版的清单**完全一致**：少一个 → 失败（玩家会拿到残缺包）；多出来 → 只警告。
        $expectedNames = [System.Collections.Generic.List[string]]::new()
        foreach ($file in $Files) { $expectedNames.Add((Get-GitHubAssetName -RelativePath ([string]$file.path))) }
        if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) { $expectedNames.Add('manifest.json') }

        $final = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $Repository, '--json', 'assets')
        if ($final.ExitCode -ne 0) { throw "GitHub 渠道对账失败：读不到 $Tag 的附件列表。" }
        $finalNames = @()
        try {
            $finalNames = @(
                ((($final.Lines -join [Environment]::NewLine) | ConvertFrom-Json).assets) |
                    ForEach-Object { [string]$_.name }
            )
        }
        catch {
            throw ("GitHub 渠道对账失败：附件列表解析不出来 " + $_.Exception.Message)
        }

        $missing = @($expectedNames | Where-Object { $finalNames -notcontains $_ } | Sort-Object)
        $extra = @($finalNames | Where-Object { $expectedNames -notcontains $_ } | Sort-Object)
        if ($missing.Count -gt 0) {
            throw ("GitHub Release 少了附件（玩家会下到残缺包）：`n  " + ($missing -join "`n  "))
        }
        if ($extra.Count -gt 0) {
            # 多余附件 = 同一版本重传、清单变小留下的（旧的 lua/pak 之类）。用户 2026-09-20 要求直接清掉，
            # 免得玩家在那个 Release 里看到这一版根本不该有的文件。
            # 保险：清单本身为空时**拒绝清理**（真出现这种异常，宁可不动 GitHub）。
            if ($expectedNames.Count -eq 0) { throw '这一版的清单为空，拒绝清理 GitHub 上的附件。' }
            Write-AxTaskStage `
                -Stage 'github' `
                -Message ("GitHub 上有 {0} 个附件不在这一版的清单里，正在清理" -f $extra.Count) `
                -Detail ($extra -join '、')
            foreach ($extraName in $extra) {
                Test-TaskCancelled
                $removeResult = Invoke-Gh -Arguments @('release', 'delete-asset', $Tag, $extraName, '--repo', $Repository, '--yes')
                if ($removeResult.ExitCode -ne 0) {
                    throw ("删除多余附件失败：{0}`n{1}" -f $extraName, (($removeResult.Lines | Out-String).Trim()))
                }
                $removedAssets.Add($extraName)
                Write-AxTaskProgress -Stage 'github' -Percent 98 -Message "已清理多余附件：$extraName" -Detail $Tag
            }

            # 删完再读一次现场：确认剩下的就是清单里的那些（删除本身也可能静默失败）。
            $after = Invoke-Gh -Arguments @('release', 'view', $Tag, '--repo', $Repository, '--json', 'assets')
            if ($after.ExitCode -ne 0) { throw "GitHub 清理后对账失败：读不到 $Tag 的附件列表。" }
            $afterNames = @(
                ((($after.Lines -join [Environment]::NewLine) | ConvertFrom-Json).assets) |
                    ForEach-Object { [string]$_.name }
            )
            $stillExtra = @($afterNames | Where-Object { $expectedNames -notcontains $_ } | Sort-Object)
            if ($stillExtra.Count -gt 0) {
                throw ("清理后仍有多余附件：`n  " + ($stillExtra -join "`n  "))
            }
            $finalNames = $afterNames
        }
        $reconcileMessage = if ($removedAssets.Count -gt 0) {
            "GitHub Release 已对账：{0} 个附件与清单完全一致（该版本是全量），并清理了 {1} 个多余附件" -f $finalNames.Count, $removedAssets.Count
        }
        else {
            "GitHub Release 已对账：{0} 个附件与清单完全一致（该版本是全量）" -f $finalNames.Count
        }
        Write-AxTaskProgress `
            -Stage 'github' `
            -Percent 98 `
            -Message $reconcileMessage `
            -Detail "https://github.com/$Repository/releases/tag/$Tag"
    }
    finally {
        if (Test-Path -LiteralPath $AssetRoot) {
            Remove-Item -LiteralPath $AssetRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    return [pscustomobject]@{
        Repository = $Repository
        Tag        = $Tag
        Uploaded   = $uploaded.Count
        Skipped    = $skipped
        Bytes      = $totalBytes
        ReleaseAssets = $finalNames.Count
        Removed    = $removedAssets.Count
    }
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
try {
    if (-not (Test-Path -LiteralPath $GameDirectory -PathType Container)) {
        throw "游戏包目录不存在：$GameDirectory"
    }
    if ($ReleaseVersion -notmatch '^[0-9]+(\.[0-9]+){1,3}$') {
        throw "版本号格式不对：$ReleaseVersion（应形如 0.5.13）"
    }

    Write-AxTaskStage -Stage 'scan' -Message "正在扫描 $PlatformLabel 打包产物" -Detail $GameDirectory
    $releaseFiles = @(Get-ReleaseFileList -Root $GameDirectory -Platform $Platform)

    # 安卓：OBB 不发本体，发它里面的条目（+ 一个旁车文件告诉启动器怎么组装回 OBB）
    $obbFile = $null
    $obbEntries = @()
    $obbUnpackRoot = Join-Path $StagingRoot ("_obb-" + $ProductSegment)
    if ($Platform -eq 'Android') {
        $obbFile = Get-ChildItem -LiteralPath $GameDirectory -Recurse -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^main\.\d+\..+\.obb$' } |
            Sort-Object Name | Select-Object -First 1
        if ($null -ne $obbFile) {
            $obbEntries = Expand-AndroidObb -ObbPath $obbFile.FullName -UnpackRoot $obbUnpackRoot
            $releaseFiles = @($releaseFiles) + @($obbEntries) |
                Sort-Object -Property Relative -CaseSensitive
            Write-AxTaskStage `
                -Stage 'scan' `
                -Message "OBB 已解成 $($obbEntries.Count) 个条目（$($obbFile.Name) 本身不上传）" `
                -Detail $obbUnpackRoot
        }
    }
    if ($releaseFiles.Count -eq 0) {
        throw "打包目录里没有一个可以发布的文件：$GameDirectory"
    }
    $sourceBytes = 0L
    foreach ($file in $releaseFiles) { $sourceBytes += $file.Length }
    Write-AxTaskStage `
        -Stage 'scan' `
        -Message "找到 $($releaseFiles.Count) 个待发布文件（$([Math]::Round($sourceBytes / 1MB, 1)) MB）" `
        -Detail $(if ($Platform -eq 'Android') { '安卓只发主 APK 与 OBB，符号/AFS/bat 已排除。' } else { '已排除 pdb、Saved 日志、_download。' })

    $previousManifest = Get-ManifestSnapshot -Path $ManifestPath
    $hashedFiles = Get-HashedFileList -Files $releaseFiles
    $totalBytes = 0L
    foreach ($file in $hashedFiles) { $totalBytes += $file.sizeBytes }
    $changeSummary = Get-ChangeSummary -Previous $previousManifest -Files $hashedFiles

    Write-AxTaskStage `
        -Stage 'manifest' `
        -Message "正在生成 v1 清单（$($hashedFiles.Count) 条）" `
        -Detail $changeSummary.Note
    Write-AxTaskProgress `
        -Stage 'manifest' `
        -Percent 34 `
        -Message ("差异：新增 {0} · 变化 {1} · 删除 {2}；按清单口径需要传输 {3}" -f `
            $changeSummary.Added, $changeSummary.Updated, $changeSummary.Removed, (Format-Size -Bytes $changeSummary.TransferBytes)) `
        -Detail $BaseUrl

    $sync = Sync-VersionDirectory -Files $hashedFiles
    Write-AxTaskProgress `
        -Stage 'stage' `
        -Percent 56 `
        -Message "产物已同步：硬链接 $($sync.Linked) · 复制 $($sync.Copied) · 跳过 $($sync.Skipped) · 删除 $($sync.Removed)" `
        -Detail $VersionDirectory

    # 版本标记文件由脚本生成（打包目录里没有），同步之后再写，避免被"清理多余文件"那一步删掉。
    [ordered]@{ version = $ReleaseVersion; platform = $PlatformLabel; channel = $ChannelLabel } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $VersionDirectory $VersionMarkerName) -Encoding UTF8 -NoNewline

    # 安卓多一个旁车：告诉启动器"要组装回哪个 OBB、由哪些条目组成"（OBB 本体不上传）。
    $obbSidecarFile = $null
    if ($null -ne $obbFile) {
        $obbSidecarFile = Join-Path $VersionDirectory $ObbSidecarName
        $sidecar = [ordered]@{
            obbFileName = $obbFile.Name
            entries     = @($obbEntries | ForEach-Object { $_.Relative })
            version     = $ReleaseVersion
            productKey  = $ManifestProductKey
        }
        [IO.File]::WriteAllText(
            $obbSidecarFile,
            ($sidecar | ConvertTo-Json -Depth 4),
            [Text.UTF8Encoding]::new($false))
    }

    $stale = Remove-StaleVersions
    if ($stale -gt 0) {
        Write-AxTaskProgress -Stage 'stage' -Percent 58 -Message "已清理 $stale 个更早的版本目录/清单（保留策略：树里只留当前版本）"
    }

    # 清单放在最后写：它描述的是"落位后的目录"，必须包含 version.json 本身。
    $markerFile = Get-Item -LiteralPath (Join-Path $VersionDirectory $VersionMarkerName) -Force
    $manifestFiles = [System.Collections.Generic.List[object]]::new()
    foreach ($file in $hashedFiles) {
        $manifestFiles.Add([ordered]@{
            path      = $file.path
            sizeBytes = $file.sizeBytes
            sha256    = $file.sha256
        })
    }
    $manifestFiles.Add([ordered]@{
        path      = $VersionMarkerName
        sizeBytes = [int64]$markerFile.Length
        sha256    = (Get-FileHash -LiteralPath $markerFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    if ($null -ne $obbSidecarFile) {
        $manifestFiles.Add([ordered]@{
            path      = $ObbSidecarName
            sizeBytes = [int64](Get-Item -LiteralPath $obbSidecarFile -Force).Length
            sha256    = (Get-FileHash -LiteralPath $obbSidecarFile -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    $generatedAt = [DateTimeOffset]::Now.ToString('o')
    $manifest = [ordered]@{
        schemaVersion = 1
        productKey    = $ManifestProductKey
        runtime       = $PlatformLabel
        version       = $ReleaseVersion
        channel       = $ChannelLabel
        baseUrl       = $BaseUrl
        generatedAt   = $generatedAt
        files         = @($manifestFiles)
        patches       = @()
    }
    if (Test-ManifestEquivalent -Previous $previousManifest -Manifest $manifest) {
        $manifest['generatedAt'] = [string]$previousManifest.GeneratedAt
    }

    # 安卓解出来的 OBB 条目只是"中转"：已经硬链接进版本目录了，临时目录可以收掉。
    if ($Platform -eq 'Android' -and (Test-Path -LiteralPath $obbUnpackRoot)) {
        Remove-Item -LiteralPath $obbUnpackRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $ManifestDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $ManifestDirectory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $ManifestPath,
        ($manifest | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Write-AxTaskStage -Stage 'manifest' -Message '清单已写入本机仓库' -Detail $ManifestPath

    $summary = [ordered]@{
        Mode           = $Mode
        Platform       = $PlatformLabel
        Version        = $ReleaseVersion
        ProductSegment = $ProductSegment
        Manifest       = $ManifestPath
        VersionDirectory = $VersionDirectory
        BaseUrl        = $BaseUrl
        FileCount      = $manifestFiles.Count
        TotalBytes     = ($totalBytes + $markerFile.Length + $(if ($null -ne $obbSidecarFile) { (Get-Item -LiteralPath $obbSidecarFile -Force).Length } else { 0 }))
        ObbFileName    = $(if ($null -ne $obbFile) { $obbFile.Name } else { '' })
        ObbEntryCount  = $obbEntries.Count
        Added          = $changeSummary.Added
        Updated        = $changeSummary.Updated
        Removed        = $changeSummary.Removed
        Note           = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) { '' } else { $ReleaseNotes.Trim() }
    }

    if ($Mode -eq 'Check') {
        Write-AxTaskProgress `
            -Stage 'completed' `
            -Percent 100 `
            -Message ("检查完成：{0} 个文件 / {1}，未提交、未上传。" -f $summary.FileCount, (Format-Size -Bytes $summary.TotalBytes)) `
            -Detail $ManifestPath
        $summary['Committed'] = $false
        $summary['Pushed'] = $false
        $summary | ConvertTo-Json -Depth 6
        exit 0
    }

    $pushed = $false
    if ($GitHubOnly) {
        Write-AxTaskProgress `
            -Stage 'local' `
            -Percent 94 `
            -Message '已生成本地清单：按选择跳过下载站，直接准备 GitHub Release' `
            -Detail $VersionDirectory
    }
    elseif ($TransferMode -eq 'files') {
        # 逐文件同步：不碰 git，只传变化的文件 + 清单（内容没变就是 0 字节）
        $pushed = Invoke-FileSyncPublish -ManifestFiles $manifestFiles
    }
    else {
    Initialize-StagingRepository -Reinitialize:($Mode -eq 'RebuildRepo')
    $commitMessage = '{0} {1} {2}：{3} 个文件 / {4}' -f `
        $ProductSegment, $ReleaseVersion, $PlatformLabel, $manifestFiles.Count, (Format-Size -Bytes $summary.TotalBytes)

    Invoke-GitChecked -Arguments @('-C', $RepoRoot, 'add', '-A')
    $hasHead = Get-HeadCommitMessage
    # 幂等：内容与仓库里已提交的那一版完全一致时，连提交都不做（同一版重跑 = 什么都不传）。
    $unchanged = $hasHead -and (Test-IndexMatchesHead)
    $pushed = $false

    if ($unchanged) {
        Write-AxTaskStage `
            -Stage 'commit' `
            -Message '内容与仓库里已提交的版本完全一致：跳过提交与推送（幂等）' `
            -Detail $commitMessage
        Write-AxTaskProgress -Stage 'push' -Percent 94 -Message '没有需要上传的内容（0 字节）'
    }
    elseif ($DryRun) {
        Write-AxTaskStage -Stage 'commit' -Message '演练模式：跳过提交与推送' -Detail $commitMessage
        Write-AxTaskStage -Stage 'push' -Message '演练模式：跳过 push 与公网校验' -Detail "git -C $RepoRoot push --force --progress origin HEAD:main"
        $summary['Committed'] = $false
        $summary['Pushed'] = $false
        $summary | ConvertTo-Json -Depth 6
        exit 0
    }
    else {
        Write-AxTaskStage -Stage 'commit' -Message '正在提交本机暂存仓库（在上一版之上增量）' -Detail $commitMessage
        # 先对齐"服务端当前那版"，否则 git 没有可排除的共同对象，会整包重传。
        Sync-RemoteTipForPush
        Invoke-CommitVersion -Message $commitMessage | Out-Null
        $headShort = (Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--short', 'HEAD')).Lines[0].Trim()
        Write-AxTaskProgress -Stage 'commit' -Percent 61 -Message "已提交：$headShort"

        # 推之前先算一遍"理论上该传多少对象"：排除服务端那一版之后剩下的就是变化的文件。
        # 正常情况下应该是几十个（清单 + 版本标记 + 路径上的 tree）；如果等于整棵树，
        # 说明协商没生效、这次会整包重传 —— 日志里能直接看出来。
        $pending = Invoke-Git -AllowFailure -Arguments @(
            '-C', $RepoRoot, 'rev-list', '--objects', 'HEAD', '--not', 'refs/remotes/origin/main')
        if ($pending.ExitCode -eq 0) {
            Write-AxTaskProgress `
                -Stage 'push' `
                -Percent 62 `
                -Message "本次需要上传 $($pending.Lines.Count) 个对象（只含变化的文件）" `
                -Detail "git rev-list --objects HEAD --not refs/remotes/origin/main"
        }

        Write-AxTaskProgress `
            -Stage 'push' `
            -Percent 62 `
            -Message '正在推送到下载站（增量）' `
            -Detail "$UploadTarget → $VersionSegmentPath"
        $pushExitCode = Invoke-GitWithProgress `
            -Arguments @('-C', $RepoRoot, 'push', '--force', '--progress', 'origin', 'HEAD:main') `
            -Stage 'push' `
            -PercentFrom 62 `
            -PercentTo 94 `
            -Message '正在上传到下载站：'
        if ($pushExitCode -ne 0) {
            throw "推送到下载站失败，退出码 $pushExitCode（具体报错见上面的输出）。"
        }
        $pushed = $true

        # 推送已经成功，旧 .git 的备份就没用了（它只用来在重建失败时兜底）。
        if (-not [string]::IsNullOrWhiteSpace($script:RebuildBackup)) {
            Remove-Item -LiteralPath $script:RebuildBackup -Recurse -Force -ErrorAction SilentlyContinue
            Write-AxTaskStage -Stage 'completed' -Message '旧的 .git 备份已删除' -Detail $script:RebuildBackup
            $script:RebuildBackup = ''
        }
    }

    }

    if ($pushed -and $TransferMode -eq 'git') {
        $hook = Get-HookSummary -Lines $script:GitOutput
        $hookDetail = @()
        if (-not [string]::IsNullOrWhiteSpace($hook.Repository)) { $hookDetail += "仓库对象 $($hook.Repository)" }
        if (-not [string]::IsNullOrWhiteSpace($hook.WorkTree)) { $hookDetail += "工作树 $($hook.WorkTree)" }
        if (-not [string]::IsNullOrWhiteSpace($hook.DiskFree)) { $hookDetail += "磁盘剩余 $($hook.DiskFree)" }
        Write-AxTaskProgress `
            -Stage 'push' `
            -Percent 94 `
            -Message '下载站已落位' `
            -Detail ($hookDetail -join ' · ')
    }

    # 本机仓库：发布成功后立刻回收掉上一版的对象，让它永远只有"当前这一版"；
    # 没发布但仓库明显偏大（比如手工折腾过）也顺手收一次。
    # 放在公网校验之前做：校验失败也不至于把垃圾留到下一次发布。
    $treeSize = Get-DirectorySize -Path (Join-Path $RepoRoot 'games')
    $gitDirectorySize = Get-DirectorySize -Path (Join-Path $RepoRoot '.git')
    $repoSizeThreshold = [int64]($treeSize * 1.4) + 100MB
    if ($TransferMode -eq 'files') {
        # files 模式不用本机 git 仓库（清单/暂存树在用，.git 不再参与），这里不回收也不告警
        $gitDirectorySize = [int64]0
    }
    elseif ($pushed) {
        $gitDirectorySize = Invoke-LocalRepoMaintenance -Reason '发布成功'
    }
    elseif ($TransferMode -eq 'git' -and $treeSize -gt 0 -and $gitDirectorySize -gt $repoSizeThreshold) {
        $gitDirectorySize = Invoke-LocalRepoMaintenance -Reason '本机仓库里堆着历史对象'
    }

    # GitHub 备用渠道：dl 落位之后，把这一版按文件清单**原样**再发一份到 GitHub。
    # 和 dl 那条完全独立（dl 是主线即启动器的官方源，GitHub 是备用源），所以这里**不看 $pushed**：
    # 同一版重跑时 dl 是幂等 0 字节，但 GitHub 可能还一次都没发过（0.5.14 就是这种局面）。
    # 顺序：先传文件、最后传清单 —— 消费方拿到清单就会按 path 去找附件。
    $gitHubSummary = $null
    if (-not $SkipGitHub -and -not [string]::IsNullOrWhiteSpace($GitHubRepo) -and -not $DryRun) {
        $gitHubFiles = [System.Collections.Generic.List[object]]::new()
        # 注意：这里必须从**版本目录**取文件，不能沿用扫描时的路径。
        # 安卓的清单条目来自 OBB 解包临时目录（_obb-<段>），那个目录在写清单之前就被清掉了
        # （踩过：gh 报"找不到路径 …\_obb-crossingvoid-android\…\interlude_V1_Low.mp4"）。
        # 版本目录里每个文件的字节和清单里的 sha256 一致（同步那一步就是按 sha256 硬链接/复制的）。
        foreach ($file in $hashedFiles) {
            $stagedPath = Join-Path $VersionDirectory ([string]$file.path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf)) {
                throw "版本目录里缺少清单里的文件，无法发 GitHub：$stagedPath"
            }
            $gitHubFiles.Add([pscustomobject]@{
                path      = $file.path
                sizeBytes = $file.sizeBytes
                sha256    = $file.sha256
                FullName  = $stagedPath
            })
        }
        $gitHubFiles.Add([pscustomobject]@{
            path      = $VersionMarkerName
            sizeBytes = [int64]$markerFile.Length
            sha256    = (Get-FileHash -LiteralPath $markerFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            FullName  = $markerFile.FullName
        })
        if ($null -ne $obbSidecarFile) {
            $gitHubFiles.Add([pscustomobject]@{
                path      = $ObbSidecarName
                sizeBytes = [int64](Get-Item -LiteralPath $obbSidecarFile -Force).Length
                sha256    = (Get-FileHash -LiteralPath $obbSidecarFile -Algorithm SHA256).Hash.ToLowerInvariant()
                FullName  = $obbSidecarFile
            })
        }

        $gitHubReleaseUrl = "https://github.com/$GitHubRepo/releases/tag/$GitHubTag"
        Write-AxTaskStage `
            -Stage 'github' `
            -Message ("正在把这一版同步到 GitHub 备用渠道（{0} 个文件 / {1}）" -f `
                $gitHubFiles.Count, (Format-Size -Bytes $summary.TotalBytes)) `
            -Detail "$GitHubRepo · $GitHubTag"
        $gitHubSummary = Invoke-GitHubGameRelease `
            -Files @($gitHubFiles) `
            -Repository $GitHubRepo `
            -Tag $GitHubTag `
            -Title ("V{0} | {1}" -f $ReleaseVersion, $GitHubTagPrefix) `
            -Notes $ReleaseNotes `
            -ManifestPath $ManifestPath `
            -AssetRoot $GitHubAssetRoot
        Write-AxTaskProgress `
            -Stage 'github' `
            -Percent 98 `
            -Message ("GitHub 渠道完成：上传 {0} 个 / {1}，已是最新跳过 {2} 个" -f `
                $gitHubSummary.Uploaded, (Format-Size -Bytes $gitHubSummary.Bytes), $gitHubSummary.Skipped) `
            -Detail $gitHubReleaseUrl
    }
    elseif (-not $SkipGitHub -and -not [string]::IsNullOrWhiteSpace($GitHubRepo)) {
        Write-AxTaskStage -Stage 'github' -Message '演练模式：跳过 GitHub 渠道上传' -Detail "$GitHubRepo · $GitHubTag"
    }

    $verify = $null
    if (-not $SkipVerify -and -not $GitHubOnly) {
        Write-AxTaskStage -Stage 'verify' -Message '正在做公网校验（清单 + 抽样文件）' -Detail $BaseUrl
        $verify = Invoke-PublicVerify -Files $hashedFiles -ExpectedFileCount $manifestFiles.Count
        Write-AxTaskProgress `
            -Stage 'verify' `
            -Percent 99 `
            -Message "公网校验通过：清单 $($verify.Files) 条，抽样 $($verify.SampledFiles) 个文件" `
            -Detail $verify.ManifestUrl
    }

    if ($TransferMode -eq 'git' -and $treeSize -gt 0 -and $gitDirectorySize -gt $repoSizeThreshold) {
        Write-AxTaskEvent -Data ([ordered]@{
            type    = 'warning'
            code    = 'staging-repo-bloated'
            message = ("本机暂存仓库的对象仍有 {0}（当前版本内容 {1}），回收没降下来；必要时用维护区里的「重整仓库」重建一次。" -f `
                (Format-Size -Bytes $gitDirectorySize), (Format-Size -Bytes $treeSize))
        })
    }

    $summary['Committed'] = $pushed
    $summary['Pushed'] = $pushed
    $summary['GitHubOnly'] = $GitHubOnly
    $summary['SkipGitHub'] = $SkipGitHub
    $summary['TransferMode'] = $TransferMode
    $summary['StagingRepoBytes'] = $gitDirectorySize
    $summary['TreeBytes'] = $treeSize
    if ($null -ne $verify) { $summary['Verify'] = $verify }
    if ($null -ne $gitHubSummary) {
        $summary['GitHubRepository'] = $gitHubSummary.Repository
        $summary['GitHubTag'] = $gitHubSummary.Tag
        $summary['GitHubUploaded'] = $gitHubSummary.Uploaded
        $summary['GitHubSkipped'] = $gitHubSummary.Skipped
        $summary['GitHubRemoved'] = $gitHubSummary.Removed
    }

    $completedMessage = if ($GitHubOnly) {
        '本地清单已生成：{0} {1}（{2} 个文件 / {3}），本次只发 GitHub' -f $PlatformLabel, $ReleaseVersion, $summary.FileCount, (Format-Size -Bytes $summary.TotalBytes)
    }
    elseif ($pushed) {
        '发布完成：{0} {1}（{2} 个文件 / {3}）' -f $PlatformLabel, $ReleaseVersion, $summary.FileCount, (Format-Size -Bytes $summary.TotalBytes)
    }
    else {
        '无需发布：{0} {1} 与下载站上已提交的内容一致（未上传任何字节）' -f $PlatformLabel, $ReleaseVersion
    }
    if ($null -ne $gitHubSummary) {
        $completedMessage += '；GitHub 备用渠道：上传 {0} 个（{1}），已是最新跳过 {2} 个' -f `
            $gitHubSummary.Uploaded, (Format-Size -Bytes $gitHubSummary.Bytes), $gitHubSummary.Skipped
        if ($gitHubSummary.Removed -gt 0) {
            $completedMessage += '，清理多余附件 {0} 个' -f $gitHubSummary.Removed
        }
        $completedDetail = "https://github.com/$($gitHubSummary.Repository)/releases/tag/$($gitHubSummary.Tag)"
    }
    else {
        $completedDetail = if ($GitHubOnly) { $ManifestPath } else { $BaseUrl }
    }
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message $completedMessage -Detail $completedDetail
    $summary | ConvertTo-Json -Depth 6
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-AxTaskEvent -Data ([ordered]@{ type = 'error'; message = $message })
    [Console]::Out.WriteLine("发布内核失败：$message")
    [Console]::Out.WriteLine($_.ScriptStackTrace)
    exit 1
}
