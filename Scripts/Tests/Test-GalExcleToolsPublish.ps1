[CmdletBinding()]
param()

# 剧情填表（TFAC剧情箱）热更新发布转发的静态 + 行为契约。
# 全程用**假的工具脚本**（临时目录里记参数、退 0），所以不会真的编译或碰网络。
# 令牌类环境变量在整个用例期间被清空/替换，免得把这台机器上真实的令牌带进断言。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\GalExcleTools\Invoke-GalExcleToolsPublish.ps1'
Assert-True (Test-Path -LiteralPath $entry -PathType Leaf) "剧情填表发布转发入口不存在：$entry"

# ① 静态合同：固定白名单 + 转发的是工具仓库自己的脚本 + 只有 pwsh 7 能起。
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $entry
Assert-True ($source -match "ValidateSet\((?s).*'BuildUpdatePackage'.*'PublishDryRun'.*'Publish'") '发布动作必须使用固定白名单。'
Assert-True ($source -match 'Build-GalExcelUpdatePackage\.ps1') '出包必须转发到工具仓库的 Build-GalExcelUpdatePackage.ps1。'
Assert-True ($source -match 'Publish-GalExcelRelease\.ps1') '发布必须转发到工具仓库的 Publish-GalExcelRelease.ps1。'
Assert-True ($source -match 'Shell\\7\\pwsh\.exe|PowerShell 7') '必须优先使用 PowerShell 7 起子进程。'
Assert-True ($source -match 'AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME') 'AI 运行时剔除必须有独立的环境开关。'
Assert-True ($source -match 'AXTOOLS_GALEXCEL_INCREMENTAL_BUILD') '增量构建开关必须存在。'
Assert-True ($source -match 'AXTOOLS_GALEXCEL_SKIP_GITHUB') '跳 GitHub 通道的环境开关必须存在。'
# 真相源必须留在工具仓库：本层不得自己压包或自己发 API。
# （只检查"没有真的发请求"：环境检查里出现 curl.exe 这个名字是允许的。）
Assert-True ($source -notmatch 'Invoke-RestMethod|--fail-with-body|attach_files') '转发层不得自己发网络请求。'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-GalPublish-' + [Guid]::NewGuid().ToString('N'))
$toolRoot = Join-Path $testRoot 'GalExcleTools'
$toolScripts = Join-Path $toolRoot 'Scripts'
$outputRoot = Join-Path $testRoot 'DabaoV'
$argsLog = Join-Path $testRoot 'tool-args.log'
$previousLog = $env:AXTOOLS_TEST_GAL_ARGS
$tokenNames = @(
    'GALEXCEL_GITEE_TOKEN', 'AXTOOLS_GITEE_TOKEN', 'FANTASYTOOLS_GITEE_TOKEN',
    'CROSSINGVOID_GITEE_TOKEN', 'GITEE_TOKEN', 'GITEE_ACCESS_TOKEN')
