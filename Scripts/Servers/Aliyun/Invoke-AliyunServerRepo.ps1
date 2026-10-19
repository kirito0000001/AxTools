[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LocalRepoPath,
    [Parameter(Mandatory)]
    [ValidateSet('status', 'import', 'commit', 'push', 'preview', 'cleanup')]
    [string]$Action,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SourceDirectory = '',
    [string]$CommitMessage = '',
    [int]$PreviewLimit = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 进度事件走 AxTools 的任务协议（stdout 上的 ::axtools 行）。结果 JSON 仍然只写文件，
# 两者互不干扰；不接这个模块脚本照样能跑，只是进度条不会动。
Import-Module (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Common\AxTaskProtocol.psm1') -Force

# 本机仓库侧动作：镜像导入、提交、推送、预览。不涉及 SSH。
# 注意：这里必须有 '.git'。镜像同步是"多删、少补、变更新"，漏掉它就会把整个版本库删掉
# （2026-09-19 在克隆上实测撞过：import 完 `git add` 报 fatal: not a git repository）。
# Saved 是服务器运行时存档/配置，不属于构建产物，镜像和清空目录时必须保留。
$preservedNames = @('.git', '.gitignore', '.gitattributes', 'Saved')

function Invoke-AxGit {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $output = & git @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw ("git {0} 失败（{1}）：{2}" -f ($Arguments -join ' '), $code, (($output | Out-String).Trim()))
    }
    return [pscustomobject]@{
        ExitCode = $code
        Output   = @($output | ForEach-Object { [string]$_ })
    }
}

# 带进度上报的 git 调用：git push 会把 "Writing objects: 45% (...)" 这类进度写在
# stderr 上，要一边跑一边转成进度事件，不能像 Invoke-AxGit 那样等它结束再拿结果
# （等结束 = 上传 1.5 GB 的整段时间里进度条纹丝不动）。
function Invoke-AxGitWithProgress {
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
    try {
        & git @Arguments 2>&1 | ForEach-Object {
            $text = ([string]$_).Trim()
            if ([string]::IsNullOrWhiteSpace($text)) { return }
            if ($text -match '^(Enumerating|Counting|Compressing|Writing|Updating|Receiving|Resolving|remote:).*?(\d{1,3})%') {
                $gitPercent = [int]$Matches[2]
                $percent = [int]($PercentFrom + ($PercentTo - $PercentFrom) * $gitPercent / 100.0)
                Write-AxTaskProgress `
                    -Stage $Stage `
                    -Percent $percent `
                    -Message ("$Message " + $gitPercent + "%") `
                    -Detail $text
            }
            else {
                # 普通输出照旧给宿主：失败的真正原因（error: ...）就是靠这些行进日志的。
                [Console]::Out.WriteLine($text)
            }
        }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return $code
}

function Assert-AxRepository {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "本机仓库目录不存在：$Path"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Path '.git') -PathType Container)) {
        throw "本机目录不是 git 仓库：$Path"
    }
}

function Get-AxRelativeFileMap {
    param([string]$Root, [string[]]$SkipNames)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $map }
    foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        $topLevel = ($relative -split '[\\/]')[0]
        if ($SkipNames -contains $topLevel) { continue }
        # 别写成 $relative.Split('\', '/')：PowerShell 会把第二个参数绑到
        # String.Split(char separator, int count) 那个重载上，'/' 转 int 直接抛
        # "Cannot convert argument "count", with value: "/""。用 -split 运算符没有这个歧义。
        $firstSegment = ($relative -split '[\\/]')[0]
        if ($SkipNames -contains $firstSegment) { continue }
        if ($SkipNames -contains $file.Name) { continue }
        $map[$relative.Replace('/', '\')] = $file
    }
    return $map
}

