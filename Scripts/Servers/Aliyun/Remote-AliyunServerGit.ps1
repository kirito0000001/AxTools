[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Action,
    [Parameter(Mandatory)][string]$ProgramKey,
    [string]$CommitMessage = '',
    [string]$TargetRef = '',
    # 180 秒对现在的产物不够：服务端启动时会跑游戏自己的 DreamChunkDownloader，
    # 它对着 CDN/GitHub 反复 404 重试（5s→10s→20s…），实测端口要 ~183 秒才监听。
    # 这个值同时是"等多久算失败"的上限，放宽到 7 分钟。
    [int]$HealthTimeoutSeconds = 420
)

# Runs on the Aliyun server under Windows PowerShell 5.1.
# Deploy-AliyunServerGit.ps1 会带上 UTF-8 BOM 再 scp，因此这里可以出现中文；
# 千万别用不带 BOM 的方式手改服务器上那份，否则中文会变乱码（不会影响解析，但很难看）。
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

# ---- 进度事件 ----------------------------------------------------------------
# 和本机脚本同一套协议：stdout 上写 "::axtools {json}"，AxTools 端解析成进度条。
# 必须用 [Console]::Out 直写，不能用 Write-Output —— 后者会被调用方的函数返回值
# 吞进结果数据里。最终那份结果 JSON 仍然是最后一次性写 stdout，本机 wrapper 会把
# 事件行和 JSON 分开，两者互不干扰。
function Write-AxEvent {
    param(
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Stage,
        [int]$Percent = -1,
        [string]$Message = '',
        [string]$Detail = ''
    )
    $payload = [ordered]@{ type = $Type; stage = $Stage }
    if ($Percent -ge 0) { $payload.percent = $Percent }
    if (-not [string]::IsNullOrWhiteSpace($Message)) { $payload.message = $Message }
    if (-not [string]::IsNullOrWhiteSpace($Detail)) { $payload.detail = $Detail }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress -Depth 4))
    [Console]::Out.Flush()
}

function Write-AxStage {
    param([Parameter(Mandatory)][string]$Stage, [string]$Message = '', [string]$Detail = '')
    Write-AxEvent -Type 'stage' -Stage $Stage -Message $Message -Detail $Detail
}

function Write-AxProgress {
    param([Parameter(Mandatory)][string]$Stage, [Parameter(Mandatory)][int]$Percent, [string]$Message = '', [string]$Detail = '')
    Write-AxEvent -Type 'progress' -Stage $Stage -Percent $Percent -Message $Message -Detail $Detail
}

function Get-AxShortSha {
    param([string]$Sha)
    if ([string]::IsNullOrWhiteSpace($Sha)) { return '(空)' }
    if ($Sha.Length -le 8) { return $Sha }
    return $Sha.Substring(0, 8)
}

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
    # Windows PowerShell 5.1 的坑：$ErrorActionPreference='Stop' 时，把写 stderr 的
    # 原生命令输出赋给变量（`$out = & git ... 2>&1`）会直接抛 RemoteException，消息
    # 就是那行 stderr。而 git checkout -f 恰恰会把 "HEAD is now at ..." 写到 stderr，
    # 于是"其实已经切换成功"被当成失败（2026-09-19 实测：切换后服务端被留在维护模式）。
    # 原生命令的成败只能看 ExitCode，所以这里临时放开 EAP。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & (Get-AxGit) @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
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

function Get-WqlCommandLineFilter {
    param([string[]]$LikeValues)

    # WMI 全表扫描在 1 核服务器上要 300~800ms：先用 WQL LIKE 让 WMI 只把命令行里带这些关键字的
    # 进程传回来（本地实测 315 个进程 920ms -> 190ms）。LIKE 只当粗筛，真正的判定留在调用方的
    # -like/-match 里，所以这里只要保证"只多不漏"：
    #   * 反斜杠在 WQL 字符串里是转义符，必须写成 \\：写单个 \ 会让查询直接报"无效查询"，而
    #     -ErrorAction SilentlyContinue 会把这种错误吞成"零进程"，静默漏掉进程；
    #   * [ 在 LIKE 里是字符集，会把路径当成另一种模式去匹配，不能保证超集；
    #   * ' 会截断字符串字面量。
    # 后两种字符出现时返回空串，调用方退回全表扫描：结果一样，只是慢一点。
    $clauses = @()
    foreach ($value in @($LikeValues)) {
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        if ($value.Contains('[') -or $value.Contains("'")) { return '' }
        $clauses += "CommandLine LIKE '%$($value.Replace('\', '\\'))%'"
    }
    if ($clauses.Count -eq 0) { return '' }
    return ($clauses -join ' OR ')
}

