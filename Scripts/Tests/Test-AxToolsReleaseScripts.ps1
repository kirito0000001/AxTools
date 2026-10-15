[CmdletBinding()]
param()

# AxTools 发布链契约测试：脚本面 + 本地全链路（出包 / 校验 / 增量 / 分卷 / 演练 / 复用）。
# 与 ZD 工具箱那套是同一份规则，所以这里断言的是**行为**，不是实现细节。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$releaseRoot = Join-Path $repoRoot 'Scripts\Release'
$scripts = [ordered]@{
    Build       = Join-Path $releaseRoot 'Build-AxToolsUpdatePackage.ps1'
    Publish     = Join-Path $releaseRoot 'Publish-AxToolsRelease.ps1'
    Gitee       = Join-Path $releaseRoot 'Publish-AxToolsGiteeRelease.ps1'
    Github      = Join-Path $releaseRoot 'Publish-AxToolsGithubRelease.ps1'
    Common      = Join-Path $releaseRoot 'AxToolsPublish.ps1'
    Tokens      = Join-Path $releaseRoot 'AxToolsReleaseCommon.psm1'
    Validate    = Join-Path $releaseRoot 'Test-AxToolsPackage.ps1'
    SetVersion  = Join-Path $releaseRoot 'Set-AxToolsVersion.ps1'
    Updater     = Join-Path $releaseRoot 'Update-AxTools.ps1'
}
foreach ($entry in $scripts.GetEnumerator()) {
    if (!(Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "缺少发布链脚本（$($entry.Key)）：$($entry.Value)"
    }
}

# ① 脚本面：参数与关键实现必须在（这些是"和 ZD 一致"的可检查部分）。
$publishSource = Get-Content -LiteralPath $scripts.Publish -Raw -Encoding UTF8
foreach ($token in @('-SkipGitee', '-SkipGithub', '-KeepReleasesWithAssets', '-TolerateSelfCheckFailure', '-SkipBuild')) {
    if (!$publishSource.Contains($token, [StringComparison]::Ordinal)) { throw "编排脚本缺少参数：$token" }
}
$commonSource = Get-Content -LiteralPath $scripts.Common -Raw -Encoding UTF8
foreach ($token in @('Rate Limit Exceeded', '--fail-with-body', '--retry 10', 'Assert-AxReleaseAssets', 'Invoke-AxSelfCheck', 'Prune-AxOldReleaseAssets')) {
    if (!$commonSource.Contains($token, [StringComparison]::Ordinal)) { throw "发布实现缺少关键处理：$token" }
}
$buildSource = Get-Content -LiteralPath $scripts.Build -Raw -Encoding UTF8
foreach ($token in @('$AttachmentLimitBytes = 100MB', '$DefaultPartSizeBytes = 90MB', '-DeltaBaseVersion', '-NoDelta', 'file-index.json', 'Bandizip')) {
    if (!$buildSource.Contains($token, [StringComparison]::Ordinal)) { throw "出包脚本缺少关键处理：$token" }
}
# 旧的 Package-AxTools.ps1 已并入 Build-AxToolsUpdatePackage.ps1，仓库里不该再有人引用它（本文件自己除外）。
$staleReferences = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Include *.ps1, *.psm1, *.cs -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|Artifacts)\\' } |
    Where-Object { $_.FullName -ne $PSCommandPath } |
    Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8).Contains('Package-AxTools.ps1', [StringComparison]::Ordinal) })
if ($staleReferences.Count -gt 0) {
    throw ("仍有文件引用已退役的 Package-AxTools.ps1：" + (($staleReferences | ForEach-Object { $_.FullName }) -join '、'))
}

