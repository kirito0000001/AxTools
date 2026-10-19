[CmdletBinding()]
param(
    # 每天只备份 SaveGames 里的 .sav；不碰 Saved 里的 Config/Logs，更不删除源目录。
    [string]$SourceDirectory = 'C:\Users\Administrator\Desktop\幻杀_Server\WindowsServer\FantasyProject\Saved\SaveGames',
    [string]$BackupRoot = 'C:\UEWatchdog\backups\FantasyProject\SaveGames',
    [int]$RetentionDays = 90,
    [string]$LogPath = 'C:\UEWatchdog\logs\save-backup.log',
    [string]$ProjectName = 'Project'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Write-BackupLog {
    param([string]$Level, [string]$Message)
    $line = '[{0}] {1} {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try {
        $directory = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        [IO.File]::AppendAllText($LogPath, $line + "`r`n", [Text.UTF8Encoding]::new($false))
    }
    catch {
        # 日志写不了不能掩盖备份结果。
    }
}

$startedAt = Get-Date
$temporary = ''
try {
    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "SaveGames 目录不存在：$SourceDirectory"
    }

    $files = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Filter '*.sav' -Force -ErrorAction Stop)
    if ($files.Count -eq 0) {
        throw "SaveGames 里没有 .sav 文件：$SourceDirectory"
    }

    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $snapshotName = $startedAt.ToString('yyyyMMdd-HHmmss')
    $target = Join-Path $BackupRoot $snapshotName
    if (Test-Path -LiteralPath $target) {
        $snapshotName = $snapshotName + '-' + $startedAt.ToString('fff')
        $target = Join-Path $BackupRoot $snapshotName
    }

    $temporary = "$target.tmp-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null

    $manifestFiles = @()
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($SourceDirectory.Length).TrimStart('\', '/')
        $destination = Join-Path $temporary $relative
        $destinationDirectory = Split-Path -Parent $destination
        if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }

        $copied = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force -ErrorAction Stop
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($sourceHash -eq $destinationHash -and
                (Get-Item -LiteralPath $destination -Force).Length -eq $file.Length) {
                $copied = $true
                break
            }

            Start-Sleep -Milliseconds 300
        }

        if (-not $copied) {
            throw "复制的 .sav 校验失败：$relative"
        }

        $manifestFiles += [ordered]@{
            path   = $relative -replace '\\', '/'
            size   = [int64]$file.Length
            sha256 = $sourceHash
        }
    }

    $manifest = [ordered]@{
        project    = $ProjectName
        capturedAt = $startedAt.ToString('o')
        source     = $SourceDirectory
        fileCount  = $manifestFiles.Count
        files      = @($manifestFiles)
    }
    [IO.File]::WriteAllText(
        (Join-Path $temporary 'backup.json'),
        ($manifest | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))

    [IO.Directory]::Move($temporary, $target)
    $temporary = ''

    # 只删除备份目录里超过保留期的快照，绝不碰源 SaveGames。
    $cutoff = (Get-Date).AddDays(-[Math]::Max(1, $RetentionDays))
    $removed = 0
    foreach ($directory in @(Get-ChildItem -LiteralPath $BackupRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($directory.Name -notmatch '^\d{8}-\d{6}(-\d{3})?$') { continue }
        if ($directory.LastWriteTime -lt $cutoff) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }

    Write-BackupLog 'INFO' ("[{0}] 备份成功：{1} 个 .sav，快照 {2}；清理旧快照 {3} 个；保留 {4} 天。" -f `
        $ProjectName, $manifestFiles.Count, $target, $removed, $RetentionDays)
    exit 0
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($temporary) -and (Test-Path -LiteralPath $temporary)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-BackupLog 'ERROR' $_.Exception.Message
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
