[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entryScript = Join-Path $scriptsRoot 'Diagnostics\Invoke-RunnerSelfTest.ps1'
if (-not (Test-Path -LiteralPath $entryScript -PathType Leaf)) {
    throw "诊断入口不存在：$entryScript"
}

function Assert-Contract {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-DiagnosticScenario {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario,

        [string]$CancellationFile
    )

    $previousCancellationFile = $env:AXTOOLS_CANCEL_FILE
    try {
        $env:AXTOOLS_CANCEL_FILE = $CancellationFile
        $lines = @(& (Join-Path $PSHOME 'pwsh.exe') `
            -NoLogo -NoProfile -NonInteractive -File $entryScript -Scenario $Scenario 2>&1 |
            ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:AXTOOLS_CANCEL_FILE = $previousCancellationFile
    }

    $events = @($lines |
        Where-Object { $_.StartsWith('::axtools ', [StringComparison]::Ordinal) } |
        ForEach-Object {
            $json = $_.Substring('::axtools '.Length)
            try { $json | ConvertFrom-Json -ErrorAction Stop } catch { $null }
        } |
        Where-Object { $null -ne $_ })

    [pscustomobject]@{
        Scenario = $Scenario
        ExitCode = $exitCode
        Lines = $lines
        Events = $events
        Result = @($events | Where-Object type -eq 'result')[-1]
    }
}

$success = Invoke-DiagnosticScenario -Scenario 'success'
Assert-Contract ($success.ExitCode -eq 0) 'success 场景退出码不是 0。'
Assert-Contract ($success.Result.status -eq 'success') 'success 场景缺少成功 result。'
Assert-Contract (@($success.Events | Where-Object type -eq 'progress').Count -ge 3) 'success 场景进度事件不足。'

$failure = Invoke-DiagnosticScenario -Scenario 'failure'
Assert-Contract ($failure.ExitCode -ne 0) 'failure 场景必须返回非零退出码。'
Assert-Contract ($failure.Result.status -eq 'failed') 'failure 场景缺少失败 result。'

$utf8 = Invoke-DiagnosticScenario -Scenario 'utf8'
Assert-Contract ($utf8.ExitCode -eq 0) 'utf8 场景退出失败。'
Assert-Contract ($utf8.Lines -contains '中文输出正常：构建、停止、发布') 'UTF-8 中文输出不完整。'

$invalid = Invoke-DiagnosticScenario -Scenario 'invalid'
Assert-Contract ($invalid.Lines -contains '::axtools not-json') 'invalid 场景未输出无效协议行。'
Assert-Contract ($invalid.Result.status -eq 'success') 'invalid 场景没有最终成功结果。'

$locked = Invoke-DiagnosticScenario -Scenario 'locked'
Assert-Contract (@($locked.Events | Where-Object { $_.type -eq 'cancellation' -and $_.mode -eq 'locked' }).Count -eq 1) 'locked 场景缺少锁定事件。'
Assert-Contract ($locked.Result.status -eq 'success') 'locked 场景没有完成。'

$cancelFile = Join-Path ([IO.Path]::GetTempPath()) "AxTools-contract-$([guid]::NewGuid().ToString('N')).signal"
try {
    Set-Content -LiteralPath $cancelFile -Value 'stop' -Encoding utf8NoBOM
    $stopped = Invoke-DiagnosticScenario -Scenario 'stop' -CancellationFile $cancelFile
    Assert-Contract ($stopped.ExitCode -eq 130) 'stop 场景退出码必须为 130。'
    Assert-Contract ($stopped.Result.status -eq 'stopped') 'stop 场景缺少 stopped result。'
}
finally {
    Remove-Item -LiteralPath $cancelFile -Force -ErrorAction SilentlyContinue
}

Write-Output 'PASS: 6 个 AxTaskProtocol 场景通过。'
exit 0