# ② 令牌模块契约：Gitee 复用幻杀工具箱的令牌；GitHub 依次看环境变量与 gh。
$originalTokens = @{}
foreach ($name in @('AXTOOLS_GITEE_TOKEN', 'FANTASYTOOLS_GITEE_TOKEN', 'AXTOOLS_GITHUB_TOKEN', 'GITHUB_TOKEN')) {
    $originalTokens[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
try {
    [Environment]::SetEnvironmentVariable('FANTASYTOOLS_GITEE_TOKEN', 'test-fantasy-token', 'Process')
    [Environment]::SetEnvironmentVariable('AXTOOLS_GITHUB_TOKEN', 'test-github-token', 'Process')
    Import-Module $scripts.Tokens -Force
    $gitee = Get-AxToolsGiteeCredential
    if ($gitee.Token -ne 'test-fantasy-token' -or $gitee.SourceName -ne 'FANTASYTOOLS_GITEE_TOKEN') {
        throw 'AxTools 发布没有复用幻杀工具箱 Gitee 令牌。'
    }
    if ((Get-AxToolsGiteeToken -Explicit 'explicit-token') -ne 'explicit-token') {
        throw 'Gitee 令牌的显式参数没有优先于环境变量。'
    }
    $github = Get-AxToolsGithubCredential
    if ($null -eq $github -or $github.Token -ne 'test-github-token' -or $github.SourceName -notlike 'AXTOOLS_GITHUB_TOKEN*') {
        throw 'GitHub 令牌没有按 AXTOOLS_GITHUB_TOKEN 解析。'
    }
}
finally {
    Remove-Module AxToolsReleaseCommon -ErrorAction SilentlyContinue
    foreach ($name in $originalTokens.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalTokens[$name], 'Process')
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-ReleaseTests-' + [Guid]::NewGuid().ToString('N'))
$releaseAssets = Join-Path $testRoot 'ReleaseAssets'
function New-FakeProgram {
    # 载荷必须用随机字节：重复字符会被压到几 KB，分卷 / 增量 / 体积墙全都测不出来。
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][int]$PayloadKb, [Parameter(Mandatory)][string]$Tag)
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $Root 'Scripts\Release') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Root 'AxTools.exe') -Value "fake-exe-$Tag" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'App.xbf') -Value "fake-app-$Tag" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'MainWindow.xbf') -Value "fake-window-$Tag" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'AxTools.pri') -Value "fake-pri-$Tag" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'extra-module.dll') -Value "extra-$Tag" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'drop.pdb') -Value 'debug-symbol' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Root 'onnxruntime.dll') -Value 'ai-runtime' -Encoding utf8NoBOM
    $payload = [byte[]]::new($PayloadKb * 1024)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [IO.File]::WriteAllBytes((Join-Path $Root 'AxTools.dll'), $payload)
    Copy-Item -LiteralPath $scripts.Updater -Destination (Join-Path $Root 'Scripts\Release\Update-AxTools.ps1')
    return $Root
}
function Assert-AxExitCode {
    param([Parameter(Mandatory)][string]$Operation)
    if ($LASTEXITCODE -ne 0) { throw "$Operation 返回退出码 $LASTEXITCODE。" }
}

