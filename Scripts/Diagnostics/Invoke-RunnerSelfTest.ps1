[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('success', 'failure', 'stop', 'utf8', 'invalid', 'locked')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common\AxTaskProtocol.psm1') -Force

switch ($Scenario) {
    'success' {
        Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止后续自检步骤。'
        Write-AxTaskStage -Stage 'environment' -Message '正在检查 PowerShell 7 环境...'
        foreach ($percent in @(5, 30, 60, 85, 100)) {
            if (Test-AxTaskCancellation) {
                Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message '用户停止了运行器自检。'
                exit 130
            }

            Write-AxTaskProgress `
                -Stage 'diagnostics' `
                -Percent $percent `
                -Message '正在验证统一任务运行器...' `
                -Detail "中文 UTF-8 进度 $percent%"
            Start-Sleep -Milliseconds 220
        }

        Write-Output '普通输出：PowerShell 7 UTF-8 正常。'
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '运行器自检成功。'
        exit 0
    }

    'failure' {
        Set-AxTaskCancellationMode -Mode 'stop' -Message '失败测试可以停止。'
        Write-AxTaskStage -Stage 'failure-injection' -Message '正在注入可控失败...'
        Write-Output '模拟构建失败：未生成任何真实产物。'
        Write-AxTaskResult -Status 'failed' -ExitCode 41 -Message '模拟任务按预期失败。'
        exit 41
    }

    'stop' {
        Set-AxTaskCancellationMode -Mode 'stop' -Message '点击进度圆环可以停止。'
        Write-AxTaskStage -Stage 'long-running' -Message '正在等待停止请求...'
        foreach ($step in 1..40) {
            if (Test-AxTaskCancellation) {
                Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message '已收到停止信号。'
                exit 130
            }

            Write-AxTaskProgress `
                -Stage 'long-running' `
                -Percent ([Math]::Min(95, $step * 2.5)) `
                -Message '停止测试正在运行...' `
                -Detail "等待停止信号，第 $step 步"
            Start-Sleep -Milliseconds 180
        }

        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '停止测试自然完成。'
        exit 0
    }

    'utf8' {
        Write-Output '中文输出正常：构建、停止、发布'
        Write-AxTaskProgress -Stage 'utf8' -Percent 100 -Message '中文结构化事件正常。'
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message 'UTF-8 验证成功。'
        exit 0
    }

    'invalid' {
        Write-Output '::axtools not-json'
        Write-Output "`e[31mANSI 输出已生成，运行器应清理颜色。`e[0m"
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '无效事件已被隔离。'
        exit 0
    }

    'locked' {
        Set-AxTaskCancellationMode -Mode 'locked' -Message '正在模拟不可中断提交阶段。'
        Write-AxTaskProgress -Stage 'commit' -Percent 50 -Message '不可停止阶段...' -Detail '停止入口应被禁用。'
        Start-Sleep -Milliseconds 700
        Write-AxTaskProgress -Stage 'commit' -Percent 100 -Message '不可停止阶段完成。'
        Set-AxTaskCancellationMode -Mode 'stop' -Message '后续步骤可以停止。'
        Write-AxTaskResult -Status 'success' -ExitCode 0 -Message '不可停止阶段验证成功。'
        exit 0
    }
}
