[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$DevelopmentExecutable,
    [Parameter(Mandatory)][int]$MainProcessId,
    [Parameter(Mandatory)][string]$ReadySignalPath,
    [Parameter(Mandatory)][string]$LogPath,
    [Parameter(Mandatory)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Test-PathWithinDirectory {
    param([string]$Path, [string]$Directory)

    $normalizedPath = [IO.Path]::GetFullPath($Path)
    $normalizedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    return $normalizedPath.StartsWith(
        $normalizedDirectory + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Write-RebuildResult {
    param([bool]$Success, [string]$Message)

    $result = [ordered]@{
        success = $Success
        message = $Message
        logPath = [IO.Path]::GetFullPath($LogPath)
        finishedAt = [DateTimeOffset]::Now.ToString('o')
    }
    $path = [IO.Path]::GetFullPath($ResultPath)
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    $result | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$executable = [IO.Path]::GetFullPath($DevelopmentExecutable)
$outputDirectory = Split-Path -Parent $executable
$debugRoot = Join-Path $root 'bin\x64\Debug'
$projectPath = Join-Path $root 'AxTools.csproj'
$logFullPath = [IO.Path]::GetFullPath($LogPath)
$readyFullPath = [IO.Path]::GetFullPath($ReadySignalPath)
$backupDirectory = "$outputDirectory.self-rebuild-backup-$([Guid]::NewGuid().ToString('N'))"
$backupCreated = $false
$newProcess = $null

if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "AxTools 源码目录不存在：$root" }
if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "AxTools 项目文件不存在：$projectPath" }
if (!(Test-PathWithinDirectory -Path $executable -Directory $debugRoot)) {
    throw "开发版 EXE 不在 AxTools Debug 输出目录内：$executable"
}
if ([IO.Path]::GetFileName($executable) -ne 'AxTools.exe') { throw "开发版入口名称无效：$executable" }
$dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

$logParent = Split-Path -Parent $logFullPath
$readyParent = Split-Path -Parent $readyFullPath
New-Item -ItemType Directory -Path $logParent -Force | Out-Null
New-Item -ItemType Directory -Path $readyParent -Force | Out-Null
Start-Transcript -LiteralPath $logFullPath -Append | Out-Null

try {
    "READY`nprocessId=$PID" | Set-Content -LiteralPath $readyFullPath -Encoding utf8NoBOM
    try {
        Wait-Process -Id $MainProcessId -Timeout 60 -ErrorAction Stop
    }
    catch {
        if (Get-Process -Id $MainProcessId -ErrorAction SilentlyContinue) {
            throw 'AxTools 主程序未能在 60 秒内退出，已取消自重建。'
        }
    }

    if (Test-Path -LiteralPath $outputDirectory -PathType Container) {
        Move-Item -LiteralPath $outputDirectory -Destination $backupDirectory
        $backupCreated = $true
    }

    Write-Host '正在编译 AxTools Debug x64...'
    & $dotnet build $projectPath --configuration Debug --runtime win-x64 '-p:Platform=x64'
    if ($LASTEXITCODE -ne 0) {
        throw "AxTools Debug x64 编译失败，退出码 $LASTEXITCODE。"
    }

    foreach ($requiredPath in @(
        $executable,
        (Join-Path $outputDirectory 'AxTools.pri'),
        (Join-Path $outputDirectory 'App.xbf'),
        (Join-Path $outputDirectory 'MainWindow.xbf'))) {
        if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "AxTools 自重建产物不完整，缺少：$requiredPath"
        }
    }

    Write-RebuildResult -Success $true -Message 'AxTools 已完成外部自重建并启动新开发版。'
    $newProcess = Start-Process -FilePath $executable -WorkingDirectory $outputDirectory -PassThru
    Start-Sleep -Seconds 2
    if ($newProcess.HasExited) {
        throw "新 AxTools 启动后立即退出，退出码 $($newProcess.ExitCode)。"
    }

    if ($backupCreated -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        $backupCreated = $false
    }
}
catch {
    $failureMessage = $_.Exception.Message
    if ($null -ne $newProcess -and !$newProcess.HasExited) {
        try { $newProcess.Kill($true) } catch {}
    }
    if ($backupCreated) {
        if (Test-Path -LiteralPath $outputDirectory) {
            Remove-Item -LiteralPath $outputDirectory -Recurse -Force
        }
        Move-Item -LiteralPath $backupDirectory -Destination $outputDirectory
        $backupCreated = $false
    }
    Write-RebuildResult -Success $false -Message $failureMessage
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        Start-Process -FilePath $executable -WorkingDirectory $outputDirectory
    }
    throw
}
finally {
    Remove-Item -LiteralPath $readyFullPath -Force -ErrorAction SilentlyContinue
    try { Stop-Transcript | Out-Null } catch {}
}