try {
    # ③ 全量出包：产物齐全、.pdb 与 AI 运行时被剔除、清单公告自动补一句。
    $programA = New-FakeProgram -Root (Join-Path $testRoot 'program-9.9.1') -PayloadKb 600 -Tag 'a'
    $stagingA = Join-Path $releaseAssets 'v9.9.1'
    & $scripts.Build -Version '9.9.1' -ProjectRoot $repoRoot -StagingDirectory $stagingA `
        -SourceDirectory $programA -SkipVersionWrite -ExcludeAiRuntime
    Assert-AxExitCode -Operation '出包 9.9.1'
    foreach ($name in @('AxTools-v9.9.1-win-x64.zip', 'AxTools-v9.9.1-win-x64.sha256.txt', 'toolbox-update.json', 'artifacts.json', 'file-index.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $stagingA $name) -PathType Leaf)) { throw "出包缺少产物：$name" }
    }
    $manifestA = Get-Content -LiteralPath (Join-Path $stagingA 'toolbox-update.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$manifestA.toolboxStableKey -ne 'AxTools' -or [string]$manifestA.version -ne '9.9.1' -or [string]$manifestA.channel -ne 'stable') {
        throw '更新清单身份信息不对。'
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifestA.releaseNotes)) { throw '公告为空时清单没有自动补一句说明。' }
    $indexA = Get-Content -LiteralPath (Join-Path $stagingA 'file-index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $indexA.files.PSObject.Properties['drop.pdb']) { throw '.pdb 不该进包，更不该进文件索引。' }
    if ($null -ne $indexA.files.PSObject.Properties['onnxruntime.dll']) { throw 'AI 推理运行时没有被剔除。' }

    & $scripts.Validate -ReleaseAssetRoot $stagingA -ExpectedVersion '9.9.1'
    Assert-AxExitCode -Operation '校验 9.9.1'

    # ④ 发布演练：两条通道都走一遍，不碰网络、不建任何远端资源。
    & $scripts.Publish -Version '9.9.1' -ProjectRoot $repoRoot -StagingDirectory $stagingA -SkipBuild -DryRun
    Assert-AxExitCode -Operation '发布演练 9.9.1'

    # ④b 不带 -SkipBuild 的演练：Gitee 段会真的去调出包脚本（靠台账复用，不重新编译）。
    #     这一步守的是"出包脚本的残留输出污染了通道返回值"那类问题 —— 现象是报错里夹着整段构建日志。
    $fullDryRun = (& $scripts.Publish -Version '9.9.1' -ProjectRoot $repoRoot -StagingDirectory $stagingA -SkipVersionWrite -DryRun *>&1 | Out-String)
    Assert-AxExitCode -Operation '完整演练 9.9.1'
    if (!$fullDryRun.Contains('完整发布完成', [StringComparison]::Ordinal)) {
        throw '完整演练没有走到结束（通道返回值可能被出包脚本的输出污染了）。'
    }

    # ⑤ 增量：只改一个文件、删一个文件 → 变化 1 个 / 删除 1 个，且增量包远小于整包。
    $programB = Join-Path $testRoot 'program-9.9.2'
    Copy-Item -LiteralPath $programA -Destination $programB -Recurse
    Set-Content -LiteralPath (Join-Path $programB 'AxTools.pri') -Value 'fake-pri-b2' -Encoding utf8NoBOM
    Remove-Item -LiteralPath (Join-Path $programB 'extra-module.dll') -Force
    $stagingB = Join-Path $releaseAssets 'v9.9.2'
    & $scripts.Build -Version '9.9.2' -ProjectRoot $repoRoot -StagingDirectory $stagingB `
        -SourceDirectory $programB -SkipVersionWrite -ExcludeAiRuntime
    Assert-AxExitCode -Operation '出包 9.9.2'
    $manifestB = Get-Content -LiteralPath (Join-Path $stagingB 'toolbox-update.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $manifestB.delta) { throw '没有产出增量包。' }
    if ([string]$manifestB.delta.baseVersion -ne '9.9.1') { throw "增量基准不对：$($manifestB.delta.baseVersion)" }
    if ([int]$manifestB.delta.changedCount -ne 1) { throw "变化文件数不对：$($manifestB.delta.changedCount)" }
    if ([int]$manifestB.delta.deleteCount -ne 1) { throw "删除文件数不对：$($manifestB.delta.deleteCount)" }
    if ([int64]$manifestB.delta.sizeBytes -ge ([int64]$manifestB.assets[0].sizeBytes / 2)) {
        throw '增量包不比整包小一半以上，说明逐文件比对没生效。'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $deltaArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $stagingB ([string]$manifestB.delta.fileName)))
    try {
        $deltaEntry = $deltaArchive.GetEntry('update-package.json')
        $reader = [IO.StreamReader]::new($deltaEntry.Open())
        try { $deltaManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    finally { $deltaArchive.Dispose() }
    if ($deltaManifest.delta -ne $true -or [string]$deltaManifest.baseVersion -ne '9.9.1') { throw '增量包内的体检单缺少 delta 段。' }
    if (@($deltaManifest.files).Count -ne 1 -or @($deltaManifest.delete).Count -ne 1) { throw '增量包内的文件表与删除表不对。' }
    if ([string]::IsNullOrWhiteSpace([string]$deltaManifest.files[0].baseSha256)) {
        throw '被修改的文件没有带 baseSha256（客户端就无法拒绝混装）。'
    }
    & $scripts.Validate -ReleaseAssetRoot $stagingB -ExpectedVersion '9.9.2'
    Assert-AxExitCode -Operation '校验 9.9.2'

    # ⑥ 分卷：强制切卷 → 卷数、sha256.txt 行数、拼接后逐字节相同。
    $programC = New-FakeProgram -Root (Join-Path $testRoot 'program-9.9.3') -PayloadKb 600 -Tag 'c'
    $stagingC = Join-Path $releaseAssets 'v9.9.3'
    & $scripts.Build -Version '9.9.3' -ProjectRoot $repoRoot -StagingDirectory $stagingC `
        -SourceDirectory $programC -SkipVersionWrite -ExcludeAiRuntime -PartSizeBytes 100KB -NoDelta
    Assert-AxExitCode -Operation '出包 9.9.3（分卷）'
    $manifestC = Get-Content -LiteralPath (Join-Path $stagingC 'toolbox-update.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $parts = @($manifestC.assets[0].parts)
    if ($parts.Count -lt 2) { throw "没有按 100 KB 分卷：$($parts.Count) 卷。" }
    $shaLines = @(Get-Content -LiteralPath (Join-Path $stagingC 'AxTools-v9.9.3-win-x64.sha256.txt'))
    if ($shaLines.Count -ne ($parts.Count + 1)) { throw "sha256.txt 行数不对：$($shaLines.Count)（应为 $($parts.Count + 1)）。" }
    $partSum = (@($parts) | Measure-Object -Property sizeBytes -Sum).Sum
    if ($partSum -ne [int64]$manifestC.assets[0].sizeBytes) { throw '分卷字节数之和与整包不一致。' }
    $joined = Join-Path $testRoot 'joined.zip'
    $output = [IO.File]::Create($joined)
    try {
        foreach ($part in $parts) {
            $input = [IO.File]::OpenRead((Join-Path $stagingC ([string]$part.fileName)))
            try { $input.CopyTo($output) } finally { $input.Dispose() }
        }
    }
    finally { $output.Dispose() }
    if ((Get-FileHash -LiteralPath $joined -Algorithm SHA256).Hash.ToLowerInvariant() -ne [string]$manifestC.assets[0].sha256) {
        throw '分卷拼接结果与整包不一致。'
    }
    & $scripts.Validate -ReleaseAssetRoot $stagingC -ExpectedVersion '9.9.3'
    Assert-AxExitCode -Operation '校验 9.9.3'

    # ⑦ 反面用例：坏 sha256.txt、缺入口 exe，都必须被拦下。
    $broken = Join-Path $releaseAssets 'v9.9.4'
    Copy-Item -LiteralPath $stagingA -Destination $broken -Recurse
    Set-Content -LiteralPath (Join-Path $broken 'AxTools-v9.9.1-win-x64.sha256.txt') `
        -Value (('0' * 64) + '  AxTools-v9.9.1-win-x64.zip') -Encoding ascii
    $rejected = $false
    try { & $scripts.Validate -ReleaseAssetRoot $broken -ExpectedVersion '9.9.1' }
    catch { $rejected = $true }
    if (!$rejected) { throw '坏 sha256.txt 竟然通过了校验。' }

    $brokenProgram = Join-Path $testRoot 'program-broken'
    New-FakeProgram -Root $brokenProgram -PayloadKb 10 -Tag 'x' | Out-Null
    Remove-Item -LiteralPath (Join-Path $brokenProgram 'AxTools.exe') -Force
    # 出包脚本把自己的失败收敛成"退出码 1 + stderr 说明"，所以这里看退出码，不看异常。
    & $scripts.Build -Version '9.9.9' -ProjectRoot $repoRoot -StagingDirectory (Join-Path $releaseAssets 'v9.9.9') `
        -SourceDirectory $brokenProgram -SkipVersionWrite
    if ($LASTEXITCODE -eq 0) { throw '缺少入口 exe 的程序目录竟然出包成功了。' }

    # ⑧ 复用：同版本重跑走台账复用，不重新压包（zip 的时间戳不变）。
    $zipPath = Join-Path $stagingA 'AxTools-v9.9.1-win-x64.zip'
    $zipWriteTime = (Get-Item -LiteralPath $zipPath).LastWriteTimeUtc
    $reuseText = (& $scripts.Build -Version '9.9.1' -ProjectRoot $repoRoot -StagingDirectory $stagingA `
            -SkipVersionWrite -ExcludeAiRuntime *>&1 | Out-String)
    Assert-AxExitCode -Operation '复用 9.9.1'
    if (!$reuseText.Contains('复用已有更新包', [StringComparison]::Ordinal)) { throw '同版本重跑没有走台账复用路径。' }
    if ((Get-Item -LiteralPath $zipPath).LastWriteTimeUtc -ne $zipWriteTime) { throw '复用路径竟然重压了 zip。' }

    Write-Host 'PASS: AxTools 发布链契约通过（出包 / 校验 / 增量 / 分卷 / 演练 / 复用）。'
    $global:LASTEXITCODE = 0
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