# 镜像同步：把打包产物精确映射到仓库工作树，多删、少补、变更新。
# .git 目录与 .gitignore / .gitattributes 永远不参与，也不会被删除。
# 进度百分比：2% 扫描，5~70% 复制，78% 删完过期文件，之后清理空目录（不定百分比）。
function Sync-AxBuildMirror {
    param([string]$Source, [string]$Target)

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "打包输出目录不存在：$Source"
    }

    Write-AxTaskProgress -Stage 'scan' -Percent 2 -Message '正在扫描打包产物…' -Detail $Source
    $sourceFiles = Get-AxRelativeFileMap -Root $Source -SkipNames $preservedNames
    $targetFiles = Get-AxRelativeFileMap -Root $Target -SkipNames $preservedNames
    Write-AxTaskProgress `
        -Stage 'scan' `
        -Percent 5 `
        -Message "正在同步产物文件（共 $($sourceFiles.Count) 个）…" `
        -Detail "仓库现有 $($targetFiles.Count) 个文件，逐个比对大小与时间戳决定是否复制。"

    $added = 0
    $updated = 0
    $removed = 0
    $removedBytes = 0L
    $total = $sourceFiles.Count
    $index = 0
    $lastPercent = 5

    foreach ($relative in $sourceFiles.Keys) {
        $index++
        $from = $sourceFiles[$relative]
        $to = Join-Path $Target $relative
        $needsCopy = $true
        if (Test-Path -LiteralPath $to -PathType Leaf) {
            $existing = Get-Item -LiteralPath $to -Force
            if ($existing.Length -eq $from.Length -and $existing.LastWriteTimeUtc -eq $from.LastWriteTimeUtc) {
                $needsCopy = $false
            }
        }
        if ($needsCopy) {
            $directory = Split-Path -Parent $to
            if (-not [string]::IsNullOrWhiteSpace($directory)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }
            Copy-Item -LiteralPath $from.FullName -Destination $to -Force
            if ($targetFiles.ContainsKey($relative)) { $updated++ } else { $added++ }
        }

        if ($total -gt 0) {
            $percent = [int](5 + (65.0 * $index / $total))
            if ($percent - $lastPercent -ge 2) {
                $lastPercent = $percent
                Write-AxTaskProgress `
                    -Stage 'copy' `
                    -Percent $percent `
                    -Message "正在同步产物文件 $index/$total" `
                    -Detail "新增 $added · 更新 $updated · 位于 $relative"
            }
        }
    }

    foreach ($relative in $targetFiles.Keys) {
        if ($sourceFiles.ContainsKey($relative)) { continue }
        $path = $targetFiles[$relative].FullName
        try {
            $removedBytes += $targetFiles[$relative].Length
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            $removed++
        }
        catch {
            # 被占用的文件留着不动，靠 git status 如实反映
        }
    }
    if ($removed -gt 0) {
        Write-AxTaskProgress `
            -Stage 'cleanup' `
            -Percent 78 `
            -Message "已删除产物里不再存在的 $removed 个文件" `
            -Detail "释放 $([Math]::Round($removedBytes / 1MB, 1)) MB。"
    }

    # 清理空目录，保持目录树与产物一致。
    Write-AxTaskStage -Stage 'cleanup' -Message '正在清理空目录…'
    foreach ($directory in (Get-ChildItem -LiteralPath $Target -Recurse -Directory -Force -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending)) {
        # .git / Saved 自己、以及它们下面的任何一级目录都不动（空目录也留着，让 git/服务端自己管）
        $relative = $directory.FullName.Substring($Target.Length).TrimStart('\', '/')
        $topLevel = ($relative -split '[\\/]')[0]
        if ($topLevel -in @('.git', 'Saved')) { continue }
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    return [ordered]@{
        addedFiles   = $added
        updatedFiles = $updated
        removedFiles = $removed
        removedBytes = $removedBytes
    }
}

function Get-AxDirectoryBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0L }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0L }
    return [int64]$sum
}

function Get-AxLocalStatus {
    param([string]$Path)
    $branch = (Invoke-AxGit -Arguments @('-C', $Path, 'rev-parse', '--abbrev-ref', 'HEAD')).Output[0]
    $head = Invoke-AxGit -Arguments @('-C', $Path, 'log', '-1', '--format=%H%n%h%n%aI%n%s')
    $dirty = Invoke-AxGit -Arguments @('-C', $Path, '-c', 'core.quotepath=false', 'status', '--porcelain')
    $upstream = Invoke-AxGit -Arguments @('-C', $Path, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}') -AllowFailure

    $ahead = 0
    $behind = 0
    if ($upstream.ExitCode -eq 0 -and $upstream.Output.Count -gt 0) {
        $counts = (Invoke-AxGit -Arguments @('-C', $Path, 'rev-list', '--left-right', '--count', '@{u}...HEAD')).Output[0]
        $parts = $counts -split '\s+'
        if ($parts.Count -ge 2) {
            $behind = [int]$parts[0]
            $ahead = [int]$parts[1]
        }
    }

    return [ordered]@{
        branch         = $branch
        head           = [ordered]@{
            sha     = $head.Output[0]
            short   = $head.Output[1]
            date    = $head.Output[2]
            subject = $head.Output[3]
        }
        upstream       = $upstream.Output[0]
        ahead          = $ahead
        behind         = $behind
        dirtyCount     = $dirty.Output.Count
        dirtyEntries   = @($dirty.Output | Select-Object -First 40)
    }
}

