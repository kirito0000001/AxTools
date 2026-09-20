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

    # 对外下载基址。清单里的 baseUrl = <这个>/games/<产品段>/<版本>/
    [string]$PublicBaseUrl = 'https://dl.crossingvoid.top',

    # dl 上的产品目录名。留空则按平台推导（Windows → crossingvoid，Android → crossingvoid-android）
    [string]$ProductSegment = '',

    # 版本标记文件名。这个文件由脚本生成（打包目录里没有），跟着产物一起上线，
    # 给外部工具读版本用。零境沿用旧名字，别的游戏传自己工程名即可。
    [string]$VersionMarkerName = 'CrossingVoid.version.json',

    [switch]$SkipVerify,
    [switch]$DryRun
)

# 游戏包发布内核（P3 重写，2026-09-20）
#
# 干什么：把"文件夹形态的打包产物"变成"下载站上的一个版本"，全程不压缩、不切片：
#   1) 检查产物（按平台过滤：PC 排除 pdb/Saved 日志，安卓只发主 APK + OBB）
#   2) 生成 v1 文件级清单（schemaVersion=1 + baseUrl + 每个文件的 sha256）
#   3) 把产物同步进本机暂存仓库（树里只保留这一个版本，更早的版本物理删掉）
#   4) 提交（amend，保持单提交）→ git push --force 到下载站裸仓库（增量、带百分比）
#   5) 服务端 post-receive hook 负责：把上一版硬链接归档到 games/<产品段>-archive/<版本>/
#      → checkout -f main 落到 /srv/downloads → 省内存 gc 回收旧对象 → 回报体积
#   6) 公网校验：拉一次清单、HEAD 抽样文件，核对版本与字节数
#
# 为什么 amend + force：这样服务端仓库对象只涨"这一个版本"的量，工作树也永远只有最新版，
# 上一版由 hook 硬链接兜底（同盘同 inode，归档那一刻不额外占空间）。
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

    # 只发"玩家真正需要"的两个文件：主 APK + OBB。
    # 排除掉打包目录里的符号、AFS 调试工具、安装/卸载 bat（旧发布流程也是这么过滤的）。
    if ($Name -match '^main\.\d+\..+\.obb$') { return $true }
    if ($Name.StartsWith('AFS_', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($Name -match '-Android-Shipping-.+\.apk$') { return $true }
    return $false
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

function Invoke-CommitVersion {
    param([string]$Message)

    # 注意：这里**不能**用 `git commit --amend`。amend 只是换掉末端提交、保留父提交，
    # 于是历史会一直累积：老版本的 blob 在服务端仍然"可达"，gc 收不掉，
    # 实测 2.5 GB 的内容把仓库对象撑到 3.2 GiB（2026-09-20）。
    # 所以直接写一个**没有父提交**的新提交：仓库永远只有一个提交、只含当前版本。
    # 增量不受影响——客户端仍然只发"服务端没有的对象"，同内容文件按哈希复用。
    $branch = (Invoke-Git -Arguments @('-C', $RepoRoot, 'symbolic-ref', '--short', 'HEAD')).Lines[0].Trim()
    $tree = (Invoke-Git -Arguments @('-C', $RepoRoot, 'write-tree')).Lines[0].Trim()

    # 提交信息走文件：中文和空格在命令行上容易被改写。
    $messagePath = Join-Path ([IO.Path]::GetTempPath()) ("axtools-game-commit-" + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        [IO.File]::WriteAllText($messagePath, $Message, [Text.UTF8Encoding]::new($false))
        $commit = (Invoke-Git -Arguments @('-C', $RepoRoot, 'commit-tree', $tree, '-F', $messagePath)).Lines[-1].Trim()
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
    $releaseFiles = Get-ReleaseFileList -Root $GameDirectory -Platform $Platform
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
        TotalBytes     = ($totalBytes + $markerFile.Length)
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
        Write-AxTaskStage -Stage 'commit' -Message '正在提交本机暂存仓库（保持单提交）' -Detail $commitMessage
        Invoke-CommitVersion -Message $commitMessage | Out-Null
        $headShort = (Invoke-Git -Arguments @('-C', $RepoRoot, 'rev-parse', '--short', 'HEAD')).Lines[0].Trim()
        Write-AxTaskProgress -Stage 'commit' -Percent 61 -Message "已提交：$headShort"

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

    if ($pushed) {
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
    if ($pushed) {
        $gitDirectorySize = Invoke-LocalRepoMaintenance -Reason '发布成功'
    }
    elseif ($treeSize -gt 0 -and $gitDirectorySize -gt $repoSizeThreshold) {
        $gitDirectorySize = Invoke-LocalRepoMaintenance -Reason '本机仓库里堆着历史对象'
    }

    $verify = $null
    if (-not $SkipVerify) {
        Write-AxTaskStage -Stage 'verify' -Message '正在做公网校验（清单 + 抽样文件）' -Detail $BaseUrl
        $verify = Invoke-PublicVerify -Files $hashedFiles -ExpectedFileCount $manifestFiles.Count
        Write-AxTaskProgress `
            -Stage 'verify' `
            -Percent 99 `
            -Message "公网校验通过：清单 $($verify.Files) 条，抽样 $($verify.SampledFiles) 个文件" `
            -Detail $verify.ManifestUrl
    }

    if ($treeSize -gt 0 -and $gitDirectorySize -gt $repoSizeThreshold) {
        Write-AxTaskEvent -Data ([ordered]@{
            type    = 'warning'
            code    = 'staging-repo-bloated'
            message = ("本机暂存仓库的对象仍有 {0}（当前版本内容 {1}），回收没降下来；必要时用维护区里的「重整仓库」重建一次。" -f `
                (Format-Size -Bytes $gitDirectorySize), (Format-Size -Bytes $treeSize))
        })
    }

    $summary['Committed'] = $pushed
    $summary['Pushed'] = $pushed
    $summary['StagingRepoBytes'] = $gitDirectorySize
    $summary['TreeBytes'] = $treeSize
    if ($null -ne $verify) { $summary['Verify'] = $verify }

    $completedMessage = if ($pushed) {
        '发布完成：{0} {1}（{2} 个文件 / {3}）' -f $PlatformLabel, $ReleaseVersion, $summary.FileCount, (Format-Size -Bytes $summary.TotalBytes)
    }
    else {
        '无需发布：{0} {1} 与下载站上已提交的内容一致（未上传任何字节）' -f $PlatformLabel, $ReleaseVersion
    }
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message $completedMessage -Detail $BaseUrl
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
