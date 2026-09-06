[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}

$adapterPaths = @(
    'Adapters\AxTools\Invoke-AxToolsAction.ps1',
    'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1',
    'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1',
    'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1',
    'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1',
    'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1',
    'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1'
)

foreach ($relativePath in $adapterPaths) {
    $path = Join-Path $scriptsRoot $relativePath
    $source = Get-Content -Raw -Encoding UTF8 -LiteralPath $path
    Assert-True ($source -match "Write-AxTaskStage -Stage 'environment'") "$relativePath 缺少独立环境检查阶段。"
    Assert-True ($source -notmatch "Set-AxTaskCancellationMode[^\r\n]*\r?\n\s*Write-AxTaskStage -Stage 'environment'") "$relativePath 仍在所有动作开始前无条件检查环境。"
}

$zdSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $scriptsRoot 'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1')
$zdBuildBranch = [regex]::Match($zdSource, 'elseif \(\$Action -in @\(''BuildAndRun'',''ForceBuildAndRun''\)\)(?<body>[\s\S]*?)elseif \(\$Action -eq ''Test''\)').Groups['body'].Value
Assert-True ($zdBuildBranch.IndexOf('Test-AxDevelopmentBuildCurrent', [StringComparison]::Ordinal) -lt $zdBuildBranch.IndexOf("Resolve-AxCommand -Name 'dotnet'", [StringComparison]::Ordinal)) 'ZD 智能直启必须先命中构建状态，再解析 dotnet。'

$galSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $scriptsRoot 'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1')
$galBuildBranch = [regex]::Match($galSource, 'elseif \(\$Action -in @\(''Build'',''BuildAndRun'',''ForceBuildAndRun''\)\)(?<body>[\s\S]*?)elseif \(\$Action -eq ''SourceHealthCheck''\)').Groups['body'].Value
Assert-True ($galBuildBranch.IndexOf('Test-AxDevelopmentBuildCurrent', [StringComparison]::Ordinal) -lt $galBuildBranch.IndexOf("Resolve-AxCommand -Name 'dotnet'", [StringComparison]::Ordinal)) '剧情工具箱智能直启必须先命中构建状态，再解析 dotnet。'

foreach ($adapter in @('CrossingVoidPc','FantasyProjectPc')) {
    $source = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $scriptsRoot "Adapters\$adapter\Invoke-$($adapter)Action.ps1")
    $developmentFunction = [regex]::Match($source, 'function Invoke-[^{]+Development\s*\{(?<body>[\s\S]*?)\r?\n\}').Groups['body'].Value
    Assert-True ($developmentFunction.IndexOf('Test-AxDevelopmentBuildCurrent', [StringComparison]::Ordinal) -lt $developmentFunction.IndexOf("Resolve-AxCommand -Name 'npm.cmd'", [StringComparison]::Ordinal)) "$adapter 智能直启必须先命中构建状态，再解析 npm。"
}

$androidSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $scriptsRoot 'Adapters\CrossingVoidAndroid\Invoke-CrossingVoidAndroidAction.ps1')
Assert-True ($androidSource -notmatch '\r?\n\s*Assert-AndroidEnvironment\r?\n\s*switch \(\$Action\)') 'Android 适配器仍在每个动作前执行完整环境检查。'

Write-Output 'PASS: 环境检查已与启动和其他操作分离。'
