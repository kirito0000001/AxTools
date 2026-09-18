[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Action,
    [Parameter(Mandatory)][string]$ProgramKey,
    [string]$CommitMessage = '',
    [string]$TargetRef = '',
    [int]$HealthTimeoutSeconds = 180
)

# Runs on the Aliyun server. Kept ASCII-only so Windows PowerShell 5.1 parses it
# regardless of file encoding. Chinese display labels are mapped on the AxTools side.
#
# Repository shape: a bare repo per program plus an external work tree that IS the
# live server directory. The work tree never receives a .git directory, and Saved/
# is excluded, so player data is never touched by a checkout.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$RepoRoot = 'C:\Users\Administrator\Desktop\ServerRepos'
$ConfigPath = 'C:\UEWatchdog\watchdog.config.psd1'
$MaintenanceFile = 'C:\UEWatchdog\maintenance.txt'
$MenuCore = 'C:\UEWatchdog\UEWatchdogMenu.Core.ps1'

$script:Git = $null
function Get-AxGit {
    if ($null -ne $script:Git) { return $script:Git }
    foreach ($candidate in @('C:\Program Files\Git\mingw64\bin\git.exe', 'C:\Program Files\Git\cmd\git.exe')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $script:Git = $candidate
            return $script:Git
        }
    }
    $command = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { throw 'git not found on server' }
    $script:Git = $command.Source
    return $script:Git
}

function Get-AxProgramInstances {
    param([string]$Key)
    switch ($Key) {
        'crossingvoid' { return @('CrossingVoid-Login', 'CrossingVoid-Main') }
        'narutobp' { return @('NarutoBP') }
        'fantasyproject' { return @('FantasyProject') }
        default { throw "unknown program: $Key" }
    }
}

# The package root is the nearest ancestor of the configured executable that holds
# an Engine directory. This keeps the mapping rule identical for all three programs
# without hardcoding any non-ASCII path.
function Get-AxProgramRoot {
    param([string]$Key)
    $config = Import-PowerShellDataFile -LiteralPath $ConfigPath
    $instances = Get-AxProgramInstances -Key $Key
    $entry = $config.Servers | Where-Object { $instances -contains [string]$_.Name } | Select-Object -First 1
    if ($null -eq $entry) { throw "no watchdog entry found for program: $Key" }

    $cursor = Split-Path -Parent ([string]$entry.ExePath)
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        if (Test-Path -LiteralPath (Join-Path $cursor 'Engine') -PathType Container) {
            return $cursor
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    throw "cannot locate package root (no Engine directory) for program: $Key"
}

function Get-AxRepoPath {
    param([string]$Key)
    return (Join-Path $RepoRoot ($Key + '.git'))
}

function Invoke-AxGit {
    param([string[]]$Arguments)
    $output = & (Get-AxGit) @Arguments 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $code
        Output   = @($output | ForEach-Object { [string]$_ })
    }
}

function Invoke-AxGitOrThrow {
    param([string[]]$Arguments)
    $result = Invoke-AxGit -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw ("git {0} failed ({1}): {2}" -f ($Arguments -join ' '), $result.ExitCode, ($result.Output -join ' | '))
    }
    return $result
}

function Invoke-AxRepoGit {
    param([string]$Repo, [string]$WorkTree, [string[]]$Arguments)
    return Invoke-AxGitOrThrow -Arguments (@("--git-dir=$Repo", "--work-tree=$WorkTree") + $Arguments)
}

function Get-AxRef {
    param([string]$Repo, [string]$RefName)
    $result = Invoke-AxGit -Arguments @("--git-dir=$Repo", 'rev-parse', '--verify', '--quiet', $RefName)
    if ($result.ExitCode -ne 0 -or $result.Output.Count -eq 0) { return '' }
    return $result.Output[0].Trim()
}

function Get-AxCommitInfo {
    param([string]$Repo, [string]$RefName)
    $sha = Get-AxRef -Repo $Repo -RefName $RefName
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    $format = '%H%n%h%n%aI%n%s'
    $result = Invoke-AxGit -Arguments @("--git-dir=$Repo", 'log', '-1', "--format=$format", $sha)
    if ($result.ExitCode -ne 0 -or $result.Output.Count -lt 4) { return $null }
    return [ordered]@{
        sha     = $result.Output[0]
        short   = $result.Output[1]
        date    = $result.Output[2]
        subject = $result.Output[3]
    }
}

