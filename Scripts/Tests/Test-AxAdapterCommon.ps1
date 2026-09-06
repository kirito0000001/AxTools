[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Common\AxAdapterCommon.psm1'
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $modulePath

foreach ($requiredPattern in @(
    '\[Diagnostics\.ProcessStartInfo\]::new\(\)',
    'UseShellExecute\s*=\s*\$false',
    'CreateNoWindow\s*=\s*\$true',
    'WindowStyle\s*=\s*\[Diagnostics\.ProcessWindowStyle\]::Hidden',
    '\[Diagnostics\.Process\]::Start\(\$startInfo\)')) {
    if ($source -notmatch $requiredPattern) {
        throw "Start-AxExecutable 缺少无终端启动配置：$requiredPattern"
    }
}

if ($source -match 'Start-Process\s+-FilePath\s+\$fullPath') {
    throw 'Start-AxExecutable 仍使用会为控制台程序创建终端窗口的 Start-Process。'
}

Write-Output 'PASS: 公共程序启动入口隐藏控制台窗口。'
exit 0
