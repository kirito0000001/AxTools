[CmdletBinding()]
param([Parameter(Mandatory)][string]$Action,[Parameter(Mandatory)][string]$ProjectRoot)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "项目目录不存在：$ProjectRoot" }
Write-Host "幻杀启动器 Android 适配器已就绪：$Action"