function Get-AxProcessesForServer {
    param($ServerEntry)
    # ExePath 是空串（或含 Windows 非法字符）时 GetFullPath 会抛非终止错误：调用方只看得到
    # "没找到进程"，错误本身却会污染输出/日志。这里自己吞掉，统一按"匹配不到任何进程"处理
    # （配置写错应该表现为"找不到"，而不是往外抛异常）。
    $target = ''
    if (-not [string]::IsNullOrWhiteSpace([string]$ServerEntry.ExePath)) {
        try {
            $target = [IO.Path]::GetFullPath([string]$ServerEntry.ExePath)
        }
        catch {
            $target = ''
        }
    }
    if ([string]::IsNullOrWhiteSpace($target)) {
        return @()
    }

    $escaped = $target.Replace('\', '\\')
    $exeName = [IO.Path]::GetFileName($target)
    $port = [int]$ServerEntry.Port
    $portToken = "-PORT=$port"

    $matches = @()
    $matches += @(Get-CimInstance Win32_Process -Filter "ExecutablePath = '$escaped'" -ErrorAction SilentlyContinue)

    # 只按 ExePath 找是不够的：配置可以指向包根部的启动器，而真正占着文件、吃着端口的
    # 是它拉起的内层进程（CrossingVoidServer-Win64-Shipping.exe）。漏掉内层进程就停不
    # 干净，紧接着的 git checkout 会因为"文件被占用"整片失败（2026-09-19 实测踩过：
    # unable to unlink ... Invalid argument），最后连回滚都动不了。
    #
    # 判定的两个关键字（端口参数、内层 exe 名）正好可以拿去缩小 WMI 的范围，省掉大部分
    # 枚举开销；LIKE 只是粗筛，真正的判定仍在下面这个 Where-Object 里。
    $cimParameters = @{ ClassName = 'Win32_Process' }
    $commandLineFilter = Get-WqlCommandLineFilter -LikeValues @($portToken, $exeName)
    if (-not [string]::IsNullOrWhiteSpace($commandLineFilter)) {
        $cimParameters['Filter'] = $commandLineFilter
    }

    $matches += @(Get-CimInstance @cimParameters -ErrorAction SilentlyContinue | Where-Object {
            $commandLine = [string]$_.CommandLine
            $matched = $false
            if (-not [string]::IsNullOrWhiteSpace($commandLine)) {
                if ($commandLine -like "*$portToken*") { $matched = $true }
                elseif (-not [string]::IsNullOrWhiteSpace($exeName) -and $commandLine -like "*$exeName*") { $matched = $true }
            }
            $matched
        })

    # 端口的实际占用者也一定算进来，避免命令行长什么样都不认识。
    $portOwnerIds = @()
    if ([string]$ServerEntry.Protocol -ieq 'UDP') {
        $portOwnerIds = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
            ForEach-Object { [int]$_.OwningProcess })
    }
    else {
        $portOwnerIds = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { [int]$_.OwningProcess })
    }
    foreach ($ownerId in $portOwnerIds) {
        if ($ownerId -le 0) { continue }
        $matches += @(Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction SilentlyContinue)
    }

    return @($matches | Where-Object { $null -ne $_ } | Sort-Object ProcessId -Unique)
}

function Set-AxMaintenanceState {
    param([string]$InstanceName, [bool]$Enabled)
    if (-not (Test-Path -LiteralPath $MenuCore -PathType Leaf)) {
        throw "watchdog menu core not found: $MenuCore"
    }
    . $MenuCore
    Set-ServerMaintenance -Path $MaintenanceFile -Name $InstanceName -Enabled $Enabled
}

