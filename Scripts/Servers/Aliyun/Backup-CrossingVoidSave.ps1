[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Backup-ProjectSaveGames.ps1') `
    -SourceDirectory 'C:\Users\Administrator\Desktop\WindowsServer\CrossingVoid\Saved\SaveGames' `
    -BackupRoot 'C:\UEWatchdog\backups\CrossingVoid\SaveGames' `
    -RetentionDays 90 `
    -ProjectName 'CrossingVoid' `
    -LogPath 'C:\UEWatchdog\logs\save-backup.log'
exit $LASTEXITCODE