function Get-AxProcessesForServer {
    param($ServerEntry)
    $target = [IO.Path]::GetFullPath([string]$ServerEntry.ExePath)
    $escaped = $target.Replace('\', '\\')
    return @(Get-CimInstance Win32_Process -Filter "ExecutablePath = '$escaped'" -ErrorAction SilentlyContinue)
}

function Set-AxMaintenanceState {
    param([string]$InstanceName, [bool]$Enabled)
    if (-not (Test-Path -LiteralPath $MenuCore -PathType Leaf)) {
        throw "watchdog menu core not found: $MenuCore"
    }
    . $MenuCore
    Set-ServerMaintenance -Path $MaintenanceFile -Name $InstanceName -Enabled $Enabled
}

function Get-AxServerConfig {
    return (Import-PowerShellDataFile -LiteralPath $ConfigPath)
}

function Get-AxEntry {
    param($Config, [string]$InstanceName)
    $entry = $Config.Servers | Where-Object { [string]$_.Name -ieq $InstanceName } | Select-Object -First 1
    if ($null -eq $entry) { throw "unknown instance: $InstanceName" }
    return $entry
}

function Stop-AxInstance {
    param($Config, [string]$InstanceName)
    $entry = Get-AxEntry -Config $Config -InstanceName $InstanceName
    $stopped = 0
    foreach ($process in (Get-AxProcessesForServer -ServerEntry $entry)) {
        try {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
            $stopped++
        }
        catch {
            # Process may have already exited.
        }
    }

    # Wait until the executable really disappears so file replacement cannot race.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ((Get-AxProcessesForServer -ServerEntry $entry).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    return $stopped
}

function Start-AxInstance {
    param($Config, [string]$InstanceName)
    $entry = Get-AxEntry -Config $Config -InstanceName $InstanceName
    $exePath = [string]$entry.ExePath
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "executable not found: $exePath"
    }
    $workingDirectory = Split-Path -Parent $exePath
    if ($entry.ContainsKey('WorkingDirectory') -and -not [string]::IsNullOrWhiteSpace([string]$entry.WorkingDirectory)) {
        $workingDirectory = [string]$entry.WorkingDirectory
    }
    $started = Start-Process -FilePath $exePath `
        -ArgumentList ([string]$entry.Arguments) `
        -WorkingDirectory $workingDirectory `
        -WindowStyle Minimized -PassThru
    return $started.Id
}

function Test-AxInstanceHealth {
    param($Config, [string]$InstanceName, [int]$TimeoutSeconds)
    $entry = Get-AxEntry -Config $Config -InstanceName $InstanceName
    $port = [int]$entry.Port
    $protocol = [string]$entry.Protocol
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $running = $false
    $listening = $false
    while ((Get-Date) -lt $deadline) {
        $running = (Get-AxProcessesForServer -ServerEntry $entry).Count -gt 0
        if ($running) {
            if ($protocol -ieq 'UDP') {
                $listening = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0
            }
            else {
                $listening = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0
            }
            if ($listening) { break }
        }
        Start-Sleep -Seconds 3
    }
    return [ordered]@{
        instance  = $InstanceName
        running   = $running
        listening = $listening
        port      = $port
        healthy   = ($running -and $listening)
    }
}

function Write-AxSupportFiles {
    param([string]$Root)
    $gitignorePath = Join-Path $Root '.gitignore'
    $gitattributesPath = Join-Path $Root '.gitattributes'

    $gitignore = @(
        '# AxTools: runtime data must never enter version control.'
        'Saved/'
        '*.pdb'
        '*.dmp'
        '*.log'
    ) -join "`n"

    $gitattributes = @(
        '# AxTools: binary payloads must never be text-normalized.'
        '* -text'
        '*.exe -diff'
        '*.dll -diff'
        '*.pak -diff'
        '*.pdb -diff'
    ) -join "`n"

    [IO.File]::WriteAllText($gitignorePath, $gitignore + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($gitattributesPath, $gitattributes + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-AxRepoSizeBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [long]$sum
}

function Invoke-AxBootstrap {
    param([string]$Key, [string]$Message)

    $root = Get-AxProgramRoot -Key $Key
    $repo = Get-AxRepoPath -Key $Key

    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $RepoRoot -Force | Out-Null
    }

    $alreadyInitialized = $false
    if (Test-Path -LiteralPath $repo -PathType Container) {
        $existing = Get-AxRef -Repo $repo -RefName 'refs/heads/main'
        if (-not [string]::IsNullOrWhiteSpace($existing)) { $alreadyInitialized = $true }
    }
    if ($alreadyInitialized) {
        throw "repository already initialized: $repo"
    }

    if (-not (Test-Path -LiteralPath $repo -PathType Container)) {
        Invoke-AxGitOrThrow -Arguments @('init', '--bare', '--initial-branch=main', $repo) | Out-Null
    }

    foreach ($pair in @(
            @('gc.auto', '0'),
            @('receive.autogc', 'false'),
            @('core.autocrlf', 'false'),
            @('core.longpaths', 'true'),
            @('core.bigFileThreshold', '64m'),
            @('advice.detachedHead', 'false'),
            @('user.name', 'AxTools'),
            @('user.email', 'axtools@crossingvoid.local'))) {
        Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'config', $pair[0], $pair[1]) | Out-Null
    }

    Write-AxSupportFiles -Root $root

    Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('add', '-A') | Out-Null

    $messagePath = Join-Path $env:TEMP ('axgitmsg_' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        [IO.File]::WriteAllText($messagePath, $Message, [Text.UTF8Encoding]::new($false))
        Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('commit', '-q', '-F', $messagePath) | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $messagePath -Force -ErrorAction SilentlyContinue
    }

    $sha = Get-AxRef -Repo $repo -RefName 'refs/heads/main'
    Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/deployed', $sha) | Out-Null

    $status = Invoke-AxGit -Arguments @("--git-dir=$repo", "--work-tree=$root", 'status', '--porcelain')
    $tracked = Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'ls-tree', '-r', '--name-only', 'refs/heads/main')

    return [ordered]@{
        program    = $Key
        repo       = $repo
        worktree   = $root
        commit     = (Get-AxCommitInfo -Repo $repo -RefName 'refs/heads/main')
        fileCount  = $tracked.Output.Count
        cleanTree  = ($status.Output.Count -eq 0)
        repoBytes  = (Get-AxRepoSizeBytes -Path $repo)
        workBytes  = (Get-AxRepoSizeBytes -Path $root)
    }
}

