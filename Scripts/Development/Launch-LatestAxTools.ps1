[CmdletBinding()]
param()

# 双击快捷方式时用：在 bin 下找最新的 AxTools.exe 并启动。
# 这样不管这次是 bin\Debug 还是 bin\x64\Debug，打开的都是最近一次构建。
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $scriptDirectory)
$binDirectory = Join-Path $projectRoot 'bin'
$logDirectory = Join-Path $env:LOCALAPPDATA 'AxTools'
$logPath = Join-Path $logDirectory 'launcher.log'

function Write-LauncherLog {
    param([string]$Message)
    try {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $Message" -Encoding utf8
    }
    catch {
        # 日志写不了也不能挡住启动。
    }
}

try {
    if (!(Test-Path -LiteralPath $binDirectory -PathType Container)) {
        throw "找不到 bin 目录：$binDirectory"
    }

    $executable = Get-ChildItem -LiteralPath $binDirectory -Recurse -File -Filter 'AxTools.exe' -ErrorAction Stop |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $executable) {
        throw "在 $binDirectory 下找不到 AxTools.exe，先编译一次开发版。"
    }

    Write-LauncherLog "启动 $($executable.FullName)（$($executable.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))）"
    Start-Process -FilePath $executable.FullName -WorkingDirectory $executable.DirectoryName
}
catch {
    Write-LauncherLog "启动失败：$($_.Exception.Message)"
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            'AxTools 开发版启动器',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    }
    catch {
        # 连消息框都弹不出来时只能靠 launcher.log。
    }
}
