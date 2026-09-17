[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Action,
    [string]$InstanceName = '',
    [int]$TailLines = 200
)

# Runs on the Aliyun server. Kept ASCII-only so Windows PowerShell 5.1 parses it
# regardless of file encoding. Chinese display labels are mapped on the AxTools side.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Get-AxWatchdogConfig {
    $configPath = 'C:\UEWatchdog\watchdog.config.psd1'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "watchdog config not found: $configPath"
    }
    return Import-PowerShellDataFile -LiteralPath $configPath
}

function Get-AxServerEntry {
    param($Config, [string]$InstanceName)

    $entry = $Config.Servers | Where-Object { [string]$_.Name -ieq $InstanceName } | Select-Object -First 1
    if ($null -eq $entry) {
        throw "unknown instance: $InstanceName"
    }
    return $entry
}

function Get-AxLogDirectories {
    param($Server)

    $directories = [System.Collections.ArrayList]::new()
    if ($Server.ContainsKey('LogDirectory') -and -not [string]::IsNullOrWhiteSpace([string]$Server.LogDirectory)) {
        [void]$directories.Add([string]$Server.LogDirectory)
    }

    $anchor = $null
    if ($Server.ContainsKey('WorkingDirectory') -and -not [string]::IsNullOrWhiteSpace([string]$Server.WorkingDirectory)) {
        $anchor = [string]$Server.WorkingDirectory
    }
    elseif ($Server.ContainsKey('ExePath')) {
        $anchor = Split-Path -Parent ([string]$Server.ExePath)
    }

    if (-not [string]::IsNullOrWhiteSpace($anchor)) {
        $cursor = $anchor
        for ($level = 0; $level -lt 4 -and -not [string]::IsNullOrWhiteSpace($cursor); $level++) {
            $candidate = Join-Path $cursor 'Saved\Logs'
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                [void]$directories.Add($candidate)
            }
            $cursor = Split-Path -Parent $cursor
        }
        [void]$directories.Add($anchor)
    }

    return @($directories | Select-Object -Unique)
}

function Get-AxLogFileInfo {
    param($Server)

    $files = @()
    foreach ($directory in (Get-AxLogDirectories -Server $Server)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
        $files += @(Get-ChildItem -LiteralPath $directory -Filter '*.log' -File -ErrorAction SilentlyContinue)
    }

    $latest = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        return [ordered]@{ path = ''; updatedAt = $null }
    }

    return [ordered]@{
        path      = $latest.FullName
        updatedAt = $latest.LastWriteTime.ToString('o')
    }
}

function Get-AxTargets {
    param([Parameter(Mandatory)][ValidateSet('logs', 'caches')][string]$Category)

    $all = @(
        [ordered]@{ id = 'watchdog-logs';    category = 'logs';   path = 'C:\UEWatchdog\logs';  keepNewest = 0; maxAgeDays = 7;  activeFile = 'C:\UEWatchdog\logs\watchdog.log' }
        [ordered]@{ id = 'iis-logs';         category = 'logs';   path = 'C:\inetpub\logs';     keepNewest = 0; maxAgeDays = 7;  activeFile = '' }
        [ordered]@{ id = 'windows-temp';     category = 'caches'; path = 'C:\Windows\Temp';     keepNewest = 0; maxAgeDays = 1;  activeFile = '' }
        [ordered]@{ id = 'watchdog-backups'; category = 'caches'; path = 'C:\UEWatchdog\backups'; keepNewest = 5; maxAgeDays = 0; activeFile = '' }
    )
    return @($all | Where-Object { $_.category -eq $Category })
}

