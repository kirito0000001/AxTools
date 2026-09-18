[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LocalRepoPath,
    [Parameter(Mandatory)]
    [ValidateSet('status', 'import', 'commit', 'push', 'preview')]
    [string]$Action,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SourceDirectory = '',
    [string]$CommitMessage = '',
    [int]$PreviewLimit = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# 本机仓库侧动作：镜像导入、提交、推送、预览。不涉及 SSH。
$preservedNames = @('.gitignore', '.gitattributes')

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
        $firstSegment = $relative.Split('\', '/')[0]
        if ($SkipNames -contains $firstSegment) { continue }
        if ($SkipNames -contains $file.Name) { continue }
        $map[$relative.Replace('/', '\')] = $file
    }
    return $map
}

# 镜像同步：把打包产物精确映射到仓库工作树，多删、少补、变更新。
# .git 目录与 .gitignore / .gitattributes 永远不参与，也不会被删除。
function Sync-AxBuildMirror {
    param([string]$Source, [string]$Target)

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "打包输出目录不存在：$Source"
    }

    $sourceFiles = Get-AxRelativeFileMap -Root $Source -SkipNames $preservedNames
    $targetFiles = Get-AxRelativeFileMap -Root $Target -SkipNames $preservedNames

    $added = 0
    $updated = 0
    $removed = 0
    $removedBytes = 0L

    foreach ($relative in $sourceFiles.Keys) {
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

    # 清理空目录，保持目录树与产物一致。
    foreach ($directory in (Get-ChildItem -LiteralPath $Target -Recurse -Directory -Force -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending)) {
        if ($directory.Name -eq '.git') { continue }
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

function Get-AxLocalStatus {
    param([string]$Path)
    $branch = (Invoke-AxGit -Arguments @('-C', $Path, 'rev-parse', '--abbrev-ref', 'HEAD')).Output[0]
    $head = Invoke-AxGit -Arguments @('-C', $Path, 'log', '-1', '--format=%H%n%h%n%aI%n%s')
    $dirty = Invoke-AxGit -Arguments @('-C', $Path, 'status', '--porcelain')
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
    $status = Invoke-AxGit -Arguments @('-C', $Path, 'status', '--porcelain')
    $entries = @()
    foreach ($line in $status.Output) {
        if ($line.Length -lt 4) { continue }
        $code = $line.Substring(0, 2).Trim()
        $name = $line.Substring(3).Trim()
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
    }
    return $entries
}

function Invoke-AxLocalAction {
    $result = [ordered]@{ success = $false; action = $Action; message = ''; data = $null }
    try {
        Assert-AxRepository -Path $LocalRepoPath

        switch ($Action) {
            'status' { $result.data = Get-AxLocalStatus -Path $LocalRepoPath }
            'import' {
                if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
                    throw '导入新构建需要 -SourceDirectory。'
                }
                $sync = Sync-AxBuildMirror -Source $SourceDirectory -Target $LocalRepoPath
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
                $messagePath = Join-Path ([IO.Path]::GetTempPath()) "axgitmsg_$([guid]::NewGuid().ToString('N')).txt"
                try {
                    [IO.File]::WriteAllText($messagePath, $CommitMessage, [Text.UTF8Encoding]::new($false))
                    $commit = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'commit', '-F', $messagePath) -AllowFailure
                }
                finally {
                    Remove-Item -LiteralPath $messagePath -Force -ErrorAction SilentlyContinue
                }
                $result.data = [ordered]@{
                    committed = ($commit.ExitCode -eq 0)
                    detail    = (($commit.Output | Out-String).Trim())
                    status    = Get-AxLocalStatus -Path $LocalRepoPath
                }
            }
            'push' {
                $push = Invoke-AxGit -Arguments @('-C', $LocalRepoPath, 'push', '--quiet', 'origin', 'HEAD:main') -AllowFailure
                if ($push.ExitCode -ne 0) {
                    throw ("推送失败：{0}" -f (($push.Output | Out-String).Trim()))
                }
                $result.data = [ordered]@{
                    pushed = $true
                    status = Get-AxLocalStatus -Path $LocalRepoPath
                }
            }
            'preview' {
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
