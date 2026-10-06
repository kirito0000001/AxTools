Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($null -ne (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue)) { $PSStyle.OutputRendering = 'PlainText' }
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Write-AxTaskEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Data
    )

    if (-not $Data.Contains('type') -or [string]::IsNullOrWhiteSpace([string]$Data.type)) {
        throw 'AxTask 事件必须包含 type。'
    }

    $json = $Data | ConvertTo-Json -Compress -Depth 8
    # 直接写 stdout，不走 Write-Output：协议行是给宿主进程读的，一旦被 Write-Output
    # 发出就会被调用方的 `$x = SomeFunction` 当成返回值收进变量，把结果数据污染成
    # "事件字符串 + 真结果"的混合数组（服务端仓库脚本的 import 就踩过这条）。
    [Console]::Out.WriteLine("::axtools $json")
}

function Write-AxTaskStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Message,
        [string]$Detail
    )

    $eventData = [ordered]@{ type = 'stage'; stage = $Stage; message = $Message }
    if (-not [string]::IsNullOrWhiteSpace($Detail)) { $eventData.detail = $Detail }
    Write-AxTaskEvent -Data $eventData
}

function Write-AxTaskProgress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][double]$Percent,
        [Parameter(Mandatory)][string]$Message,
        [string]$Detail
    )

    $eventData = [ordered]@{
        type = 'progress'
        stage = $Stage
        percent = [Math]::Max(0, [Math]::Min(100, $Percent))   # 不用 [Math]::Clamp：它是 .NET Core 2.0+ API，Windows PowerShell 5.1 没有
        message = $Message
    }
    if (-not [string]::IsNullOrWhiteSpace($Detail)) { $eventData.detail = $Detail }
    Write-AxTaskEvent -Data $eventData
}

function Write-AxTaskResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('success', 'failed', 'stopped')]
        [string]$Status,

        [Parameter(Mandatory)]
        [int]$ExitCode,

        [string]$Message
    )

    $eventData = [ordered]@{ type = 'result'; status = $Status; exitCode = $ExitCode }
    if (-not [string]::IsNullOrWhiteSpace($Message)) { $eventData.message = $Message }
    Write-AxTaskEvent -Data $eventData
}

function Set-AxTaskCancellationMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('cancel', 'stop', 'locked')]
        [string]$Mode,

        [string]$Message
    )

    $eventData = [ordered]@{ type = 'cancellation'; mode = $Mode }
    if (-not [string]::IsNullOrWhiteSpace($Message)) { $eventData.message = $Message }
    Write-AxTaskEvent -Data $eventData
}

function Test-AxTaskCancellation {
    [CmdletBinding()]
    param()

    return -not [string]::IsNullOrWhiteSpace($env:AXTOOLS_CANCEL_FILE) -and
        (Test-Path -LiteralPath $env:AXTOOLS_CANCEL_FILE -PathType Leaf)
}

Export-ModuleMember -Function @(
    'Write-AxTaskEvent',
    'Write-AxTaskStage',
    'Write-AxTaskProgress',
    'Write-AxTaskResult',
    'Set-AxTaskCancellationMode',
    'Test-AxTaskCancellation'
)
