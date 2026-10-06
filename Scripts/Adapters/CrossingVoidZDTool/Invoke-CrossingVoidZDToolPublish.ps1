<#
.SYNOPSIS
    AxTools「工具-零境素材」的动作入口：把动作转给 ZD 工程里的出包/发布脚本。

.DESCRIPTION
    为什么这层只是转发：
      ZD 工具箱是一个**独立工程**（有自己的 git 仓库、README 与 `Scripts\`），
      出包与发版的真相源必须留在那个仓库里 —— 和两个兄弟项目的做法一致
      （PC：`Invoke-CrossingVoidPcAction.ps1` → `Scripts\Publish-LauncherGiteePackage.ps1`；
       安卓：`Invoke-CrossingVoidAndroidAction.ps1` → `Scripts\Publish-AndroidLauncher.ps1`）。
      这样脱离 AxTools、在 ZD 目录里手敲一条命令也能完成同样的发布。

    被转发的脚本（都在 ZD 工程的 `Scripts\` 下）：
      Build-ZDToolboxUpdatePackage.ps1    出包：清单 + zip + sha256 + 体积墙（超限自动分卷）
      Publish-ZDToolboxRelease.ps1        ★「完整发布」编排层：同一批产物发到 Gitee + GitHub
      Publish-ZDToolboxGiteeRelease.ps1   单通道：Gitee（幂等 + 清单最后生效 + 清理旧附件）
      Publish-ZDToolboxGithubRelease.ps1  单通道：GitHub（幂等靠 digest + 从不删除旧版本）
      Set-ZDToolboxVersion.ps1            版本号写入 csproj（打磨动作）

    两条通道的差异是**有意**的，不是没对齐：
      · Gitee 每仓库附件合计 1 GB，所以发新版本会清理旧 Release 的附件（只留最近 3 个）；
      · GitHub 没有这条约束，只上传、从不删除，旧版本永久保留。
      两边传的是同一批附件（分卷时就是同一组卷 + 同一份 sha256.txt + 同一份清单）。
      只想发一条时：`AXTOOLS_ZD_SKIP_GITHUB=1`（只发 Gitee），或直接调编排层的 -SkipGitee。

    `::axtools` 协议行由被调用的脚本直接写 stdout，本层**不再重复输出**，
    只搬运退出码。
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
    # AXTOOLS_ZD_ALLOW_STUB_UPDATER 时才追加这个开关。
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

function Get-ZdToolboxScriptPath {
    param([Parameter(Mandatory)][string]$Name)

    $path = Join-Path $toolboxScripts $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "ZD 工程里找不到脚本：$path。请确认「工具-零境素材」的源码目录是 ZD 工具箱工程，且已拉取最新提交。"
    }
    return $path
}

function Get-ZdStagingDirectory {
    <#
        出包产物落点：跟 AxTools 给这个工具配的 OutputRoot 对齐（和其它工具一个规矩），
        没有就退回 ZD 工程的 artifacts\release\v{version}（.gitignore 已忽略 artifacts/）。
        必须和 ZD 侧发布脚本的默认推导保持一致，否则会各出一套包。
    #>
    param([Parameter(Mandatory)][string]$Version)

    if (![string]::IsNullOrWhiteSpace($OutputRoot)) {
        $base = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([char[]]'\/')
        return Join-Path $base "v$Version"
    }
    return Join-Path $root (Join-Path 'artifacts\release' "v$Version")
}

function Get-ZdCommonBuildArguments {
    <#
        ★ 体积墙：ZD 当前源码打不出 ≤100 MiB 的单附件（实测链：344 MB 解压 → 137.52 MB(zip)
        → 去 node_modules/.pdb 115.30 MB → 去 Atlas 内置 Python 98.24 MB → 真实可发集 115.91 MB），
        唯一把最终包压进限制内的是去掉 **WindowsAppSDK 传递进来的 AI 推理运行时**
        （onnxruntime.dll + Microsoft.ML.OnnxRuntime.dll + DirectML.dll，解压约 39 MB）
        → 最终 99.40 MB（余量 0.60 MB）。

        这个剔除有实测支撑，不是猜的：
          · 全工程 grep 搜不到任何 OnnxRuntime/DirectML 用法；
          · 三个 dll 挪走 / 放回去，主程序都正常存活 12 秒；
          · 主程序启动后模块列表里没有加载 onnxruntime / DirectML；
          · 对「剔除了它们的那个包」跑本体自带的 UI 冒烟 --ui-smoke：41 PASS / 0 FAIL。
        但这是"当前功能用不到"，不是"永远用不到" —— 将来真引入 ONNX/DirectML 推理时必须关掉：
        把环境变量 AXTOOLS_ZD_EXCLUDE_AI_RUNTIME 设成 0。
    #>
    param([Parameter(Mandatory)][string]$Version)

    $arguments = @(
        '-Version', $Version,
        '-ProjectRoot', $root,
        '-StagingDirectory', (Get-ZdStagingDirectory -Version $Version)
    )

    $exclude = [Environment]::GetEnvironmentVariable('AXTOOLS_ZD_EXCLUDE_AI_RUNTIME', 'Process')
    if ([string]::IsNullOrWhiteSpace($exclude)) {
        $exclude = [Environment]::GetEnvironmentVariable('AXTOOLS_ZD_EXCLUDE_AI_RUNTIME', 'User')
    }
    if ($exclude -notin @('0', 'false', 'False', 'no', 'NO')) {
        $arguments += '-ExcludeAiRuntime'
    }
    else {
        Write-Host 'AXTOOLS_ZD_EXCLUDE_AI_RUNTIME=0：本次不剔除 AI 推理运行时（包会超 100 MiB 被拒绝）。' -ForegroundColor Yellow
    }

    # 增量构建（默认开）：复用上一次的 obj/bin 编译缓存，只重编改动过的部分。
    # 实测同一工作目录连续 publish：全量 60.6s → 源码未动 11.3s → 只改 1 个 .cs 17.6s。
    # publish 输出目录每次清空、源码用 robocopy /MIR 同步，所以**包内容与全量构建一致**；
    # 这是编译缓存，不是"复用旧包"（后者由出包脚本的源码指纹负责）。
    # 排除故障或想要绝对干净的一次构建时，把 AXTOOLS_ZD_INCREMENTAL_BUILD 设成 0。
    $incremental = [Environment]::GetEnvironmentVariable('AXTOOLS_ZD_INCREMENTAL_BUILD', 'Process')
    if ([string]::IsNullOrWhiteSpace($incremental)) {
        $incremental = [Environment]::GetEnvironmentVariable('AXTOOLS_ZD_INCREMENTAL_BUILD', 'User')
    }
    if ($incremental -in @('0', 'false', 'False', 'no', 'NO')) {
        Write-Host 'AXTOOLS_ZD_INCREMENTAL_BUILD=0：本次走全量全新构建（不复用编译缓存）。' -ForegroundColor Yellow
    }
    else {
        $arguments += '-IncrementalBuild'
    }
    return $arguments
}

function Test-ZdEnvFlag {
    <#
        读一个布尔型环境开关（Process 优先于 User）。与 AXTOOLS_ZD_EXCLUDE_AI_RUNTIME /
        AXTOOLS_ZD_INCREMENTAL_BUILD 同一套约定：没设 = 关闭；设成 1/true/yes = 打开。
    #>
    param([Parameter(Mandatory)][string]$Name)

    foreach ($scope in @('Process', 'User')) {
        $value = [Environment]::GetEnvironmentVariable($Name, $scope)
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim() -in @('1', 'true', 'True', 'TRUE', 'yes', 'YES')
        }
    }
    return $false
}

function Get-ZdPublishArguments {
    <#
        发布链的公共参数。注意**参数是分动作、也分脚本的**：
          · 现在转发的是编排层 `Publish-ZDToolboxRelease.ps1`，它自己会按通道过滤参数
            （GitHub 子脚本不认 `-KeepReleasesWithAssets` / `-SkipPrune` / 出包相关开关）；
          · `-ExcludeAiRuntime` 与 `-IncrementalBuild` 由 Get-ZdCommonBuildArguments 追加，
            编排层收下后只传给 Gitee 段（那一段才会出包）。
        早先两个动作共用一份参数，结果 `PublishDryRun` 把 `-ExcludeAiRuntime` 传给了不认它的脚本，
        直接 "找不到与参数名称 'ExcludeAiRuntime' 匹配的参数"（实测踩到）。
    #>
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][bool]$AllowStubUpdater,
        [Parameter(Mandatory)][bool]$DryRun,
        [Parameter(Mandatory)][bool]$SkipGithub
    )

    $arguments = @(Get-ZdCommonBuildArguments -Version $Version)
    # 空公告就不拼 -Notes：别往参数数组里塞空元素（见 Invoke-ZdToolboxScript 的 AllowEmptyString 注释）
    if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments += @('-Notes', $ReleaseNotes) }
    if ($AllowStubUpdater) { $arguments += '-AllowStubUpdater' }
    if ($DryRun) { $arguments += '-DryRun' }
    if ($SkipGithub) { $arguments += '-SkipGithub' }
    return $arguments
}

function Invoke-ZdToolboxScript {
    <#
        起一个**独立 powershell.exe** 转发，而不是 dot-source 到当前进程：
        被转发的脚本末尾会 `exit`，dot-source 会把本适配器一起结束掉；
        而且它们自己会打印 `::axtools` 协议行与退出码，隔一层进程最干净。
        执行策略用 Bypass：本机 CurrentUser/LocalMachine 都是 Undefined（实测），
        不加会被「禁止运行脚本」挡住。
    #>
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        # ★ 实测（5.1 与 pwsh 7 行为一致）：[Parameter(Mandatory)][string[]] 只要数组里
        # **有一个空字符串元素**就抛
        #   `Cannot bind argument to parameter 'Arguments' because it is an empty string.` /
        #   `无法将自变量绑定到参数 'Arguments'，因为它是空字符串。`
        # 而 `-ReleaseNotes` 为空时 `@('-Notes', $ReleaseNotes)` 正好塞进一个空元素 ——
        # 于是"没填更新公告"就会让整个发布动作在**进入函数体之前**死掉（UI 上表现为参数绑定异常）。
        # 两个 Allow* 是必需的，不是可选装饰。
        [Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation
    )


    # ★ 必须优先 pwsh 7：`Scripts\Common\*.psm1`（本脚本要 Import-Module 的两个）是
    #   **UTF-8 无 BOM**，而 Windows PowerShell 5.1 会按 ANSI 解码 → 中文串里的引号错位
    #   → 模块在解析期就报 "字符串缺少终止符"，整个适配器连第一步都跑不到。
    #   本机实测：同一模块 pwsh 7.6.6 → 可导入；powershell.exe 5.1 → 解析失败。
    #   AxTools 自己的 PowerShellLocator 也只认 pwsh 7，这里保持一致。
    $powerShell = @(
        'C:\Program Files\PowerShell\7\pwsh.exe',
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        $found = Get-Command 'pwsh.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $found) { $powerShell = $found.Source }
    }
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        throw '没有找到 PowerShell 7 (pwsh.exe)。AxTools 的脚本按 UTF-8 无 BOM 保存，只有 PowerShell 7 能正确解析其中的中文；请安装 PowerShell 7 后重试。'
    }


    $argumentList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    # 直接继承 stdout/stderr：协议行必须原样落到宿主能读到的 stdout 上，
    # 这里绝不能把输出收进变量再转发（会把实时进度憋到最后一次性吐出来）。
    & $powerShell @argumentList
    $exit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exit -ne 0) {
        throw "$Operation 失败，退出码 $exit。"
    }
}

try {
    $root = Assert-AxProjectRoot -ProjectRoot $ProjectRoot -RequiredPaths @(
        'CrossingVoidZDTool.csproj',
        'Pakout.ps1',
        'Scripts\Build-ZDToolboxUpdatePackage.ps1',
        'Scripts\ZDToolboxPublish.ps1',
        'Scripts\Publish-ZDToolboxRelease.ps1',
        'Scripts\Publish-ZDToolboxGiteeRelease.ps1',
        'Scripts\Publish-ZDToolboxGithubRelease.ps1',
        'Scripts\Set-ZDToolboxVersion.ps1')

    $toolboxScripts = Join-Path $root 'Scripts'

    switch ($Action) {
        'CheckEnvironment' {
            Write-AxTaskStage -Stage 'environment' -Message '正在检查 ZD 工具箱出包与发布环境...'
            $details = [Collections.Generic.List[string]]::new()

            $dotnet = Resolve-AxCommand -Name 'dotnet'
            $sdk = @(& $dotnet --list-sdks 2>&1 | Where-Object { $_ -match '^8\.' })
            if ($sdk.Count -eq 0) { throw '未检测到 .NET 8 SDK。' }
            $details.Add("dotnet=$dotnet（.NET 8 SDK 就绪）")

            $bandizip = @('C:\Program Files\Bandizip\bz.exe', 'C:\Program Files (x86)\Bandizip\bz.exe') |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if ([string]::IsNullOrWhiteSpace($bandizip)) {
                $bandizip = (Get-Command 'bz.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).Source
            }
            $details.Add($(if ([string]::IsNullOrWhiteSpace($bandizip)) {
                        'Bandizip 未找到 → 会退到 .NET ZipFile（压缩率略差，可能顶到 100 MiB 墙）'
                    }
                    else { "Bandizip=$bandizip" }))

            $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $curl) { throw '没有找到 curl.exe，Gitee 附件上传无法进行。' }
            $details.Add("curl=$($curl.Source)")

            $token = ''
            foreach ($name in @('CROSSINGVOID_GITEE_TOKEN', 'GITEE_TOKEN', 'GITEE_ACCESS_TOKEN')) {
                foreach ($scope in @('Process', 'User', 'Machine')) {
                    $value = [Environment]::GetEnvironmentVariable($name, $scope)
                    if (![string]::IsNullOrWhiteSpace($value)) { $token = "$name@$scope"; break }
                }
                if (![string]::IsNullOrWhiteSpace($token)) { break }
            }
            $details.Add($(if ([string]::IsNullOrWhiteSpace($token)) {
                        '★ 没有找到 Gitee 令牌 → 出包能跑，真实发布会被拒绝'
                    }
                    else { "Gitee 令牌来源：$token（长度已隐去）" }))

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
                -Message 'ZD 工具箱出包与发布环境检查完成。' `
                -Detail ($details -join [Environment]::NewLine)
        }

        'SetVersion' {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要写入的版本号。' }
            Invoke-ZdToolboxScript `
                -ScriptPath (Get-ZdToolboxScriptPath -Name 'Set-ZDToolboxVersion.ps1') `
                -Arguments @('-Version', $Version, '-ProjectRoot', $root) `
                -Operation '写入版本号'
            Write-AxTaskProgress -Stage 'version' -Percent 100 -Message "版本号已写入项目：$Version"
        }

        'BuildUpdatePackage' {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要出包的版本号。' }
            $staging = Get-ZdStagingDirectory -Version $Version
            $arguments = @(Get-ZdCommonBuildArguments -Version $Version)
    # 空公告就不拼 -Notes：别往参数数组里塞空元素（见 Invoke-ZdToolboxScript 的 AllowEmptyString 注释）
    if (![string]::IsNullOrWhiteSpace($ReleaseNotes)) { $arguments += @('-Notes', $ReleaseNotes) }
            if ($AllowStubUpdater) { $arguments += '-AllowStubUpdater' }
            Invoke-ZdToolboxScript `
                -ScriptPath (Get-ZdToolboxScriptPath -Name 'Build-ZDToolboxUpdatePackage.ps1') `
                -Arguments $arguments `
                -Operation '出更新包'
            Write-AxTaskProgress -Stage 'package' -Percent 100 -Message "更新包已出到：$staging"
        }

        { $_ -in @('PublishDryRun', 'Publish') } {
            if ([string]::IsNullOrWhiteSpace($Version)) { throw '请先填写要发布的版本号。' }
            if ($AllowStubUpdater -and $Action -ne 'PublishDryRun' -and !$DryRun) {
                throw '占位更新器只允许在演练里放行；真实发布必须先让客户端交付 Scripts\热更新覆盖.ps1。'
            }
            # 参数按动作给：编排层收下后自行按通道过滤（GitHub 子脚本不认出包/清理类开关）。
            $skipGithub = Test-ZdEnvFlag -Name 'AXTOOLS_ZD_SKIP_GITHUB'
            if ($skipGithub) {
                Write-Host 'AXTOOLS_ZD_SKIP_GITHUB 已生效：本次只发 Gitee，跳过 GitHub 通道。' -ForegroundColor Yellow
            }
            $arguments = Get-ZdPublishArguments `
                -Version $Version `
                -AllowStubUpdater:$AllowStubUpdater `
                -DryRun:($Action -eq 'PublishDryRun' -or $DryRun) `
                -SkipGithub:$skipGithub

            Invoke-ZdToolboxScript `
                -ScriptPath (Get-ZdToolboxScriptPath -Name 'Publish-ZDToolboxRelease.ps1') `
                -Arguments $arguments `
                -Operation $(if ($Action -eq 'PublishDryRun') { '发布演练' } else { '完整发布' })
            Write-AxTaskProgress -Stage 'completed' -Percent 100 `
                -Message $(if ($Action -eq 'PublishDryRun') { '发布演练完成（未接触 Gitee / GitHub）。' }
                    elseif ($skipGithub) { 'Gitee 发布完成（已按要求跳过 GitHub 通道）。' }
                    else { '完整发布完成（Gitee + GitHub）。' })
        }
    }

    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "ZD 工具箱 $Action 完成。"
    exit 0
}
catch {
    Write-Output $_.Exception.ToString()
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}