function Invoke-AxStatus {
    param([string]$Key)

    $root = Get-AxProgramRoot -Key $Key
    $repo = Get-AxRepoPath -Key $Key
    $initialized = (Test-Path -LiteralPath $repo -PathType Container) -and
        -not [string]::IsNullOrWhiteSpace((Get-AxRef -Repo $repo -RefName 'refs/heads/main'))

    $result = [ordered]@{
        program          = $Key
        initialized      = $initialized
        repo             = $repo
        worktree         = $root
        main             = $null
        deployed         = $null
        previous         = $null
        pendingCommit    = $false
        canSwitchLatest  = $false
        rollbackPossible = $false
        instances        = @()
    }

    if (-not $initialized) {
        return $result
    }

    $result.main = Get-AxCommitInfo -Repo $repo -RefName 'refs/heads/main'
    $result.deployed = Get-AxCommitInfo -Repo $repo -RefName 'refs/heads/deployed'
    $result.previous = Get-AxCommitInfo -Repo $repo -RefName 'refs/heads/previous'

    # 版本之间是"切换"关系：main 是最新推送版，previous 是上一个版本，
    # deployed 是当前实际在跑的那个。两个开关分别指向 main 和 previous。
    if ($null -ne $result.main -and $null -ne $result.deployed) {
        $result.pendingCommit = ($result.main.sha -ne $result.deployed.sha)
        $result.canSwitchLatest = $result.pendingCommit
        $result.latestTarget = $result.main.sha
    }

    if ($null -ne $result.previous -and $null -ne $result.deployed) {
        $result.rollbackPossible = ($result.previous.sha -ne $result.deployed.sha)
        if ($result.rollbackPossible) { $result.rollbackTarget = $result.previous.sha }
    }

    $config = Get-AxServerConfig
    $instances = @()
    foreach ($name in (Get-AxProgramInstances -Key $Key)) {
        $entry = Get-AxEntry -Config $config -InstanceName $name
        $processes = Get-AxProcessesForServer -ServerEntry $entry
        $port = [int]$entry.Port
        $listening = if ([string]$entry.Protocol -ieq 'UDP') {
            @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0
        }
        else {
            @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0
        }
        $instances += [ordered]@{
            name      = $name
            running   = ($processes.Count -gt 0)
            pid       = $(if ($processes.Count -gt 0) { [int]$processes[0].ProcessId } else { $null })
            port      = $port
            listening = $listening
        }
    }
    $result.instances = $instances
    $result.repoBytes = Get-AxRepoSizeBytes -Path $repo

    return $result
}

