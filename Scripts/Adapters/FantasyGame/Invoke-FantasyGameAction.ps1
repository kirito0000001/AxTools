[CmdletBinding()]
param([Parameter(Mandatory)][string]$Action,[Parameter(Mandatory)][string]$ProjectRoot)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "项目目录不存在：$ProjectRoot" }
Write-Host "幻杀游戏占位分类检查通过：$ProjectRoot"
