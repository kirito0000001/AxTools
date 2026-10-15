<#
.SYNOPSIS
    AxTools「工具-剧情填表」的动作入口：把热更新发布动作转给 TFACStorybox 工程里的出包/发布脚本。

.DESCRIPTION
    为什么这层只是转发：
      TFAC剧情箱是一个**独立工程**（有自己的 git 仓库、`Scripts\`、README），
      出包与发版的真相源必须留在那个仓库里 —— 和两个兄弟项目的做法一致
      （零境素材：`Invoke-CrossingVoidZDToolPublish.ps1`；PC 启动器：`Invoke-CrossingVoidPcAction.ps1`）。
      这样脱离 AxTools、在 GalExcleTools 目录里手敲一条命令也能完成同样的发布。

    被转发的脚本（都在 GalExcleTools 工程的 `Scripts\` 下）：
      Build-GalExcelUpdatePackage.ps1    出包：清单 + zip + sha256 + 体积墙（超限自动分卷）+ 增量包
      Publish-GalExcelRelease.ps1        ★「完整发布」编排层：同一批产物发到 Gitee + GitHub
      Publish-GalExcelGiteeRelease.ps1   单通道：Gitee（幂等 + 清单最后生效 + 清理旧附件）
      Publish-GalExcelGithubRelease.ps1  单通道：GitHub（幂等靠 digest + 从不删除旧版本）
      Set-GalExcelVersion.ps1            版本号写入 csproj（打磨动作）

    `::axtools` 协议行由被调用的脚本直接写 stdout，本层**不再重复输出**，只搬运退出码。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'CheckEnvironment',
        'BuildUpdatePackage',
        'PublishDryRun',
        'Publish',
        'SetVersion')]
    [string]$Action,

    [Parameter(Mandatory)][string]$ProjectRoot,
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [switch]$DryRun,

    # 允许把仓库里那份占位更新器（AXTOOLS-STUB）打进包 —— 只为整条彩排链路。
    # 带占位更新器的包不可发布；AxTools 侧只在「发布演练」且设了
    # AXTOOLS_GALEXCEL_ALLOW_STUB_UPDATER 时才追加这个开关。
    [switch]$AllowStubUpdater
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($null -ne (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue)) { $PSStyle.OutputRendering = 'PlainText' }
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

$toolboxScripts = ''

function Get-GalToolboxScriptPath {
    param([Parameter(Mandatory)][string]$Name)

    $path = Join-Path $toolboxScripts $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "GalExcleTools 工程里找不到脚本：$path。请确认「工具-剧情填表」的源码目录是 TFAC剧情箱工程，且已拉取最新提交。"
    }
    return $path
}

function Get-GalStagingDirectory {
    <#
        出包产物落点：跟 AxTools 给这个工具配的 OutputRoot 对齐（和其它工具一个规矩），
        没有就退回工程的 artifacts\release\v{version}。
        必须和工具侧发布脚本的默认推导保持一致，否则会各出一套包。
    #>
    param([Parameter(Mandatory)][string]$Version)

    if (![string]::IsNullOrWhiteSpace($OutputRoot)) {
        $base = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([char[]]'\/')
        return Join-Path $base "v$Version"
    }
    return Join-Path $root (Join-Path 'artifacts\release' "v$Version")
}

function Test-GalEnvFlag {
    # 读一个布尔型环境开关（Process 优先于 User）：没设 = 关闭；设成 1/true/yes = 打开。
    param([Parameter(Mandatory)][string]$Name)

    foreach ($scope in @('Process', 'User')) {
        $value = [Environment]::GetEnvironmentVariable($Name, $scope)
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim() -in @('1', 'true', 'True', 'TRUE', 'yes', 'YES')
        }
    }
    return $false
}

function Get-GalGiteeTokenSource {
    <#
        Gitee 令牌：**这个工具自己的变量优先**；没有就复用 AxTools 这台机器上已有的令牌。
        AXTOOLS_GITEE_TOKEN 就是 GUI 那个令牌框写进去的，FANTASYTOOLS_/CROSSINGVOID_ 是给别的
        工具配的 —— AxTools 自己发布时就是这么复用的，这里保持一致，
        免得出现"GUI 里明明存过令牌、工具侧却说没配"。
        返回来源名（找不到就是空串），值本身不往外打印。
    #>
    foreach ($name in @(
            'GALEXCEL_GITEE_TOKEN',
            'AXTOOLS_GITEE_TOKEN',
            'FANTASYTOOLS_GITEE_TOKEN',
            'CROSSINGVOID_GITEE_TOKEN',
            'GITEE_TOKEN',
            'GITEE_ACCESS_TOKEN')) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if (![string]::IsNullOrWhiteSpace($value)) {
                return [pscustomobject]@{ Name = $name; Scope = $scope; Token = $value.Trim() }
            }
        }
    }
    return $null
}
function Enable-GalGiteeTokenForChild {
    <#
        令牌走**进程环境变量**递给子进程，不走命令行参数 —— 免得它出现在进程列表里。
        工具自己的变量已经设过就什么都不做（那是显式配置，优先）。
    #>
    $existing = Get-GalGiteeTokenSource
    if ($null -ne $existing -and $existing.Name -eq 'GALEXCEL_GITEE_TOKEN') { return $existing }
    if ($null -eq $existing) { return $null }
    [Environment]::SetEnvironmentVariable('GALEXCEL_GITEE_TOKEN', $existing.Token, 'Process')
    return $existing
}
function Get-GalCommonBuildArguments {
    <#
        出包的公共参数（与零境素材那边同一套开关语义）：
          · AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME：默认剔除 WindowsAppSDK 传递进来的 AI 推理运行时
            （onnxruntime.dll / DirectML.dll / Microsoft.ML.OnnxRuntime.dll）。TFAC剧情箱全工程
            grep 不到任何 Windows.AI / OnnxRuntime / DirectML 用法，所以是纯陪跑体积；设成 0 可以要回来。
          · AXTOOLS_GALEXCEL_INCREMENTAL_BUILD：默认复用编译缓存；设成 0 走全量全新构建。
    #>
    param([Parameter(Mandatory)][string]$Version)

    $arguments = @(
        '-Version', $Version,
        '-ProjectRoot', $root,
        '-StagingDirectory', (Get-GalStagingDirectory -Version $Version)
    )

    $exclude = [Environment]::GetEnvironmentVariable('AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME', 'Process')
    if ([string]::IsNullOrWhiteSpace($exclude)) {
        $exclude = [Environment]::GetEnvironmentVariable('AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME', 'User')
    }
    if ($exclude -notin @('0', 'false', 'False', 'no', 'NO')) {
        $arguments += '-ExcludeAiRuntime'
    }
    else {
        Write-Host 'AXTOOLS_GALEXCEL_EXCLUDE_AI_RUNTIME=0：本次不剔除 AI 推理运行时（包会大 16 MB 左右）。' -ForegroundColor Yellow
    }

    $incremental = [Environment]::GetEnvironmentVariable('AXTOOLS_GALEXCEL_INCREMENTAL_BUILD', 'Process')
    if ([string]::IsNullOrWhiteSpace($incremental)) {
        $incremental = [Environment]::GetEnvironmentVariable('AXTOOLS_GALEXCEL_INCREMENTAL_BUILD', 'User')
    }
    if ($incremental -in @('0', 'false', 'False', 'no', 'NO')) {
        Write-Host 'AXTOOLS_GALEXCEL_INCREMENTAL_BUILD=0：本次走全量全新构建（不复用编译缓存）。' -ForegroundColor Yellow
    }
    else {
        $arguments += '-IncrementalBuild'
    }
    return $arguments
}

function Get-GalPublishArguments {
    <#
        发布链的公共参数。注意**参数是分动作、也分脚本的**：
          · 转发的是编排层 `Publish-GalExcelRelease.ps1`，它自己会按通道过滤参数；
          · `-ExcludeAiRuntime` 与 `-IncrementalBuild` 由 Get-GalCommonBuildArguments 追加。
    #>
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][bool]$AllowStubUpdater,
        [Parameter(Mandatory)][bool]$DryRun,
        [Parameter(Mandatory)][bool]$SkipGithub
    )

    $arguments = @(Get-GalCommonBuildArguments -Version $Version)
    # 空公告就不拼 -Notes：别往参数数组里塞空元素（Mandatory 的 [string[]] 见到空串会在进函数体前就绑定失败）。
    if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments += @('-Notes', $ReleaseNotes) }
    if ($AllowStubUpdater) { $arguments += '-AllowStubUpdater' }
    if ($DryRun) { $arguments += '-DryRun' }
    if ($SkipGithub) { $arguments += '-SkipGithub' }
    return $arguments
}

function Invoke-GalToolboxScript {
    <#
        起一个**独立 pwsh 7** 转发，而不是 dot-source 到当前进程：
        被转发的脚本末尾会 `exit`，dot-source 会把本适配器一起结束掉；
        而且它们自己会打印 `::axtools` 协议行与退出码，隔一层进程最干净。
        必须优先 pwsh 7：`Scripts\Common\*.psm1` 是 UTF-8 **带 BOM**、而工具侧脚本按 pwsh 7 写，
        与 AxTools 自己的 PowerShellLocator 只认 pwsh 7 保持一致。
    #>
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        # ★ 实测（5.1 与 pwsh 7 行为一致）：[Parameter(Mandatory)][string[]] 只要数组里
        # **有一个空字符串元素**就抛「无法将自变量绑定到参数 'Arguments'，因为它是空字符串」。
        # 两个 Allow* 是必需的，不是可选装饰。
        [Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation
    )

    $powerShell = @(
        'C:\Program Files\PowerShell\7\pwsh.exe',
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        $found = Get-Command 'pwsh.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $found) { $powerShell = $found.Source }
    }
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        throw '没有找到 PowerShell 7 (pwsh.exe)。AxTools 的脚本按 UTF-8 保存，只有 PowerShell 7 能正确解析其中的中文；请安装 PowerShell 7 后重试。'
    }

    $argumentList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    # 直接继承 stdout/stderr：协议行必须原样落到宿主能读到的 stdout 上。
    & $powerShell @argumentList
    $exit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exit -ne 0) {
        throw "$Operation 失败，退出码 $exit。"
    }
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'GalExcleTools.csproj',
        'Scripts\Build-GalExcelUpdatePackage.ps1',
        'Scripts\GalExcelPublish.ps1',
        'Scripts\Publish-GalExcelRelease.ps1',
        'Scripts\Publish-GalExcelGiteeRelease.ps1',
        'Scripts\Publish-GalExcelGithubRelease.ps1',
        'Scripts\Set-GalExcelVersion.ps1')

    $toolboxScripts = Join-Path $root 'Scripts'

    switch ($Action) {
        'CheckEnvironment' {
            Write-AxTaskStage -Stage 'environment' -Message '正在检查剧情填表出包与发布环境...'
            $details = [Collections.Generic.List[string]]::new()

            $dotnet = Resolve-AxCommand -Name 'dotnet'
            $sdk = @(& $dotnet --list-sdks 2>&1 | Where-Object { $_ -match '^8\.' })
            if ($sdk.Count -eq 0) { throw '未检测到 .NET 8 SDK。' }
            $details.Add("dotnet=$dotnet（.NET 8 SDK 就绪）")

            $bandizip = @('C:\Program Files\Bandizip\bz.exe', 'C:\Program Files (x86)\Bandizip\bz.exe') |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            $details.Add($(if ([string]::IsNullOrWhiteSpace($bandizip)) {
                        'Bandizip 未找到 → 会退到 .NET ZipFile（压缩率略差，可能顶到 100 MiB 墙）'
                    }
                    else { "Bandizip=$bandizip" }))

            $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $curl) { throw '没有找到 curl.exe，Gitee / GitHub 附件上传无法进行。' }
            $details.Add("curl=$($curl.Source)")

            $giteeSource = Get-GalGiteeTokenSource
            $gitee = if ($null -eq $giteeSource) { '' } else { "$($giteeSource.Name)@$($giteeSource.Scope)" }
            $details.Add($(if ([string]::IsNullOrWhiteSpace($gitee)) {
                        '★ 没有找到 Gitee 令牌 → 出包能跑，真实发布会被拒绝'
                    }
                    else { "Gitee 令牌来源：$gitee（长度已隐去）" }))

            $github = ''
            foreach ($name in @('GALEXCEL_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN')) {
                foreach ($scope in @('Process', 'User', 'Machine')) {
                    $value = [Environment]::GetEnvironmentVariable($name, $scope)
                    if (![string]::IsNullOrWhiteSpace($value)) { $github = "$name@$scope"; break }
                }
                if (![string]::IsNullOrWhiteSpace($github)) { break }
            }
            if ([string]::IsNullOrWhiteSpace($github)) {
                $gh = Get-Command 'gh.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -ne $gh) {
                    $token = (& $gh.Source auth token 2>$null)
                    if (![string]::IsNullOrWhiteSpace($token)) { $github = 'gh auth token' }
                }
            }
            $details.Add($(if ([string]::IsNullOrWhiteSpace($github)) {
                        '★ 没有找到 GitHub 令牌 → 只能加 -SkipGithub 发 Gitee'
                    }
                    else { "GitHub 令牌来源：$github（长度已隐去）" }))

            $updater = Join-Path $root 'Scripts\热更新覆盖.ps1'
            if (!(Test-Path -LiteralPath $updater -PathType Leaf)) {
                $details.Add('★ 缺少 Scripts\热更新覆盖.ps1 → 出包会被拒绝（客户端交付物）')
            }
            else {
                $stream = [IO.File]::OpenRead($updater)
                try {
                    $head = [byte[]]::new(3)
                    [void]$stream.Read($head, 0, 3)
                    $hasBom = $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF
                }
                finally { $stream.Dispose() }
                $isStub = [IO.File]::ReadAllText($updater, [Text.Encoding]::UTF8).Contains('AXTOOLS-STUB')
                $details.Add("热更新覆盖.ps1：BOM=$hasBom；占位=$isStub")
            }

            Write-AxTaskProgress -Stage 'environment' -Percent 100 `
                -Message '剧情填表出包与发布环境检查完成。' `
                -Detail ($details -join [Environment]::NewLine)
        }

        'SetVersion' {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要写入的版本号。' }
            Invoke-GalToolboxScript `
                -ScriptPath (Get-GalToolboxScriptPath -Name 'Set-GalExcelVersion.ps1') `
                -Arguments @('-Version', $Version, '-ProjectRoot', $root) `
                -Operation '写入版本号'
            Write-AxTaskProgress -Stage 'version' -Percent 100 -Message "版本号已写入项目：$Version"
        }

        'BuildUpdatePackage' {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要出包的版本号。' }
            $staging = Get-GalStagingDirectory -Version $Version
            $arguments = @(Get-GalCommonBuildArguments -Version $Version)
            if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments += @('-Notes', $ReleaseNotes) }
            if ($AllowStubUpdater) { $arguments += '-AllowStubUpdater' }
            Invoke-GalToolboxScript `
                -ScriptPath (Get-GalToolboxScriptPath -Name 'Build-GalExcelUpdatePackage.ps1') `
                -Arguments $arguments `
                -Operation '出更新包'
            Write-AxTaskProgress -Stage 'package' -Percent 100 -Message "更新包已出到：$staging"
        }

        { $_ -in @('PublishDryRun', 'Publish') } {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要发布的版本号。' }
            if ($AllowStubUpdater -and $Action -ne 'PublishDryRun' -and !$DryRun) {
                throw '占位更新器只允许在演练里放行；真实发布必须先让客户端交付 Scripts\热更新覆盖.ps1。'
            }
            # 只有真的要写远端时才碰令牌环境：演练连这个变量都不设。
            if ($Action -eq 'Publish' -and !$DryRun) {
                $token = Enable-GalGiteeTokenForChild
                if ($null -eq $token) {
                    throw ('缺少 Gitee 令牌：请设置 GALEXCEL_GITEE_TOKEN（或 AXTOOLS_GITEE_TOKEN / GITEE_TOKEN），' +
                        '或在 AxTools 发布中心保存一次 Gitee 令牌。')
                }
                if ($token.Name -ne 'GALEXCEL_GITEE_TOKEN') {
                    Write-Host "Gitee 令牌：复用 $($token.Name)（长度已隐去）" -ForegroundColor DarkGray
                }
            }
            $skipGithub = Test-GalEnvFlag -Name 'AXTOOLS_GALEXCEL_SKIP_GITHUB'
            if ($skipGithub) {
                Write-Host 'AXTOOLS_GALEXCEL_SKIP_GITHUB 已生效：本次只发 Gitee，跳过 GitHub 通道。' -ForegroundColor Yellow
            }
            $arguments = Get-GalPublishArguments `
                -Version $Version `
                -AllowStubUpdater:$AllowStubUpdater `
                -DryRun:($Action -eq 'PublishDryRun' -or $DryRun) `
                -SkipGithub:$skipGithub

            Invoke-GalToolboxScript `
                -ScriptPath (Get-GalToolboxScriptPath -Name 'Publish-GalExcelRelease.ps1') `
                -Arguments $arguments `
                -Operation $(if ($Action -eq 'PublishDryRun') { '发布演练' } else { '完整发布' })
            Write-AxTaskProgress -Stage 'completed' -Percent 100 `
                -Message $(if ($Action -eq 'PublishDryRun') { '发布演练完成（未接触 Gitee / GitHub）。' }
                    elseif ($skipGithub) { 'Gitee 发布完成（已按要求跳过 GitHub 通道）。' }
                    else { '完整发布完成（Gitee + GitHub）。' })
        }
    }

    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "剧情填表 $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.ToString()
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