function Get-AxChangeReport {
    param([string]$Path)
    # core.quotepath=false：不然中文路径会被转义成 "\345\233\236\347\211\271" 这种八进制
    # 序列，确认导入的弹窗就没法看了（人看不懂等于没确认）。
    $status = Invoke-AxGit -Arguments @('-C', $Path, '-c', 'core.quotepath=false', 'status', '--porcelain')
    $totalEntries = $status.Output.Count
    $index = 0
    $lastPercent = 88
    Write-AxTaskProgress `
        -Stage 'report' `
        -Percent 88 `
        -Message "正在统计变更清单（$totalEntries 项）…" `
        -Detail '逐项读取体积，供你确认这次导入是否符合预期。'

    $entries = @()
    foreach ($line in $status.Output) {
        $index++
        if ($line.Length -lt 4) { continue }
        $code = $line.Substring(0, 2).Trim()
        $name = $line.Substring(3).Trim()
        # 含空格等特殊字符时 git 仍会用双引号包起来，展示时去掉；重命名行 ("a -> b") 保持原样。
        if ($name.Length -ge 2 -and $name.StartsWith('"') -and $name.EndsWith('"')) {
            $name = $name.Substring(1, $name.Length - 2)
        }
        $kind = switch -Regex ($code) {
            '^\?\?' { 'added'; break }
            '^A' { 'added'; break }
            '^D' { 'removed'; break }
            '^R' { 'renamed'; break }
            default { 'modified' }
        }
        $size = 0L
        $full = Join-Path $Path $name
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $size = (Get-Item -LiteralPath $full -Force).Length
        }
        $entries += [ordered]@{ kind = $kind; code = $code; path = $name; bytes = $size }

        if ($totalEntries -gt 0) {
            $percent = [int](88 + (11.0 * $index / $totalEntries))
            if ($percent - $lastPercent -ge 2) {
                $lastPercent = $percent
                Write-AxTaskProgress `
                    -Stage 'report' `
                    -Percent $percent `
                    -Message "正在统计变更清单 $index/$totalEntries" `
                    -Detail $name
            }
        }
    }
    return $entries
}

