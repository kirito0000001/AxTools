[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Backup-ProjectSaveGames.ps1') `
    -SourceDirectory 'C:\Users\Administrator\Desktop\幻杀_Server\WindowsServer\FantasyProject\Saved\SaveGames' `
    -BackupRoot 'C:\UEWatchdog\backups\FantasyProject\SaveGames' `
    -RetentionDays 90 `
    -ProjectName 'FantasyProject' `
    -LogPath 'C:\UEWatchdog\logs\save-backup.log'
exit $LASTEXITCODE
