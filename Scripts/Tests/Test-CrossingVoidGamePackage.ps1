# 零境游戏包发布内核（文件级清单 + git 增量 → 野草云下载站）的契约与端到端测试。
#
# 端到端部分全部跑在临时目录里：临时打包目录 + 临时裸仓库 + 临时暂存仓库，
# 不碰真实下载站、不联网（-SkipVerify）。

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-AxEvents {
    param([string[]]$Output)
    return @($Output |
        Where-Object { $_.StartsWith('::axtools ', [StringComparison]::Ordinal) } |
        ForEach-Object { $_.Substring('::axtools '.Length) | ConvertFrom-Json })
}

function Invoke-ReleaseKernel {
    param([string[]]$ExtraArguments)

    $arguments = @('-NoProfile', '-File', $kernelScript) + $ExtraArguments
    $output = @(& (Get-Command pwsh.exe -ErrorAction Stop).Source @arguments 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$kernelScript = Join-Path $scriptsRoot 'Publishing\Games\Publish-GameDownloadRelease.ps1'
$legacyScript = Join-Path $scriptsRoot 'Publishing\CrossingVoid\Publish-CrossingVoidGame.ps1'
$sharedAdapterScript = Join-Path $scriptsRoot 'Adapters\GameUpload\Invoke-GameUploadAction.ps1'

Assert-True (Test-Path -LiteralPath $kernelScript -PathType Leaf) "缺少游戏包发布内核：$kernelScript"
Assert-True (-not (Test-Path -LiteralPath $legacyScript)) `
    "旧的 OSS/分片内核已经退役，不该再留在仓库里：$legacyScript"
Assert-True (Test-Path -LiteralPath $sharedAdapterScript -PathType Leaf) "缺少共用游戏发布适配入口：$sharedAdapterScript"

$kernelSource = Get-Content -LiteralPath $kernelScript -Raw -Encoding UTF8
foreach ($requiredText in @(
    "ValidateSet('Check', 'Publish', 'RebuildRepo')",
    'schemaVersion',
    'baseUrl',
    'sha256',
    'post-receive',
    "'push', '--force', '--progress'",
    "'commit-tree'",
    "'update-ref'",
    "'push', '--force', '--progress', 'origin', 'HEAD:main'",
    "'diff', '--cached', '--quiet', 'HEAD'",
    'games/$ProductSegment',
    'CrossingVoid.version.json',
    'Manifest_DebugFiles_Win64.txt',
    '$SkipVerify')) {
    if (-not $kernelSource.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "游戏包发布内核缺少契约文本：$requiredText"
    }
}
Assert-True ($kernelSource -match "main\\\.\\d\+\\\..\+\\\.obb") '内核没有把 OBB 纳入安卓发布范围。'
Assert-True ($kernelSource -match "AFS_") '内核没有排除安卓的 AFS 调试 APK。'
Assert-True ($kernelSource -match 'pdb') '内核没有排除 pdb。'

$adapterSource = Get-Content -LiteralPath $sharedAdapterScript -Raw -Encoding UTF8
foreach ($requiredText in @(
    'CheckGamePackage',
    'PublishGameVersion',
    'RebuildGameDownloadRepo',
    'Publishing\Games\Publish-GameDownloadRelease.ps1',
    "'Check'",
    "'Publish'",
    "'RebuildRepo'",
    'Write-AxTaskResult',
    'ProductSegment',
    'VersionMarkerName')) {
    if (-not $adapterSource.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "共用游戏发布适配入口缺少契约文本：$requiredText"
    }
}

# 三个游戏各自的包装脚本：只带自己的产品段名和版本标记文件名，实现都在 GameUpload 里。
foreach ($game in @(
    @{ Name = 'CrossingVoidGame'; Segment = 'crossingvoid';  Marker = 'CrossingVoid.version.json' },
    @{ Name = 'FantasyGame';      Segment = 'fantasyproject'; Marker = 'FantasyProject.version.json' },
    @{ Name = 'NarutoGame';       Segment = 'narutobp';       Marker = 'NarutoBP.version.json' })) {
    $wrapper = Join-Path $scriptsRoot "Adapters\$($game.Name)\Invoke-$($game.Name)Action.ps1"
    Assert-True (Test-Path -LiteralPath $wrapper -PathType Leaf) "缺少游戏适配入口：$wrapper"
    $source = Get-Content -LiteralPath $wrapper -Raw -Encoding UTF8
    foreach ($requiredText in @(
        'GameUpload\Invoke-GameUploadAction.ps1',
        'CheckGamePackage',
        'PublishGameVersion',
        'RebuildGameDownloadRepo',
        $game.Segment,
        $game.Marker)) {
        if (-not $source.Contains($requiredText, [StringComparison]::Ordinal)) {
            throw "$($game.Name) 包装脚本缺少契约文本：$requiredText"
        }
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-GameRelease-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $testRoot 'Package'
$stagingRoot = Join-Path $testRoot 'Staging'
$remoteRoot = Join-Path $testRoot 'Remote'

function New-TestFile {
    param([string]$Root, [string]$Relative, [string]$Content)
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Set-Content -LiteralPath $path -Value $Content -NoNewline -Encoding UTF8
}

try {
    New-Item -ItemType Directory -Force -Path $packageRoot, $remoteRoot | Out-Null
    New-TestFile -Root $packageRoot -Relative 'CrossingVoid.exe' -Content ('A' * 512)
    New-TestFile -Root $packageRoot -Relative 'CrossingVoid\Content\Paks\pakchunk0.pak' -Content ('B' * 2048)
    New-TestFile -Root $packageRoot -Relative 'Manifest_UFSFiles_Win64.txt' -Content 'ufs'
    New-TestFile -Root $packageRoot -Relative 'Manifest_DebugFiles_Win64.txt' -Content 'debug-files'
    New-TestFile -Root $packageRoot -Relative 'CrossingVoid\Binaries\Win64\CrossingVoid.pdb' -Content 'symbols'
    New-TestFile -Root $packageRoot -Relative 'Saved\Logs\latest.log' -Content 'log'
    New-TestFile -Root $packageRoot -Relative 'Saved\LogsArchive\keep.log' -Content 'keep'

    $remoteRepo = Join-Path $remoteRoot 'game-axtest.git'
    & git init --bare -b main $remoteRepo | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) '无法创建临时裸仓库。'

    # 先给暂存仓库造出"带历史"的现场（P2 时真实仓库就是这样：三个提交）。
    # 内核必须把它压平回单提交，否则旧版本的 blob 在服务端一直可达、gc 收不掉。
    $localRepoRoot = Join-Path $stagingRoot 'axtest'
    & git init -b main $localRepoRoot | Out-Null
    foreach ($historyVersion in @('0.9.0', '0.9.0b')) {
        New-TestFile -Root $localRepoRoot -Relative "games/axtest/$historyVersion/old.txt" -Content $historyVersion
        & git -C $localRepoRoot add -A | Out-Null
        & git -C $localRepoRoot -c user.name=Test -c user.email=test@local commit -m "旧历史 $historyVersion" | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) '无法预置暂存仓库历史。'
    }
    Assert-True (@(& git -C $localRepoRoot rev-list --count HEAD)[0] -eq '2') '预置的暂存仓库历史不是两条提交。'

    $common = @(
        '-Platform', 'Windows',
        '-GameDirectory', $packageRoot,
        '-StagingRoot', $stagingRoot,
        '-ProductSegment', 'axtest',
        '-UploadTarget', $remoteRepo,
        '-SkipVerify')

    $first = Invoke-ReleaseKernel -ExtraArguments (@('-Mode', 'Publish', '-ReleaseVersion', '0.9.1') + $common)
    Assert-True ($first.ExitCode -eq 0) "首次发布失败（退出码 $($first.ExitCode)）：$($first.Output -join [Environment]::NewLine)"

    $events = Get-AxEvents -Output $first.Output
    $progressEvents = @($events | Where-Object type -EQ 'progress')
    Assert-True ($progressEvents.Count -gt 0) '内核没有输出进度事件。'
    Assert-True (-not ($progressEvents | Where-Object { $_.PSObject.Properties.Name -contains 'data' })) `
        '进度字段错误地嵌套在 data 中。'
    Assert-True (-not ($progressEvents | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.stage) -or
        $null -eq $_.percent -or
        [string]::IsNullOrWhiteSpace([string]$_.message)
    })) '进度事件缺少顶层 stage、percent 或 message。'
    Assert-True (($progressEvents | Where-Object stage -EQ 'push').Count -gt 0) '推送阶段没有上报进度。'
    $pushPercents = @($progressEvents | Where-Object stage -EQ 'push' | ForEach-Object { [double]$_.percent })
    Assert-True (($pushPercents | Measure-Object -Minimum).Minimum -ge 0) '推送进度出现负值。'
    Assert-True (($pushPercents | Measure-Object -Maximum).Maximum -le 100) '推送进度超过 100%。'

    $manifestPath = Join-Path $stagingRoot 'axtest\games\axtest\manifests\0.9.1.json'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) "没有生成版本清单：$manifestPath"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 1) '清单没有使用文件级协议 1。'
    Assert-True ($manifest.productKey -eq 'axtest-game') '清单产品标识错误。'
    Assert-True ($manifest.runtime -eq 'Windows') '清单平台标识错误。'
    Assert-True ($manifest.version -eq '0.9.1') '清单版本号错误。'
    Assert-True ($manifest.baseUrl -eq 'https://dl.crossingvoid.top/games/axtest/0.9.1/') '清单 baseUrl 错误。'
    Assert-True (@($manifest.patches).Count -eq 0) '清单必须预留空的 patches。'

    # 发布一次之后：仓库被压平成单提交，更早的版本目录被清掉。
    Assert-True (@(& git -C $localRepoRoot rev-list --count HEAD)[0] -eq '1') `
        '暂存仓库没有压平成单提交（历史在累积，服务端对象会一直涨）。'
    $localTree = @(& git -C $localRepoRoot ls-tree -r --name-only HEAD)
    Assert-True (-not ($localTree | Where-Object { $_ -like 'games/axtest/0.9.0*' })) '更早的版本目录没有被清掉。'

    $manifestPaths = @($manifest.files | ForEach-Object { $_.path })
    Assert-True ($manifestPaths -contains 'CrossingVoid.exe') '清单缺少主程序。'
    Assert-True ($manifestPaths -contains 'CrossingVoid.version.json') '清单缺少版本标记文件。'
    Assert-True ($manifestPaths -contains 'Manifest_UFSFiles_Win64.txt') '清单错误排除了 UFS 清单。'
    Assert-True (-not ($manifestPaths -contains 'CrossingVoid/Binaries/Win64/CrossingVoid.pdb')) '清单不应包含 pdb。'
    Assert-True (-not ($manifestPaths -contains 'Saved/Logs/latest.log')) '清单不应包含 Saved\Logs。'
    Assert-True (-not ($manifestPaths -contains 'Manifest_DebugFiles_Win64.txt')) '清单不应包含调试文件清单。'
    Assert-True ($manifestPaths -contains 'Saved/LogsArchive/keep.log') 'Saved 下非日志目录不应被误删。'

    $versionRoot = Join-Path $stagingRoot 'axtest\games\axtest\0.9.1'
    foreach ($entry in $manifest.files) {
        $target = Join-Path $versionRoot ([string]$entry.path -replace '/', '\')
        Assert-True (Test-Path -LiteralPath $target -PathType Leaf) "清单里的文件没有落位：$($entry.path)"
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($actual -eq [string]$entry.sha256) "清单哈希与文件不一致：$($entry.path)"
        Assert-True ((Get-Item -LiteralPath $target).Length -eq [int64]$entry.sizeBytes) "清单字节数与文件不一致：$($entry.path)"
    }

    # 暂存目录里的文件必须是硬链接（和打包产物共用同一份数据），否则本地等于白占一个版本。
    $stagedExe = Join-Path $versionRoot 'CrossingVoid.exe'
    Assert-True ((Get-Item -Force -LiteralPath $stagedExe).LinkType -eq 'HardLink') `
        '暂存目录里的产物不是硬链接，本地会多占一份空间。'
    $linkNames = @(& fsutil hardlink list $stagedExe)
    Assert-True ($linkNames.Count -eq 2) "暂存文件的硬链接数不是 2：$($linkNames -join '; ')"
    Assert-True (($linkNames -join "`n") -match [regex]::Escape('Package\CrossingVoid.exe')) `
        '暂存文件没有和打包产物指向同一份数据。'

    $remoteTree = @(& git --git-dir=$remoteRepo ls-tree -r --name-only main)
    Assert-True ($LASTEXITCODE -eq 0) '无法读取临时裸仓库的文件树。'
    Assert-True ($remoteTree -contains 'games/axtest/0.9.1/CrossingVoid.exe') '裸仓库缺少版本文件。'
    Assert-True ($remoteTree -contains 'games/axtest/manifests/0.9.1.json') '裸仓库缺少清单文件。'
    Assert-True (@(& git --git-dir=$remoteRepo rev-list --count main)[0] -eq '1') `
        '暂存仓库没有保持单提交（amend 失效）。'

    # 幂等：同一份产物再发一次，应当连提交都不做。
    $second = Invoke-ReleaseKernel -ExtraArguments (@('-Mode', 'Publish', '-ReleaseVersion', '0.9.1') + $common)
    Assert-True ($second.ExitCode -eq 0) "重复发布失败：$($second.Output -join [Environment]::NewLine)"
    Assert-True (($second.Output -join [Environment]::NewLine) -match '跳过提交与推送') '重复发布没有走幂等分支。'
    Assert-True (@(& git --git-dir=$remoteRepo rev-list --count main)[0] -eq '1') '重复发布多出了提交。'

    # 只检查模式：不联网、不提交。
    $check = Invoke-ReleaseKernel -ExtraArguments (@('-Mode', 'Check', '-ReleaseVersion', '0.9.1') + $common)
    Assert-True ($check.ExitCode -eq 0) "检查模式失败：$($check.Output -join [Environment]::NewLine)"
    Assert-True (($check.Output -join [Environment]::NewLine) -match '未提交、未上传') '检查模式没有明确说明不上传。'

    # 新版本：树里只能留最新版，清单也只留当前版本。
    New-TestFile -Root $packageRoot -Relative 'CrossingVoid.exe' -Content ('A' * 768)
    $third = Invoke-ReleaseKernel -ExtraArguments (@('-Mode', 'Publish', '-ReleaseVersion', '0.9.2') + $common)
    Assert-True ($third.ExitCode -eq 0) "发布新版本失败：$($third.Output -join [Environment]::NewLine)"
    $remoteTree = @(& git --git-dir=$remoteRepo ls-tree -r --name-only main)
    Assert-True (-not ($remoteTree | Where-Object { $_ -like 'games/axtest/0.9.1/*' })) '旧版本目录仍留在树里。'
    Assert-True (-not ($remoteTree -contains 'games/axtest/manifests/0.9.1.json')) '旧版本清单仍留在树里。'
    Assert-True ($remoteTree -contains 'games/axtest/0.9.2/CrossingVoid.exe') '新版本没有进树。'
    Assert-True (@(& git --git-dir=$remoteRepo rev-list --count main)[0] -eq '1') '发新版本后提交数不再是 1。'
    # 发布成功后本机仓库要顺手回收：只留当前这一版的对象，旧版的不许留在本地。
    Assert-True (($third.Output -join [Environment]::NewLine) -match '本机仓库回收完成') `
        '发布成功后没有回收本机仓库的旧对象。'
    $leftovers = @(& git -C $localRepoRoot fsck --unreachable --no-progress 2>&1 |
        Where-Object { $_ -match 'unreachable|dangling' })
    Assert-True ($leftovers.Count -eq 0) "本机仓库里还留着上一版的对象：$($leftovers -join '; ')"

    # 安卓：发主 APK + OBB 里的条目（OBB 本体不发），外加一个旁车告诉启动器怎么组装回 OBB。
    $androidRoot = Join-Path $testRoot 'AndroidPackage'
    New-Item -ItemType Directory -Force -Path $androidRoot | Out-Null
    New-TestFile -Root $androidRoot -Relative 'CrossingVoid-Android-Shipping-arm64.apk' -Content ('P' * 1024)
    New-TestFile -Root $androidRoot -Relative 'AFS_CrossingVoid-Android-Shipping-arm64.apk' -Content 'afs'
    New-TestFile -Root $androidRoot -Relative 'Install_CrossingVoid.bat' -Content 'bat'
    New-TestFile -Root $androidRoot -Relative 'CrossingVoid_Symbols_v1\native.so' -Content 'symbols'
    New-TestFile -Root $androidRoot -Relative 'win-x64\UnrealAndroidFileTool.exe' -Content 'tool'

    # 造一个"真的 ZIP"当 OBB（UE 产出的 OBB 就是不压缩 ZIP，条目就是 chunk 容器）
    $obbPayload = Join-Path $testRoot 'obb-payload'
    New-TestFile -Root $obbPayload -Relative 'CrossingVoid\Content\Paks\pakchunk0-Android_ASTC.pak' -Content ('O' * 4096)
    New-TestFile -Root $obbPayload -Relative 'Engine\Config\StagedBuild_Test.ini' -Content 'engine-config'
    $obbPath = Join-Path $androidRoot 'main.1.com.TFAC.CorssingVoid.obb'
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $obbPayload,
        $obbPath,
        [IO.Compression.CompressionLevel]::NoCompression,
        $false)

    $androidRemote = Join-Path $remoteRoot 'game-axtest-android.git'
    & git init --bare -b main $androidRemote | Out-Null
    $android = Invoke-ReleaseKernel -ExtraArguments @(
        '-Mode', 'Publish',
        '-Platform', 'Android',
        '-GameDirectory', $androidRoot,
        '-ReleaseVersion', '0.9.2',
        '-StagingRoot', $stagingRoot,
        '-ProductSegment', 'axtest-android',
        '-UploadTarget', $androidRemote,
        '-SkipVerify')
    Assert-True ($android.ExitCode -eq 0) "安卓发布失败：$($android.Output -join [Environment]::NewLine)"
    $androidManifest = Get-Content -LiteralPath (Join-Path $stagingRoot 'axtest-android\games\axtest-android\manifests\0.9.2.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $androidPaths = @($androidManifest.files | ForEach-Object { $_.path })
    Assert-True ($androidManifest.productKey -eq 'axtest-android-game') '安卓清单产品标识错误。'
    Assert-True ($androidManifest.runtime -eq 'Android') '安卓清单平台标识错误。'
    Assert-True ($androidManifest.baseUrl -eq 'https://dl.crossingvoid.top/games/axtest-android/0.9.2/') '安卓清单 baseUrl 错误。'
    Assert-True ($androidPaths -contains 'CrossingVoid-Android-Shipping-arm64.apk') '安卓清单缺少主 APK。'
    Assert-True (-not ($androidPaths -contains 'main.1.com.TFAC.CorssingVoid.obb')) '安卓清单不该再直接发 OBB 本体。'
    Assert-True ($androidPaths -contains 'CrossingVoid/Content/Paks/pakchunk0-Android_ASTC.pak') '安卓清单缺少 OBB 里的 chunk 条目。'
    Assert-True ($androidPaths -contains 'Engine/Config/StagedBuild_Test.ini') '安卓清单缺少 OBB 里的其它条目。'
    Assert-True ($androidPaths -contains 'CrossingVoid.obb.json') '安卓清单缺少 OBB 旁车文件。'
    Assert-True (-not ($androidPaths -contains 'AFS_CrossingVoid-Android-Shipping-arm64.apk')) '安卓清单不应包含 AFS APK。'
    Assert-True (-not ($androidPaths -contains 'Install_CrossingVoid.bat')) '安卓清单不应包含安装 bat。'
    Assert-True (-not ($androidPaths | Where-Object { $_ -like 'CrossingVoid_Symbols_v1/*' })) '安卓清单不应包含符号文件。'

    # 旁车内容：OBB 名字 + 组成它的条目
    $sidecar = Get-Content -LiteralPath (Join-Path $stagingRoot 'axtest-android\games\axtest-android\0.9.2\CrossingVoid.obb.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($sidecar.obbFileName -eq 'main.1.com.TFAC.CorssingVoid.obb') "旁车里的 OBB 名字不对：$($sidecar.obbFileName)"
    Assert-True (@($sidecar.entries).Count -eq 2) "旁车里的条目数不对：$(@($sidecar.entries).Count)"
    Assert-True ($sidecar.entries -contains 'CrossingVoid/Content/Paks/pakchunk0-Android_ASTC.pak') '旁车里缺 chunk 条目。'

    # 重整仓库：压成一条没有父提交的提交（也就是全量重传）。
    $rebuild = Invoke-ReleaseKernel -ExtraArguments (@('-Mode', 'RebuildRepo', '-ReleaseVersion', '0.9.2') + $common)
    Assert-True ($rebuild.ExitCode -eq 0) "重整仓库失败：$($rebuild.Output -join [Environment]::NewLine)"
    $parents = (@(& git --git-dir=$remoteRepo rev-list --parents --max-count=1 main)[0] -split '\s+')
    Assert-True ($parents.Count -eq 1) '重整仓库后仍然存在父提交。'
    Assert-True (Test-Path -LiteralPath (Join-Path $stagingRoot 'axtest\.git') -PathType Container) '重整仓库后暂存仓库不完整。'
    # 旧 .git 的备份不能落在仓库目录里，否则会被 git add -A 一起提交。
    $rebuiltTree = @(& git --git-dir=$remoteRepo ls-tree -r --name-only main)
    Assert-True (-not ($rebuiltTree | Where-Object { $_ -like '*.git-backup*' -or $_ -like '*.git.rebuild*' })) `
        '重整仓库把旧 .git 的备份一起提交了。'
    Assert-True (-not (Get-ChildItem -LiteralPath (Join-Path $stagingRoot 'axtest') -Force -Directory |
            Where-Object { $_.Name -like '.git-backup*' -or $_.Name -like '.git.rebuild*' })) `
        '重整仓库的备份目录留在了仓库目录里。'

    Write-Output 'CrossingVoid game release kernel tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
