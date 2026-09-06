[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$TargetDirectory,
    [Parameter(Mandatory)][int]$MainProcessId,
    [string]$EntryExe = 'AxTools.exe',
    [Parameter(Mandatory)][string]$TargetVersion,
    [Parameter(Mandatory)][string]$ReadySignalPath,
    [Parameter(Mandatory)][string]$LogPath,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
function Assert-SafeRelative([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path) -or $Path.Split([char[]]'\/') -contains '..') { throw "不安全的相对路径：$Path" }
}
$target = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
$package = [IO.Path]::GetFullPath($PackagePath)
Assert-SafeRelative $EntryExe
if (!(Test-Path -LiteralPath $target -PathType Container)) { throw "目标目录不存在：$target" }
if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "更新包不存在：$package" }
$logParent = Split-Path -Parent ([IO.Path]::GetFullPath($LogPath))
New-Item -ItemType Directory -Path $logParent -Force | Out-Null
Start-Transcript -LiteralPath $LogPath -Append | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ("AxTools-Update-" + [guid]::NewGuid().ToString('N'))
$backup = "$target.update-backup"
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $staging -Force
    $manifestPath = Join-Path $staging 'update-package.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { throw '更新包缺少 update-package.json。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.toolboxStableKey -ne 'AxTools' -or $manifest.version -ne $TargetVersion -or $manifest.runtime -ne 'win-x64') { throw '更新包身份或版本不匹配。' }
    if (!(Test-Path -LiteralPath (Join-Path $staging $EntryExe))) { throw "更新包缺少入口：$EntryExe" }
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReadySignalPath))) -Force | Out-Null
    "READY`nversion=$TargetVersion" | Set-Content -LiteralPath $ReadySignalPath -Encoding utf8NoBOM
    try { Wait-Process -Id $MainProcessId -Timeout 30 -ErrorAction Stop } catch { if (Get-Process -Id $MainProcessId -ErrorAction SilentlyContinue) { throw '主程序未能在 30 秒内退出。' } }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    Move-Item -LiteralPath $target -Destination $backup
    try {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $staging '*') -Destination $target -Recurse -Force
        if (!(Test-Path -LiteralPath (Join-Path $target $EntryExe))) { throw '覆盖后入口缺失。' }
    }
    catch {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Move-Item -LiteralPath $backup -Destination $target
        throw
    }
    Remove-Item -LiteralPath $backup -Recurse -Force
    if (!$NoRestart) { Start-Process -FilePath (Join-Path $target $EntryExe) -WorkingDirectory $target }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    try { Stop-Transcript | Out-Null } catch {}
}
