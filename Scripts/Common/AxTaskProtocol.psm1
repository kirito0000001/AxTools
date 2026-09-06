Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'
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
    Write-Output "::axtools $json"
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
        percent = [Math]::Clamp($Percent, 0, 100)
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