# 批量解除维护模式，并把失败原样带回来。
# 之前的写法是 `try { ... } catch { }`：解除失败被吞掉，面板照样显示"已恢复看门狗自动重启"，
# 而服务器其实还锁在维护模式里 —— 看门狗永远不接管，只有人工能救。
function Release-AxMaintenance {
    param([string[]]$InstanceNames)

    $failed = @()
    foreach ($name in $InstanceNames) {
        try {
            Set-AxMaintenanceState -InstanceName $name -Enabled $false
        }
        catch {
            $failed += [ordered]@{ instance = $name; error = $_.Exception.Message }
        }
    }
    return $failed
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
    param(
        $Config,
        [string]$InstanceName,
        [int]$TimeoutSeconds,
        [int]$PercentFrom = -1,
        [int]$PercentTo = -1
    )
    $entry = Get-AxEntry -Config $Config -InstanceName $InstanceName
    $port = [int]$entry.Port
    $protocol = [string]$entry.Protocol
    $started = Get-Date
    $deadline = $started.AddSeconds($TimeoutSeconds)
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
        if ($PercentFrom -ge 0 -and $PercentTo -ge $PercentFrom) {
            $elapsed = ((Get-Date) - $started).TotalSeconds
            $ratio = [Math]::Min(1.0, $elapsed / [Math]::Max(1, $TimeoutSeconds))
            $state = if (-not $running) { '进程未运行' } elseif (-not $listening) { '进程在，端口未监听' } else { '就绪' }
            Write-AxProgress `
                -Stage 'health' `
                -Percent ([int]($PercentFrom + ($PercentTo - $PercentFrom) * $ratio)) `
                -Message ("正在等待 " + $InstanceName + " 就绪（已等 " + [int]$elapsed + " 秒 / 最多 " + $TimeoutSeconds + " 秒）") `
                -Detail ($state + '：' + $protocol + ' ' + [string]$port)
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

    Write-AxStage -Stage 'prepare' -Message '正在读取服务器仓库与实例状态…'

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

    Write-AxStage -Stage 'prepare' -Message '正在检查服务器仓库与配置…' -Detail ($root)

    try {
        foreach ($name in $instances) {
            Set-AxMaintenanceState -InstanceName $name -Enabled $true
            $maintenanceOn += $name
        }
        $report.steps += 'maintenance-on'
        Write-AxProgress `
            -Stage 'maintenance' `
            -Percent 6 `
            -Message '已进入维护模式，看门狗暂时不会自动重启' `
            -Detail (($instances -join '、') + '；这一步是为了避免切换过程中被自动拉起。')

        foreach ($name in $instances) {
            $stopped = Stop-AxInstance -Config $config -InstanceName $name
            $report.steps += "stopped:$name=$stopped"
        }
        Write-AxProgress `
            -Stage 'stop' `
            -Percent 12 `
            -Message '已停止服务端实例' `
            -Detail (($report.steps | Where-Object { $_ -like 'stopped:*' }) -join '，')

        # git checkout 不报百分比（stderr 不是终端时 git 不出进度），所以这一段是不确定进度，
        # 进度条会转圈、计时器在走；真正的百分比等到健康检查阶段才有。
        Write-AxStage `
            -Stage 'checkout' `
            -Message '正在把服务器目录切换到目标版本…' `
            -Detail ('目标 ' + (Get-AxShortSha -Sha $targetSha) + '；这一步要写上万个小文件，最慢的一段。')
        Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('checkout', '-f', $targetSha) | Out-Null
        Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/deployed', $targetSha) | Out-Null
        $report.steps += 'checkout'
        Write-AxProgress `
            -Stage 'checkout' `
            -Percent 55 `
            -Message '服务器目录已切换到目标版本' `
            -Detail ('deployed -> ' + (Get-AxShortSha -Sha $targetSha))

        foreach ($name in $instances) {
            # 别写成 $pid = ...：$PID 是 PowerShell 的只读自动变量，赋值会抛
            # "无法覆盖变量 PID" —— 于是"其实已经切好、进程也已经起来"却被判成失败
            # （2026-09-19 实测：切换成功的瞬间报了失败，还把服务端回滚了一遍）。
            $startedId = Start-AxInstance -Config $config -InstanceName $name
            $report.steps += "started:$name=$startedId"
        }
        Write-AxProgress `
            -Stage 'start' `
            -Percent 60 `
            -Message '已启动服务端实例，开始等待端口就绪' `
            -Detail (($report.steps | Where-Object { $_ -like 'started:*' }) -join '，')

        $health = @()
        $total = $instances.Count
        for ($index = 0; $index -lt $total; $index++) {
            $from = 62 + [int](35 * $index / $total)
            $to = 62 + [int](35 * ($index + 1) / $total)
            $health += (Test-AxInstanceHealth -Config $config -InstanceName $instances[$index] `
                    -TimeoutSeconds $HealthTimeoutSeconds -PercentFrom $from -PercentTo $to)
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
            Write-AxProgress `
                -Stage 'previous' `
                -Percent 97 `
                -Message '已记录上一版本，随时可以退回去' `
                -Detail ('previous -> ' + (Get-AxShortSha -Sha $deployedBefore))
        }

        $report.success = $true
    }
    catch {
        $report.success = $false
        $report.message = $_.Exception.Message

        # Put the previously deployed tree back, but keep maintenance on so the
        # watchdog cannot start a half-switched server while we report the failure.
        if (-not [string]::IsNullOrWhiteSpace($deployedBefore) -and $deployedBefore -ne $targetSha) {
            Write-AxStage `
                -Stage 'restore' `
                -Message '切换失败，正在恢复到切换前的版本…' `
                -Detail ($report.message)
            try {
                foreach ($name in $instances) { [void](Stop-AxInstance -Config $config -InstanceName $name) }
                Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('checkout', '-f', $deployedBefore) | Out-Null
                Invoke-AxGitOrThrow -Arguments @("--git-dir=$repo", 'update-ref', 'refs/heads/deployed', $deployedBefore) | Out-Null
                foreach ($name in $instances) { [void](Start-AxInstance -Config $config -InstanceName $name) }
                $report.restored = $true
                Write-AxProgress `
                    -Stage 'restore' `
                    -Percent 95 `
                    -Message '已恢复到切换前的版本，服务端重新启动' `
                    -Detail ('deployed -> ' + (Get-AxShortSha -Sha $deployedBefore))
            }
            catch {
                $report.restoreError = $_.Exception.Message
                Write-AxStage `
                    -Stage 'restore' `
                    -Message '恢复也失败了，服务端保持维护模式，需要人工介入' `
                    -Detail ($report.restoreError)
            }
        }
    }
    finally {
        if ($report.success -or $report.restored) {
            $releaseFailures = @(Release-AxMaintenance -InstanceNames $maintenanceOn)
            if ($releaseFailures.Count -eq 0) {
                $report.maintenanceReleased = $true
                Write-AxProgress -Stage 'maintenance' -Percent 99 -Message '已恢复看门狗自动重启'
            }
            else {
                $report.maintenanceReleased = $false
                $report.maintenanceReleaseError = (($releaseFailures | ForEach-Object { '{0}: {1}' -f $_.instance, $_.error }) -join '; ')
                Write-AxStage `
                    -Stage 'maintenance' `
                    -Message '解除维护模式失败，服务端仍处于维护模式，需要人工介入' `
                    -Detail $report.maintenanceReleaseError
            }
        }
        else {
            $report.maintenanceReleased = $false
        }
    }

    return $report
}

# 把服务端从"停着 / 卡在维护模式"救回来：对齐工作目录 → 起进程 → 健康检查 → 放开看门狗。
# 之前的失败路径只保证"能恢复就恢复"，恢复本身再失败就没人管了 —— 这个动作就是那个兜底。
function Invoke-AxRecover {
    param([string]$Key)

    $root = Get-AxProgramRoot -Key $Key
    $repo = Get-AxRepoPath -Key $Key
    $config = Get-AxServerConfig
    $instances = Get-AxProgramInstances -Key $Key
    $deployed = Get-AxRef -Repo $repo -RefName 'refs/heads/deployed'
    if ([string]::IsNullOrWhiteSpace($deployed)) {
        throw "deployed ref not found; initialization is required before recovery: $repo"
    }

    $maintenanceOn = @()
    $report = [ordered]@{
        program = $Key
        action  = 'recover'
        root    = $root
        target  = (Get-AxCommitInfo -Repo $repo -RefName 'refs/heads/deployed')
        steps   = @()
        health  = @()
    }

    Write-AxStage -Stage 'prepare' -Message '正在检查服务器仓库与配置…' -Detail $root

    try {
        # 先把维护模式打开并停掉进程，再动工作目录：进程还活着的时候 git checkout
        # 会因为文件被占用整片失败（"unable to unlink ... Invalid argument"），
        # 那样目录会停在一个半新半旧的混合状态。
        foreach ($name in $instances) {
            Set-AxMaintenanceState -InstanceName $name -Enabled $true
            $maintenanceOn += $name
        }
        Write-AxProgress -Stage 'maintenance' -Percent 8 -Message '已暂时接管，看门狗先不参与重启'

        foreach ($name in $instances) {
            $stopped = Stop-AxInstance -Config $config -InstanceName $name
            $report.steps += "stopped:$name=$stopped"
        }
        Write-AxProgress `
            -Stage 'stop' `
            -Percent 14 `
            -Message '已停止残留的服务端进程' `
            -Detail (($report.steps | Where-Object { $_ -like 'stopped:*' }) -join '，')

        # 工作目录必须和 deployed 一致，否则"启动"起来跑的是一份混着两版的目录。
        Write-AxStage -Stage 'checkout' -Message '正在把服务器目录对齐到部署版本…' -Detail ('deployed ' + (Get-AxShortSha -Sha $deployed))
        $dirty = Invoke-AxGit -Arguments @("--git-dir=$repo", "--work-tree=$root", 'status', '--porcelain')
        if ($dirty.ExitCode -ne 0 -or $dirty.Output.Count -gt 0) {
            Invoke-AxRepoGit -Repo $repo -WorkTree $root -Arguments @('checkout', '-f', $deployed) | Out-Null
            $report.steps += "checkout(差异 $($dirty.Output.Count) 项)"
        }
        else {
            $report.steps += 'checkout(已一致)'
        }
        Write-AxProgress `
            -Stage 'checkout' `
            -Percent 50 `
            -Message '服务器目录已对齐到部署版本' `
            -Detail ('deployed -> ' + (Get-AxShortSha -Sha $deployed))

        foreach ($name in $instances) {
            $startedId = Start-AxInstance -Config $config -InstanceName $name
            $report.steps += "started:$name=$startedId"
        }
        Write-AxProgress `
            -Stage 'start' `
            -Percent 60 `
            -Message '已重新启动服务端实例' `
            -Detail (($report.steps | Where-Object { $_ -like 'started:*' }) -join '，')

        $health = @()
        $total = $instances.Count
        for ($index = 0; $index -lt $total; $index++) {
            $from = 62 + [int](35 * $index / $total)
            $to = 62 + [int](35 * ($index + 1) / $total)
            $health += (Test-AxInstanceHealth -Config $config -InstanceName $instances[$index] `
                    -TimeoutSeconds $HealthTimeoutSeconds -PercentFrom $from -PercentTo $to)
        }
        $report.health = $health
        $unhealthy = @($health | Where-Object { -not $_.healthy })
        if ($unhealthy.Count -gt 0) {
            $names = ($unhealthy | ForEach-Object { $_.instance }) -join ', '
            throw "health check failed after $HealthTimeoutSeconds seconds: $names"
        }

        $report.success = $true
    }
    catch {
        $report.success = $false
        $report.message = $_.Exception.Message
    }
    finally {
        # 只有真的健康起来才放开看门狗；否则保持维护模式，别让它在半死状态里反复重启。
        if ($report.success) {
            $releaseFailures = @(Release-AxMaintenance -InstanceNames $maintenanceOn)
            if ($releaseFailures.Count -eq 0) {
                $report.maintenanceReleased = $true
                Write-AxProgress -Stage 'maintenance' -Percent 99 -Message '已恢复看门狗自动重启'
            }
            else {
                $report.maintenanceReleased = $false
                $report.maintenanceReleaseError = (($releaseFailures | ForEach-Object { '{0}: {1}' -f $_.instance, $_.error }) -join '; ')
                Write-AxStage `
                    -Stage 'maintenance' `
                    -Message '解除维护模式失败，服务端仍处于维护模式，需要人工介入' `
                    -Detail $report.maintenanceReleaseError
            }
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
            'recover' { $result.data = Invoke-AxRecover -Key $ProgramKey }
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