function Get-AxTargetFiles {
    param($Target)

    if (-not (Test-Path -LiteralPath $Target.path -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $Target.path -File -Recurse -ErrorAction SilentlyContinue)
}

function Get-AxCleanableFiles {
    param($Target, [switch]$All)

    $files = @(Get-AxTargetFiles -Target $Target | Sort-Object LastWriteTime -Descending)
    if ($All) {
        return @($files | Where-Object { $_.FullName -ne $Target.activeFile })
    }
    if ($Target.keepNewest -gt 0 -and $files.Count -gt $Target.keepNewest) {
        $files = @($files | Select-Object -Skip $Target.keepNewest)
    }
    if ($Target.maxAgeDays -gt 0) {
        $cutoff = (Get-Date).AddDays(-[double]$Target.maxAgeDays)
        $files = @($files | Where-Object { $_.LastWriteTime -lt $cutoff })
    }
    return $files
}

function Get-AxScan {
    param([string]$Category, [bool]$All)

    $targets = @()
    foreach ($target in (Get-AxTargets -Category $Category)) {
        $exists = Test-Path -LiteralPath $target.path -PathType Container
        $all = @(Get-AxTargetFiles -Target $target)
        $cleanable = @(Get-AxCleanableFiles -Target $target -All:$All)
        $totalSum = $all | Measure-Object -Property Length -Sum
        $cleanSum = $cleanable | Measure-Object -Property Length -Sum
        $targets += [ordered]@{
            id             = $target.id
            path           = $target.path
            exists         = $exists
            fileCount      = $all.Count
            totalBytes     = [long]$totalSum.Sum
            cleanableCount = $cleanable.Count
            cleanableBytes = [long]$cleanSum.Sum
        }
    }
    return [ordered]@{ targets = $targets }
}

function Invoke-AxClean {
    param([string]$Category, [bool]$All)

    $targets = @()
    foreach ($target in (Get-AxTargets -Category $Category)) {
        $removedFiles = 0
        $removedBytes = 0
        $failures = @()

        foreach ($file in (Get-AxCleanableFiles -Target $target -All:$All)) {
            try {
                $size = $file.Length
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                $removedFiles++
                $removedBytes += $size
            }
            catch {
                $failures += [ordered]@{ path = $file.FullName; error = $_.Exception.Message }
            }
        }

        # 全部清除时，正在写入的日志文件不能删除，改为清空内容。
        if ($All -and -not [string]::IsNullOrWhiteSpace($target.activeFile) -and
            (Test-Path -LiteralPath $target.activeFile -PathType Leaf)) {
            try {
                $activeSize = (Get-Item -LiteralPath $target.activeFile).Length
                [IO.File]::WriteAllText($target.activeFile, '')
                $removedFiles++
                $removedBytes += $activeSize
            }
            catch {
                $failures += [ordered]@{ path = $target.activeFile; error = $_.Exception.Message }
            }
        }

        $targets += [ordered]@{
            id           = $target.id
            path         = $target.path
            removedFiles = $removedFiles
            removedBytes = [long]$removedBytes
            failures     = $failures
        }
    }
    return [ordered]@{ targets = $targets }
}

function Get-AxCrashRoots {
    return @(
        [ordered]@{ id = 'crossingvoid-crashes'; path = 'C:\Users\Administrator\Desktop\WindowsServer\CrossingVoid\Saved\Crashes'; keepNewest = 20 }
        [ordered]@{ id = 'narutobp-crashes';    path = 'C:\Users\Administrator\Desktop\BP_Server\WindowsServer\NarutoBP\Saved\Crashes'; keepNewest = 20 }
        [ordered]@{ id = 'fantasy-crashes';     path = 'C:\Users\Administrator\Desktop\幻杀_Server\WindowsServer\FantasyProject\Saved\Crashes'; keepNewest = 20 }
    )
}

function Get-AxCrashScan {
    $targets = @()
    foreach ($root in (Get-AxCrashRoots)) {
        $exists = Test-Path -LiteralPath $root.path -PathType Container
        $dirs = @()
        $files = @()
        if ($exists) {
            $dirs = @(Get-ChildItem -LiteralPath $root.path -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
            $files = @(Get-ChildItem -LiteralPath $root.path -Recurse -File -ErrorAction SilentlyContinue)
        }
        $cleanableDirs = @()
        if ($dirs.Count -gt $root.keepNewest) {
            $cleanableDirs = @($dirs | Select-Object -Skip $root.keepNewest)
        }
        $cleanableBytes = 0
        foreach ($dir in $cleanableDirs) {
            $cleanableBytes += (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
        }
        $targets += [ordered]@{
            id             = $root.id
            path           = $root.path
            exists         = $exists
            fileCount      = $dirs.Count
            totalBytes     = [long](($files | Measure-Object -Property Length -Sum).Sum)
            cleanableCount = $cleanableDirs.Count
            cleanableBytes = [long]$cleanableBytes
        }
    }
    return [ordered]@{ targets = $targets }
}

function Invoke-AxCrashClean {
    $targets = @()
    foreach ($root in (Get-AxCrashRoots)) {
        $removedFiles = 0
        $removedBytes = 0
        $failures = @()
        $dirs = @()
        if (Test-Path -LiteralPath $root.path -PathType Container) {
            $dirs = @(Get-ChildItem -LiteralPath $root.path -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        }
        $cleanableDirs = @()
        if ($dirs.Count -gt $root.keepNewest) {
            $cleanableDirs = @($dirs | Select-Object -Skip $root.keepNewest)
        }
        foreach ($dir in $cleanableDirs) {
            try {
                $size = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum).Sum
                Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop
                $removedFiles++
                $removedBytes += $size
            }
            catch {
                $failures += [ordered]@{ path = $dir.FullName; error = $_.Exception.Message }
            }
        }
        $targets += [ordered]@{
            id           = $root.id
            path         = $root.path
            removedFiles = $removedFiles
            removedBytes = [long]$removedBytes
            failures     = $failures
        }
    }
    return [ordered]@{ targets = $targets }
}

function Get-AxServerProcesses {
    param($Server)

    $targetPath = [System.IO.Path]::GetFullPath([string]$Server.ExePath)
    $escaped = $targetPath.Replace('\', '\\')
    return @(Get-CimInstance Win32_Process -Filter "ExecutablePath = '$escaped'" -ErrorAction SilentlyContinue)
}

function Invoke-AxInstanceAction {
    param($Config, [string]$InstanceName, [string]$Mode)

    $server = Get-AxServerEntry -Config $Config -InstanceName $InstanceName
    $processes = Get-AxServerProcesses -Server $server
    $stopped = 0

    if ($Mode -eq 'stop' -or $Mode -eq 'restart') {
        foreach ($process in $processes) {
            try {
                Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
                $stopped++
            }
            catch {
                # The process may have already exited.
            }
        }
        if ($Mode -eq 'stop') {
            return [ordered]@{ instance = $InstanceName; stopped = $stopped; started = $null }
        }
        Start-Sleep -Seconds 3
    }

    $exePath = [string]$server.ExePath
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "executable not found: $exePath"
    }
    $workingDirectory = Split-Path -Parent $exePath
    if ($server.ContainsKey('WorkingDirectory') -and -not [string]::IsNullOrWhiteSpace([string]$server.WorkingDirectory)) {
        $workingDirectory = [string]$server.WorkingDirectory
    }
    $started = Start-Process -FilePath $exePath `
        -ArgumentList ([string]$server.Arguments) `
        -WorkingDirectory $workingDirectory `
        -WindowStyle Minimized -PassThru

    return [ordered]@{ instance = $InstanceName; stopped = $stopped; started = $started.Id }
}

function Set-AxMaintenance {
    param($Config, [string]$InstanceName, [bool]$Enabled)

    $menuCore = 'C:\UEWatchdog\UEWatchdogMenu.Core.ps1'
    if (-not (Test-Path -LiteralPath $menuCore -PathType Leaf)) {
        throw "watchdog menu core not found: $menuCore"
    }
    . $menuCore

    $path = 'C:\UEWatchdog\maintenance.txt'
    if ($Config.ContainsKey('MaintenanceFile') -and -not [string]::IsNullOrWhiteSpace([string]$Config.MaintenanceFile)) {
        $path = [string]$Config.MaintenanceFile
    }
    Set-ServerMaintenance -Path $path -Name $InstanceName -Enabled $Enabled
    return [ordered]@{ instance = $InstanceName; maintenance = $Enabled }
}

function Invoke-AxWatchdogTask {
    param([bool]$Enabled)

    if ($Enabled) {
        Start-ScheduledTask -TaskName 'UEWatchdog' -ErrorAction Stop
    }
    else {
        Stop-ScheduledTask -TaskName 'UEWatchdog' -ErrorAction Stop
    }
    return [ordered]@{ enabled = $Enabled }
}

function Get-AxWatchdogTaskState {
    $task = Get-ScheduledTask -TaskName 'UEWatchdog' -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        return [ordered]@{ exists = $false; state = 'Missing' }
    }
    return [ordered]@{ exists = $true; state = [string]$task.State }
}

function Invoke-AxRemoteAction {
    $result = [ordered]@{ success = $false; action = $Action; message = ''; data = $null }
    try {
        $config = Get-AxWatchdogConfig
        switch ($Action) {
            'logs' {
                # 只返回文件位置；日志内容由 AxTools 用 scp 取回后本地读取，
                # 避免在服务端读取正在被写入的日志文件。
                if ([string]::IsNullOrWhiteSpace($InstanceName) -or $InstanceName -ieq 'watchdog') {
                    $watchdogLog = 'C:\UEWatchdog\logs\watchdog.log'
                    $updatedAt = $null
                    if (Test-Path -LiteralPath $watchdogLog -PathType Leaf) {
                        $updatedAt = (Get-Item -LiteralPath $watchdogLog).LastWriteTime.ToString('o')
                    }
                    $result.data = [ordered]@{
                        instance  = 'watchdog'
                        label     = 'Watchdog'
                        path      = $watchdogLog
                        updatedAt = $updatedAt
                    }
                }
                else {
                    $entry = Get-AxServerEntry -Config $config -InstanceName $InstanceName
                    $tail = Get-AxLogFileInfo -Server $entry
                    $label = [string]$entry.Name
                    if ($entry.ContainsKey('DisplayName') -and -not [string]::IsNullOrWhiteSpace([string]$entry.DisplayName)) {
                        $label = [string]$entry.DisplayName
                    }
                    $result.data = [ordered]@{
                        instance  = [string]$entry.Name
                        label     = $label
                        path      = $tail.path
                        updatedAt = $tail.updatedAt
                    }
                }
            }
            'scan-old-logs' { $result.data = Get-AxScan -Category 'logs' -All $false }
            'clean-old-logs' { $result.data = Invoke-AxClean -Category 'logs' -All $false }
            'scan-all-logs' { $result.data = Get-AxScan -Category 'logs' -All $true }
            'clean-all-logs' { $result.data = Invoke-AxClean -Category 'logs' -All $true }
            'scan-caches' { $result.data = Get-AxScan -Category 'caches' -All $false }
            'clean-caches' { $result.data = Invoke-AxClean -Category 'caches' -All $false }
            'scan-crashes' { $result.data = Get-AxCrashScan }
            'clean-crashes' { $result.data = Invoke-AxCrashClean }
            'start' { $result.data = Invoke-AxInstanceAction -Config $config -InstanceName $InstanceName -Mode 'start' }
            'stop' { $result.data = Invoke-AxInstanceAction -Config $config -InstanceName $InstanceName -Mode 'stop' }
            'restart' { $result.data = Invoke-AxInstanceAction -Config $config -InstanceName $InstanceName -Mode 'restart' }
            'maintenance-on' { $result.data = Set-AxMaintenance -Config $config -InstanceName $InstanceName -Enabled $true }
            'maintenance-off' { $result.data = Set-AxMaintenance -Config $config -InstanceName $InstanceName -Enabled $false }
            'watchdog-task-on' { $result.data = Invoke-AxWatchdogTask -Enabled $true }
            'watchdog-task-off' { $result.data = Invoke-AxWatchdogTask -Enabled $false }
            'watchdog-task-state' { $result.data = Get-AxWatchdogTaskState }
            default { throw "unsupported action: $Action" }
        }
        $result.success = $true
    }
    catch {
        $result.message = $_.Exception.Message
    }

    [Console]::Out.Write(($result | ConvertTo-Json -Depth 8 -Compress))
}

Invoke-AxRemoteAction
