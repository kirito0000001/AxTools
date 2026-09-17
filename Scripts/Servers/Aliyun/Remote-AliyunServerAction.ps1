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

function Get-AxLogTail {
    param($Server, [int]$TailLines)

    $files = @()
    foreach ($directory in (Get-AxLogDirectories -Server $Server)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
        $files += @(Get-ChildItem -LiteralPath $directory -Filter '*.log' -File -ErrorAction SilentlyContinue)
    }

    $latest = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        return [ordered]@{ path = ''; updatedAt = $null; lines = @() }
    }

    $lines = @(Get-Content -LiteralPath $latest.FullName -Tail $TailLines -ErrorAction SilentlyContinue)
    return [ordered]@{
        path      = $latest.FullName
        updatedAt = $latest.LastWriteTime.ToString('o')
        lines     = $lines
    }
}

function Get-AxCacheTargets {
    return @(
        [ordered]@{ id = 'watchdog-backups'; path = 'C:\UEWatchdog\backups'; keepNewest = 5; maxAgeDays = 0 }
        [ordered]@{ id = 'watchdog-logs';    path = 'C:\UEWatchdog\logs';    keepNewest = 0; maxAgeDays = 7 }
        [ordered]@{ id = 'windows-temp';     path = 'C:\Windows\Temp';       keepNewest = 0; maxAgeDays = 1 }
        [ordered]@{ id = 'iis-logs';         path = 'C:\inetpub\logs';       keepNewest = 0; maxAgeDays = 7 }
    )
}

function Get-AxCleanableFiles {
    param($Target)

    if (-not (Test-Path -LiteralPath $Target.path -PathType Container)) {
        return @()
    }

    $files = @(Get-ChildItem -LiteralPath $Target.path -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($Target.keepNewest -gt 0 -and $files.Count -gt $Target.keepNewest) {
        $files = @($files | Select-Object -Skip $Target.keepNewest)
    }
    if ($Target.maxAgeDays -gt 0) {
        $cutoff = (Get-Date).AddDays(-[double]$Target.maxAgeDays)
        $files = @($files | Where-Object { $_.LastWriteTime -lt $cutoff })
    }
    return $files
}

function Get-AxCacheScan {
    $targets = @()
    foreach ($target in (Get-AxCacheTargets)) {
        $exists = Test-Path -LiteralPath $target.path -PathType Container
        $all = @()
        if ($exists) {
            $all = @(Get-ChildItem -LiteralPath $target.path -File -Recurse -ErrorAction SilentlyContinue)
        }
        $cleanable = @(Get-AxCleanableFiles -Target $target)
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

function Invoke-AxCacheClean {
    $targets = @()
    foreach ($target in (Get-AxCacheTargets)) {
        $removedFiles = 0
        $removedBytes = 0
        $failures = @()
        foreach ($file in (Get-AxCleanableFiles -Target $target)) {
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
                        lines     = @(Get-Content -LiteralPath $watchdogLog -Tail $TailLines -ErrorAction SilentlyContinue)
                    }
                }
                else {
                    $entry = Get-AxServerEntry -Config $config -InstanceName $InstanceName
                    $tail = Get-AxLogTail -Server $entry -TailLines $TailLines
                    $label = [string]$entry.Name
                    if ($entry.ContainsKey('DisplayName') -and -not [string]::IsNullOrWhiteSpace([string]$entry.DisplayName)) {
                        $label = [string]$entry.DisplayName
                    }
                    $result.data = [ordered]@{
                        instance  = [string]$entry.Name
                        label     = $label
                        path      = $tail.path
                        updatedAt = $tail.updatedAt
                        lines     = $tail.lines
                    }
                }
            }
            'scan-caches' { $result.data = Get-AxCacheScan }
            'clean-caches' { $result.data = Invoke-AxCacheClean }
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
