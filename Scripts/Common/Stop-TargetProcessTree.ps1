[CmdletBinding()]
param(
    [Parameter(Mandatory)][int[]]$TargetProcessId,

    # "开发会话"成员：从目标进程沿父进程往上找，遇到它们就继续往上，最后连整棵树一起结束。
    # 为什么必须往上找：Tauri dev / Vite 的 watcher 会在应用被杀掉后立刻把它重新拉起来
    # （2026-09-20 实测：杀完 1 秒，同样的可执行文件又出现），只杀应用本身等于没杀。
    # 注意：**不要把 pwsh / powershell 放进来**。它们是"可能出现在链上"的东西，但绝不作为根 ——
    # 否则一旦调用方的 shell 命令行里也带关键词，就会顺着链把调用方自己杀掉（2026-09-20 踩过）。
    [string[]]$SessionProcessNames = @(
        'cmd', 'node', 'cargo', 'npm', 'npx', 'rustc', 'bash', 'sh', 'yarn'
    ),

    # 边界：遇到这些绝不再往上（explorer / IDE / 终端 / AxTools 自己），避免误伤它们。
    [string[]]$BoundaryProcessNames = @(
        'explorer', 'rider64', 'devenv', 'Code', 'AxTools', 'WindowsTerminal', 'wt', 'conhost', 'services', 'wininit',
        'pwsh', 'powershell'
    ),

    # 光看进程名不够：某个 cmd/node 也可能是"别人的"。只有命令行里真的提到这些开发工具，
    # 才认为它是"这次发布的那个开发会话"。否则立刻停手，绝不往上多杀一层。
    [string[]]$SessionCommandKeywords = @('tauri', 'vite', 'cargo', 'npm', 'pnpm', 'yarn')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

try {

function Get-ProcessInfo([int]$id) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    return [pscustomobject]@{
        Id             = [int]$process.ProcessId
        Name           = [string]$process.Name
        BaseName       = [IO.Path]::GetFileNameWithoutExtension([string]$process.Name)
        ParentId       = [int]$process.ParentProcessId
        ExecutablePath = [string]$process.ExecutablePath
        CommandLine    = [string]$process.CommandLine
    }
}

function Test-SessionCandidate($item) {
    if ($null -eq $item) { return $false }
    if ($SessionProcessNames -notcontains $item.BaseName) { return $false }
    $commandLine = [string]$item.CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) { return $false }
    foreach ($keyword in $SessionCommandKeywords) {
        if ($commandLine -like "*$keyword*") { return $true }
    }
    return $false
}

$sessionRoots = [System.Collections.Generic.List[object]]::new()
$chains = [System.Collections.Generic.List[object]]::new()

foreach ($targetId in $TargetProcessId) {
    $chain = [System.Collections.Generic.List[object]]::new()
    $current = Get-ProcessInfo $targetId
    $depth = 0
    while ($null -ne $current -and $depth -lt 8) {
        $chain.Add($current)
        if ($BoundaryProcessNames -contains $current.BaseName) { break }
        $current = Get-ProcessInfo $current.ParentId
        $depth++
    }

    $chains.Add([pscustomobject]@{
        Target = $targetId
        Chain  = @($chain | ForEach-Object { "$($_.BaseName)#$($_.Id)" })
    })

    # 会话根 = 链上最靠上的那个"像个开发会话的成员"（进程名对 + 命令行里提到开发工具）。
    # 一旦遇到不满足的成员就停 —— 宁可少杀一层，也不能顺着别人的 shell 往上杀。
    if ($chain.Count -eq 0) {
        # 目标已经不在了：没什么可做的
        continue
    }
    $root = $chain[0]
    foreach ($item in $chain) {
        if ($BoundaryProcessNames -contains $item.BaseName) { break }
        if (Test-SessionCandidate $item) { $root = $item; continue }
        if ($item -ne $chain[0]) { break }
    }
    $sessionRoots.Add($root)
}

$killed = [System.Collections.Generic.List[object]]::new()
foreach ($root in $sessionRoots) {
    if ($killed | Where-Object { $_.Id -eq $root.Id }) { continue }
    $output = & taskkill /PID $root.Id /T /F 2>&1
    $killed.Add([pscustomobject]@{
        Id     = $root.Id
        Name   = $root.Name
        Result = (($output | ForEach-Object { [string]$_ }) -join ' ').Trim()
    })
}

# 结束之后再查一遍：谁还在（会自我重启的、权限不够没杀掉的）
Start-Sleep -Milliseconds 800
$survivors = [System.Collections.Generic.List[object]]::new()
foreach ($targetId in $TargetProcessId) {
    $alive = Get-ProcessInfo $targetId
    if ($null -ne $alive) {
        $survivors.Add([pscustomobject]@{ Id = $alive.Id; Name = $alive.Name; Path = $alive.ExecutablePath })
    }
}

[pscustomobject]@{
    Chains    = @($chains)
    Killed    = @($killed)
    Survivors = @($survivors)
} | ConvertTo-Json -Depth 6 -Compress
}
catch {
    [pscustomobject]@{
        Error     = $_.Exception.Message
        Detail    = ($_ | Out-String)
        Chains    = @()
        Killed    = @()
        Survivors = @()
    } | ConvertTo-Json -Depth 6 -Compress
    exit 1
}
exit 0
