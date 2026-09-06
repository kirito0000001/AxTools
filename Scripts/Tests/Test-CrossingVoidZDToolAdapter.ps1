[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (!$Condition) { throw $Message }
}

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\CrossingVoidZDTool\Invoke-CrossingVoidZDToolAction.ps1'
Assert-True (Test-Path -LiteralPath $entry -PathType Leaf) "ZD 工具箱适配入口不存在：$entry"

$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $entry
Assert-True ($source -match "ValidateSet\('CheckEnvironment'.*'ReplaceRelease'\)") '动作必须使用固定白名单。'
Assert-True ($source -match 'Invoke-AxDotNetIncrementalBuild') '开发构建必须复用统一的 .NET 增量构建入口。'
Assert-True ($source -match "-Configuration\s+'Debug'") '开发构建必须固定使用 Debug。'
Assert-True ($source -match "Configuration\s*=\s*'DebugX64'") '开发构建状态必须隔离为 DebugX64。'
Assert-True ($source -notmatch 'Release 开发产物有效') '开发启动提示不得再把 Release 产物当作开发版。'
Assert-True ($source -match "(?s)Write-AxTaskProgress.{0,120}-Stage\s+'verify'.{0,120}-Percent\s+85") '编译完成后必须明确进入资源校验阶段。'
Assert-True ($source -match "(?s)Write-AxTaskProgress.{0,120}-Stage\s+'build-state'.{0,120}-Percent\s+90") '资源校验后必须明确进入构建状态写入阶段。'
Assert-True ($source -match '步骤 1/8 · 正在检查源码与 Debug 产物状态') 'ZD 开发启动必须先显示源码与产物状态检查。'
Assert-True ($source -match '步骤 2/3 · 正在启动现有 Debug 产物') 'ZD 智能直启必须明确显示启动步骤。'
Assert-True ($source -match '步骤 2/8 · 检测到变化，准备增量构建') 'ZD 重新编译必须明确说明状态检查结果。'
Assert-True ($source -match '步骤 5/8 · 正在校验 EXE 与 WinUI 资源') 'ZD 编译完成后必须列出资源校验步骤。'
Assert-True ($source -match '步骤 6/8 · 正在记录源码指纹与构建状态') 'ZD 必须明确显示构建状态记录步骤。'
Assert-True ($source -match '步骤 7/8 · 正在启动 ZD Debug 程序') 'ZD 必须明确显示 Debug 进程启动步骤。'
Assert-True ($source -match "-Detail '需要校验：EXE、PRI、App.xbf、MainWindow.xbf'") 'ZD 资源校验必须显示具体资源类型。'
Assert-True ($source -match "'--runtime',\s*'win-x64'") '开发构建必须固定使用 win-x64。'
Assert-True ($source -match "'-p:Platform=x64'") '开发构建必须固定使用 Platform=x64。'
Assert-True ($source -match 'CrossingVoidZDTool\.RegressionTests\.csproj') '回归测试必须显式运行独立测试项目。'
Assert-True ($source -match 'Test-AxDevelopmentBuildCurrent') '普通开发启动必须使用智能构建状态。'
Assert-True ($source -notmatch 'Pakout\.bat') 'AxTools 不得调用旧 Pakout.bat。'
Assert-True ($source -notmatch "'-Clean'|\s-Clean\b") 'AxTools 不得向打包脚本传入 -Clean。'
Assert-True ($source -notmatch 'Build\.bat|UnrealBuildTool') 'Unreal 同步检查不得触发 Unreal 构建。'
Assert-True ($source -notmatch "Start-Process -FilePath 'explorer\.exe'.*-WindowStyle Hidden") '打开目录时不能隐藏资源管理器窗口。'
Assert-True ($source -match 'AXTOOLS_PENDING_PACKAGE_ROOT') '待替换记录必须支持隔离测试根目录。'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-ZD-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $testRoot 'CrossingVoidZDTool'
$outputRoot = Join-Path $testRoot 'DabaoV'
$pendingRoot = Join-Path $testRoot 'PendingPackages'
$previousPendingRoot = $env:AXTOOLS_PENDING_PACKAGE_ROOT
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'Tests\CrossingVoidZDTool.RegressionTests'),$outputRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $projectRoot 'CrossingVoidZDTool.csproj') -Value '<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $projectRoot 'Tests\CrossingVoidZDTool.RegressionTests\CrossingVoidZDTool.RegressionTests.csproj') -Value '<Project />' -Encoding UTF8
    @'
param([string]$Configuration,[string]$Runtime,[string]$OutputRoot,[string]$Version,[switch]$Clean,[switch]$KeepWorkFolder)
$ErrorActionPreference = 'Stop'
$program = Join-Path $OutputRoot '零境交错：ZD工具箱V1.2.0\零境交错：ZD工具箱'
New-Item -ItemType Directory -Force -Path $program | Out-Null
foreach($name in @('零境交错：ZD工具箱.exe','零境交错：ZD工具箱.pri','App.xbf','MainWindow.xbf','零境交错：ZD工具箱.runtimeconfig.json','零境交错：ZD工具箱.deps.json')) {
    Set-Content -LiteralPath (Join-Path $program $name) -Value $name -Encoding UTF8
}
'@ | Set-Content -LiteralPath (Join-Path $projectRoot 'Pakout.ps1') -Encoding UTF8
    $env:AXTOOLS_PENDING_PACKAGE_ROOT = $pendingRoot
    $releaseExecutable = Join-Path $outputRoot '零境交错：ZD工具箱V1.2.0\零境交错：ZD工具箱\零境交错：ZD工具箱.exe'

    $stageLines = @(& $entry -Action ValidateAndStagePackage -ProjectRoot $projectRoot -OutputRoot $outputRoot -ReleaseExecutable $releaseExecutable 2>&1 | ForEach-Object { $_.ToString() })
    Assert-True ($LASTEXITCODE -eq 0) "临时打包验证失败：$($stageLines -join [Environment]::NewLine)"
    Assert-True (Test-Path -LiteralPath (Join-Path $pendingRoot 'CrossingVoidZDTool.json') -PathType Leaf) '临时打包后没有生成待替换记录。'

    $replaceLines = @(& $entry -Action ReplaceRelease -ProjectRoot $projectRoot -OutputRoot $outputRoot -ReleaseExecutable $releaseExecutable 2>&1 | ForEach-Object { $_.ToString() })
    Assert-True ($LASTEXITCODE -eq 0) "确认替换失败：$($replaceLines -join [Environment]::NewLine)"
    Assert-True (Test-Path -LiteralPath $releaseExecutable -PathType Leaf) '确认替换后正式 EXE 不存在。'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $pendingRoot 'CrossingVoidZDTool.json'))) '替换成功后仍残留待替换记录。'
}
finally {
    $env:AXTOOLS_PENDING_PACKAGE_ROOT = $previousPendingRoot
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Output 'PASS: ZD 工具箱适配器静态合同通过。'