$originalTokens = @{}
foreach ($name in $tokenNames) { $originalTokens[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function New-FakeToolScript {
    param([Parameter(Mandatory)][string]$Name)
    # 不带 param 块：任何参数都会进 $args，正好用来核对转发内容。
    # 顺带记下子进程里有没有拿到 Gitee 令牌（令牌走环境变量、不走命令行）。
    @'
$token = [Environment]::GetEnvironmentVariable('GALEXCEL_GITEE_TOKEN', 'Process')
Set-Content -LiteralPath $env:AXTOOLS_TEST_GAL_ARGS -Value ("$($MyInvocation.MyCommand.Name) :: " + ($args -join ' ') + ' :: token=' + $token) -Encoding UTF8
exit 0
'@ | Set-Content -LiteralPath (Join-Path $toolScripts $Name) -Encoding UTF8
}

function Get-LastCall {
    if (!(Test-Path -LiteralPath $argsLog)) { return '' }
    return ([IO.File]::ReadAllText($argsLog, [Text.Encoding]::UTF8)).Trim()
}

try {
    New-Item -ItemType Directory -Force -Path $toolScripts, $outputRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $toolRoot 'GalExcleTools.csproj') -Value '<Project />' -Encoding UTF8
    foreach ($name in @(
            'Build-GalExcelUpdatePackage.ps1',
            'GalExcelPublish.ps1',
            'Publish-GalExcelRelease.ps1',
            'Publish-GalExcelGiteeRelease.ps1',
            'Publish-GalExcelGithubRelease.ps1',
            'Set-GalExcelVersion.ps1')) {
        New-FakeToolScript -Name $name
    }
    $env:AXTOOLS_TEST_GAL_ARGS = $argsLog
    foreach ($name in $tokenNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }

    # ② 出包：参数里要有版本、产物目录（OutputRoot\v<版本>）、AI 剔除与增量构建。
    & $entry -Action BuildUpdatePackage -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0' -ReleaseNotes '填表修复'
    Assert-True ($LASTEXITCODE -eq 0) "出包转发失败：$LASTEXITCODE"
    $call = Get-LastCall
    Assert-True ($call -match 'Build-GalExcelUpdatePackage\.ps1') "出包没有转发到出包脚本：$call"
    Assert-True ($call -match '-Version 2\.3\.0') "出包没带版本号：$call"
    Assert-True ($call -match [regex]::Escape((Join-Path $outputRoot 'v2.3.0'))) "产物目录没有对齐 OutputRoot\v<版本>：$call"
    Assert-True ($call -match '-ExcludeAiRuntime') "出包默认应剔除 AI 推理运行时：$call"
    Assert-True ($call -match '-IncrementalBuild') "出包默认应复用编译缓存：$call"
    Assert-True ($call -match '-Notes') "出包应转发 Release 公告：$call"

    # ③ 关掉 AI 剔除开关后不该再传这个参数。
    $env:AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME = '0'
    & $entry -Action BuildUpdatePackage -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0'
    Assert-True ($LASTEXITCODE -eq 0) "关掉 AI 剔除后出包失败：$LASTEXITCODE"
    $call = Get-LastCall
    Assert-True ($call -notmatch '-ExcludeAiRuntime') "AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME=0 时不该再传 -ExcludeAiRuntime：$call"
    [Environment]::SetEnvironmentVariable('AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME', $null, 'Process')

    # ④ 发布演练：转发到编排层、带 -DryRun，不要求令牌，也不该放行占位更新器。
    & $entry -Action PublishDryRun -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0'
    Assert-True ($LASTEXITCODE -eq 0) "发布演练转发失败：$LASTEXITCODE"
    $call = Get-LastCall
    Assert-True ($call -match 'Publish-GalExcelRelease\.ps1') "发布没有转发到编排层：$call"
    Assert-True ($call -match '-DryRun') "发布演练必须带 -DryRun：$call"
    Assert-True ($call -notmatch '-AllowStubUpdater') "发布演练默认不该放行占位更新器：$call"

    # ⑤ 令牌预检必须在脚本里：一台机器上"一个令牌都没有"的状态没法在用例里造出来
    #    （User/Machine 作用域里真配着令牌，测试不该去改它），所以这里只静态核对。
    Assert-True ($source -match '缺少 Gitee 令牌') '真实发布必须先做令牌预检并给出提示。'

    # ⑥ 令牌复用：这个工具自己没配 GALEXCEL_GITEE_TOKEN 时，把 AxTools 侧已有的令牌
    #    （GUI 令牌框写的 AXTOOLS_GITEE_TOKEN）以**进程环境变量**递给子进程 —— 不走命令行参数。
    [Environment]::SetEnvironmentVariable('GALEXCEL_GITEE_TOKEN', $null, 'Process')
    $env:AXTOOLS_GITEE_TOKEN = 'test-reused-token'
    & $entry -Action Publish -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0'
    Assert-True ($LASTEXITCODE -eq 0) "带复用令牌的完整发布转发失败：$LASTEXITCODE"
    $call = Get-LastCall
    Assert-True ($call -match 'Publish-GalExcelRelease\.ps1') "完整发布没有转发到编排层：$call"
    Assert-True ($call -notmatch '-DryRun') "完整发布不该带 -DryRun：$call"
    Assert-True ($call -match 'token=test-reused-token') "AxTools 侧令牌没有递给子进程：$call"
    Assert-True ($call -notmatch '-GiteeAccessToken') "令牌不该走命令行参数：$call"

    # ⑦ 占位更新器只允许在演练里放行：真实发布必须被拒。
    & $entry -Action Publish -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0' -AllowStubUpdater *> $null
    Assert-True ($LASTEXITCODE -ne 0) '真实发布不该放行占位更新器。'
    & $entry -Action PublishDryRun -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0' -AllowStubUpdater
    Assert-True ($LASTEXITCODE -eq 0) "演练放行占位更新器失败：$LASTEXITCODE"
    Assert-True ((Get-LastCall) -match '-AllowStubUpdater') "演练应把 -AllowStubUpdater 转发下去。"

    # ⑧ 工具仓库缺脚本时要明确拒绝（不能装作发布成功）。
    Remove-Item -LiteralPath (Join-Path $toolScripts 'Build-GalExcelUpdatePackage.ps1') -Force
    & $entry -Action BuildUpdatePackage -ProjectRoot $toolRoot -OutputRoot $outputRoot -Version '2.3.0' *> $null
    Assert-True ($LASTEXITCODE -ne 0) '工具仓库缺出包脚本时不该成功。'

    Write-Output 'PASS: 剧情填表热更新发布转发契约通过。'
    $global:LASTEXITCODE = 0
}
finally {
    $env:AXTOOLS_TEST_GAL_ARGS = $previousLog
    foreach ($name in $tokenNames) { [Environment]::SetEnvironmentVariable($name, $originalTokens[$name], 'Process') }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