function Invoke-AxLocalAction {
    $result = [ordered]@{ success = $false; action = $Action; message = ''; data = $null }
    try {
        Write-AxTaskStage -Stage 'prepare' -Message "正在检查本机仓库（$Action）…" -Detail $LocalRepoPath
        Assert-AxRepository -Path $LocalRepoPath

        switch ($Action) {
            'cleanup' {
                # 只清理 .git：不 check out、不删工作树，Saved 永远不在这里被碰。
                $gitPath = Join-Path $LocalRepoPath '.git'
                $before = Get-AxDirectoryBytes -Path $gitPath
                Write-AxTaskProgress `
                    -Stage 'cleanup' `
                    -Percent 10 `
                    -Message "正在安全清理本地仓库（清理前 .git $([Math]::Round($before / 1MB, 1)) MB）…" `
                    -Detail '只执行 reflog expire + git gc，不动工作树和 Saved。'

                $reflog = Invoke-AxGit -Arguments @(
                    '-C', $LocalRepoPath, 'reflog', 'expire', '--expire=now', '--all') -AllowFailure
                if ($reflog.ExitCode -ne 0) {
                    Write-AxTaskStage -Stage 'cleanup' -Message 'reflog 回收失败，继续尝试 git gc' -Detail (($reflog.Output | Out-String).Trim())
                }

                Write-AxTaskProgress -Stage 'cleanup' -Percent 45 -Message '正在执行 git gc --prune=now…'
                $gc = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'gc', '--prune=now') -AllowFailure
                if ($gc.ExitCode -ne 0) {
                    throw ("git gc 失败（退出码 {0}）：{1}" -f $gc.ExitCode, (($gc.Output | Out-String).Trim()))
                }

                $after = Get-AxDirectoryBytes -Path $gitPath
                $workTree = Get-AxDirectoryBytes -Path $LocalRepoPath
                $status = Get-AxLocalStatus -Path $LocalRepoPath
                $result.data = [ordered]@{
                    gitBeforeBytes = $before
                    gitAfterBytes  = $after
                    workTreeBytes  = $workTree
                    status         = $status
                }
                Write-AxTaskProgress `
                    -Stage 'cleanup' `
                    -Percent 95 `
                    -Message ("清理完成：.git {0} MB → {1} MB" -f [Math]::Round($before / 1MB, 1), [Math]::Round($after / 1MB, 1)) `
                    -Detail '工作树和 Saved 未改动。'
            }
            'status' { $result.data = Get-AxLocalStatus -Path $LocalRepoPath }
            'import' {
                if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
                    throw '导入新构建需要 -SourceDirectory。'
                }
                Write-AxTaskStage -Stage 'import' -Message '开始镜像打包产物…' -Detail $SourceDirectory
                $sync = Sync-AxBuildMirror -Source $SourceDirectory -Target $LocalRepoPath
                Write-AxTaskProgress -Stage 'stage' -Percent 84 -Message '正在暂存变更（git add -A）…' -Detail $LocalRepoPath
                Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'add', '-A') | Out-Null
                $result.data = [ordered]@{
                    sync    = $sync
                    changes = Get-AxChangeReport -Path $LocalRepoPath
                }
            }
            'commit' {
                if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
                    throw '提交需要 -CommitMessage。'
                }
                Write-AxTaskStage -Stage 'commit' -Message '正在提交本机仓库…'
                $messagePath = Join-Path ([IO.Path]::GetTempPath()) "axgitmsg_$([guid]::NewGuid().ToString('N')).txt"
                try {
                    [IO.File]::WriteAllText($messagePath, $CommitMessage, [Text.UTF8Encoding]::new($false))
                    $commit = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'commit', '-F', $messagePath) -AllowFailure
                }
                finally {
                    Remove-Item -LiteralPath $messagePath -Force -ErrorAction SilentlyContinue
                }
                Write-AxTaskProgress -Stage 'commit' -Percent 85 -Message '正在读取提交后的仓库状态…'
                $result.data = [ordered]@{
                    committed = ($commit.ExitCode -eq 0)
                    detail    = (($commit.Output | Out-String).Trim())
                    status    = Get-AxLocalStatus -Path $LocalRepoPath
                }
            }
            'push' {
                Write-AxTaskProgress `
                    -Stage 'push' `
                    -Percent 2 `
                    -Message '正在推送到服务器裸仓库…' `
                    -Detail '这一步只更新服务器上的仓库，不会重启服务端；上传量取决于这次改了多少文件。'
                $previousSshCommand = $env:GIT_SSH_COMMAND
                try {
                    # 网络抖动时 git push 容易以 128 直接退出；加 keepalive，并给一次自动重试。
                    $env:GIT_SSH_COMMAND = 'ssh -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=10 -o ServerAliveCountMax=3'
                    $pushExitCode = 1
                    for ($attempt = 1; $attempt -le 2; $attempt++) {
                        $pushExitCode = Invoke-AxGitWithProgress `
                            -Arguments @('-C', $LocalRepoPath, 'push', '--progress', 'origin', 'HEAD:main') `
                            -Stage 'push' `
                            -PercentFrom 2 `
                            -PercentTo 92 `
                            -Message '正在上传到服务器：'
                        if ($pushExitCode -eq 0) { break }
                        if ($attempt -lt 2) {
                            Write-AxTaskProgress `
                                -Stage 'push' `
                                -Percent 5 `
                                -Message '推送失败，3 秒后自动重试一次…' `
                                -Detail "git push 退出码 $pushExitCode"
                            Start-Sleep -Seconds 3
                        }
                    }
                }
                finally {
                    if ($null -eq $previousSshCommand) {
                        Remove-Item Env:GIT_SSH_COMMAND -ErrorAction SilentlyContinue
                    }
                    else {
                        $env:GIT_SSH_COMMAND = $previousSshCommand
                    }
                }
                if ($pushExitCode -ne 0) {
                    throw "推送失败，退出码 $pushExitCode（具体报错见上面的输出）。"
                }
                Write-AxTaskProgress -Stage 'push' -Percent 95 -Message '上传完成，正在核对仓库状态…'
                $result.data = [ordered]@{
                    pushed = $true
                    status = Get-AxLocalStatus -Path $LocalRepoPath
                }
            }
            'preview' {
                Write-AxTaskStage -Stage 'preview' -Message '正在读取本机仓库的提交与差异…'
                $log = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'log', "-n", "$PreviewLimit", '--format=%H|%h|%aI|%s')
                $commits = @()
                foreach ($line in $log.Output) {
                    $parts = $line -split '\|', 4
                    if ($parts.Count -lt 4) { continue }
                    $commits += [ordered]@{ sha = $parts[0]; short = $parts[1]; date = $parts[2]; subject = $parts[3] }
                }
                $diff = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'diff', '--stat', '--no-color', 'origin/main..HEAD') -AllowFailure
                $result.data = [ordered]@{
                    commits = $commits
                    diffStat = @($diff.Output)
                }
            }
        }
        $result.success = $true
    }
    catch {
        $result.message = $_.Exception.Message
    }

    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        ($result | ConvertTo-Json -Depth 8 -Compress),
        [Text.UTF8Encoding]::new($false))

    exit 0
}

Invoke-AxLocalAction