function Invoke-AxSwitchTo {
    param([string]$Key, [string]$Target, [string]$Label, [bool]$RecordPrevious = $false)

    $root = Get-AxProgramRoot -Key $Key
    $repo = Get-AxRepoPath -Key $Key
    $targetSha = Get-AxRef -Repo $repo -RefName $Target
    if ([string]::IsNullOrWhiteSpace($targetSha)) {
        throw "target ref not found: $Target"
    }

    $deployedBefore = Get-AxRef -Repo $repo -RefName 'refs/heads/deployed'
    $config = Get-AxServerConfig
    $instances = Get-AxProgramInstances -Key $Key
    $maintenanceOn = @()
    $report = [ordered]@{
        program          = $Key
        action           = $Label
        target           = (Get-AxCommitInfo -Repo $repo -RefName $Target)
        previousDeployed = $deployedBefore
        steps            = @()
    }

    try {
        foreach ($name in $instances) {
            Set-AxMaintenanceState -InstanceName $name -Enabled $true
            $maintenanceOn += $name
        }
        $report.steps += 'maintenance-on'

        foreach ($name in $instances) {
            $stopped = Stop-AxInstance -Config $config -InstanceName $name
            $report.steps += "stopped:$name=$stopped"
        }

        Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('checkout', '-f', $targetSha) | Out-Null
        Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/deployed', $targetSha) | Out-Null
        $report.steps += 'checkout'

        foreach ($name in $instances) {
            $pid = Start-AxInstance -Config $config -InstanceName $name
            $report.steps += "started:$name=$pid"
        }

        $health = @()
        foreach ($name in $instances) {
            $health += (Test-AxInstanceHealth -Config $config -InstanceName $name -TimeoutSeconds $HealthTimeoutSeconds)
        }
        $report.health = $health
        $unhealthy = @($health | Where-Object { -not $_.healthy })
        if ($unhealthy.Count -gt 0) {
            $names = ($unhealthy | ForEach-Object { $_.instance }) -join ', '
            throw "health check failed after $HealthTimeoutSeconds seconds: $names"
        }

        # 切换成功后，把刚才在跑的那一版记为"上一个版本"。失败时不记录，
        # 保证 previous 永远是一个真的跑起来过、可以退回去的版本。
        if ($RecordPrevious -and -not [string]::IsNullOrWhiteSpace($deployedBefore) -and $deployedBefore -ne $targetSha) {
            Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/previous', $deployedBefore) | Out-Null
            $report.previousAfter = $deployedBefore
        }

        $report.success = $true
    }
    catch {
        $report.success = $false
        $report.message = $_.Exception.Message

        # Put the previously deployed tree back, but keep maintenance on so the
        # watchdog cannot start a half-switched server while we report the failure.
        if (-not [string]::IsNullOrWhiteSpace($deployedBefore) -and $deployedBefore -ne $targetSha) {
            try {
                foreach ($name in $instances) { [void](Stop-AxInstance -Config $config -InstanceName $name) }
                Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('checkout', '-f', $deployedBefore) | Out-Null
                Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/deployed', $deployedBefore) | Out-Null
                foreach ($name in $instances) { [void](Start-AxInstance -Config $config -InstanceName $name) }
                $report.restored = $true
            }
            catch {
                $report.restoreError = $_.Exception.Message
            }
        }
    }
    finally {
        if ($report.success -or $report.restored) {
            foreach ($name in $maintenanceOn) {
                try { Set-AxMaintenanceState -InstanceName $name -Enabled $false } catch { }
            }
            $report.maintenanceReleased = $true
        }
        else {
            $report.maintenanceReleased = $false
        }
    }

    return $report
}

function Invoke-AxRemoteGitAction {
    $result = [ordered]@{ success = $false; action = $Action; message = ''; data = $null }
    try {
        switch ($Action) {
            'bootstrap' { $result.data = Invoke-AxBootstrap -Key $ProgramKey -Message $CommitMessage }
            'status' { $result.data = Invoke-AxStatus -Key $ProgramKey }
            'apply' {
                $result.data = Invoke-AxSwitchTo -Key $ProgramKey -Target 'refs/heads/main' `
                    -Label 'apply' -RecordPrevious $true
            }
            'rollback' {
                $repo = Get-AxRepoPath -Key $ProgramKey
                if ([string]::IsNullOrWhiteSpace((Get-AxRef -Repo $repo -RefName 'refs/heads/previous'))) {
                    throw 'no previous version available for rollback'
                }
                $result.data = Invoke-AxSwitchTo -Key $ProgramKey -Target 'refs/heads/previous' `
                    -Label 'rollback' -RecordPrevious $false
            }
            default { throw "unsupported action: $Action" }
        }
        $result.success = $true
    }
    catch {
        $result.message = $_.Exception.Message
    }

    [Console]::Out.Write(($result | ConvertTo-Json -Depth 10 -Compress))
}

Invoke-AxRemoteGitAction
