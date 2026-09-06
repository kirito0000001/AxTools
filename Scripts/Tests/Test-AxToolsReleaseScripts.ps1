[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packageScript = Join-Path $repoRoot "Scripts\Release\Package-AxTools.ps1"
$validateScript = Join-Path $repoRoot "Scripts\Release\Test-AxToolsPackage.ps1"
$publishScript = Join-Path $repoRoot "Scripts\Release\Publish-AxToolsRelease.ps1"
$updaterScript = Join-Path $repoRoot "Scripts\Release\Update-AxTools.ps1"
$releaseCommonModule = Join-Path $repoRoot "Scripts\Release\AxToolsReleaseCommon.psm1"

if (!(Test-Path -LiteralPath $releaseCommonModule -PathType Leaf)) {
    throw "缺少 AxTools 发布公共模块：$releaseCommonModule"
}
$originalAxToken = [Environment]::GetEnvironmentVariable('AXTOOLS_GITEE_TOKEN', 'Process')
$originalFantasyToken = [Environment]::GetEnvironmentVariable('FANTASYTOOLS_GITEE_TOKEN', 'Process')
try {
    [Environment]::SetEnvironmentVariable('AXTOOLS_GITEE_TOKEN', $null, 'Process')
    [Environment]::SetEnvironmentVariable('FANTASYTOOLS_GITEE_TOKEN', 'test-fantasy-token', 'Process')
    Import-Module $releaseCommonModule -Force
    $credential = Get-AxToolsGiteeCredential
    if ($credential.Token -ne 'test-fantasy-token' -or
        $credential.SourceName -ne 'FANTASYTOOLS_GITEE_TOKEN') {
        throw 'AxTools 发布没有复用幻杀工具箱 Gitee 令牌。'
    }
}
finally {
    Remove-Module AxToolsReleaseCommon -ErrorAction SilentlyContinue
    [Environment]::SetEnvironmentVariable('AXTOOLS_GITEE_TOKEN', $originalAxToken, 'Process')
    [Environment]::SetEnvironmentVariable('FANTASYTOOLS_GITEE_TOKEN', $originalFantasyToken, 'Process')
}

$publishSource = Get-Content -LiteralPath $publishScript -Raw -Encoding UTF8
if ($publishSource -notmatch '\[string\]\$ReleaseNotes') {
    throw 'AxTools 发布脚本没有接收更新说明。'
}
if ($publishSource -notmatch '## AxTools V\$Version' -or
    $publishSource -match '## 更新说明') {
    throw 'AxTools 发布脚本没有使用单一可编辑正文与空白默认介绍。'
}
$githubDeleteIndex = $publishSource.IndexOf('release delete-asset', [StringComparison]::Ordinal)
$githubUploadIndex = $publishSource.IndexOf('release upload', [StringComparison]::Ordinal)
if ($githubDeleteIndex -lt 0 -or $githubUploadIndex -lt 0 -or $githubDeleteIndex -gt $githubUploadIndex) {
    throw 'AxTools 同版本重传没有在上传前清空 GitHub Release 旧附件。'
}

foreach ($requiredScript in @($packageScript, $validateScript, $publishScript, $updaterScript, $releaseCommonModule)) {
    if (!(Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "缺少发布脚本：$requiredScript"
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("AxTools-ReleaseTests-" + [Guid]::NewGuid().ToString("N"))
$sourceRoot = Join-Path $testRoot "publish"
$outputRoot = Join-Path $testRoot "ReleaseAssets"
try {
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot "Scripts\Release") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceRoot "AxTools.exe") -Value "fake-exe" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceRoot "AxTools.dll") -Value "fake-dll" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceRoot "App.xbf") -Value "fake-app-xbf" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceRoot "MainWindow.xbf") -Value "fake-window-xbf" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceRoot "AxTools.pri") -Value "fake-pri" -Encoding utf8NoBOM
    Copy-Item -LiteralPath $updaterScript -Destination (Join-Path $sourceRoot "Scripts\Release\Update-AxTools.ps1")

    & $packageScript `
        -ProjectRoot $repoRoot `
        -Version "1.0.1-beta.1" `
        -Channel "beta" `
        -OutputRoot $outputRoot `
        -ReleaseNotes "AxTools 测试包说明" `
        -SourceDirectory $sourceRoot `
        -SkipBuild

    if ($LASTEXITCODE -ne 0) {
        throw "Package-AxTools.ps1 返回退出码 $LASTEXITCODE"
    }

    $manifestPath = Join-Path $outputRoot "toolbox-update.json"
    $zipPath = Join-Path $outputRoot "AxTools-v1.0.1-beta.1-win-x64.zip"
    $shaPath = Join-Path $outputRoot "AxTools-v1.0.1-beta.1-win-x64.sha256.txt"
    foreach ($path in @($manifestPath, $zipPath, $shaPath)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "缺少预期产物：$path"
        }
    }
    $packageManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    if ($packageManifest.releaseNotes -ne 'AxTools 测试包说明') {
        throw 'AxTools 更新清单没有保留自定义 Release 介绍。'
    }

    & $validateScript -ReleaseAssetRoot $outputRoot -ExpectedVersion "1.0.1-beta.1"
    if ($LASTEXITCODE -ne 0) {
        throw "Test-AxToolsPackage.ps1 返回退出码 $LASTEXITCODE"
    }

    & $publishScript `
        -ProjectRoot $repoRoot `
        -Version "1.0.1-beta.1" `
        -Channel "beta" `
        -OutputRoot $outputRoot `
        -Target "Both" `
        -ReleaseNotes "修复发布流程" `
        -SkipBuild `
        -DryRun
    if ($LASTEXITCODE -ne 0) {
        throw "发布 DryRun 返回退出码 $LASTEXITCODE"
    }

    $installRoot = Join-Path $testRoot "installed"
    $projectDataRoot = Join-Path $testRoot "project-data"
    $readyPath = Join-Path $testRoot "updater-ready.signal"
    $updaterLog = Join-Path $testRoot "updater.log"
    New-Item -ItemType Directory -Path $installRoot, $projectDataRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installRoot "AxTools.exe") -Value "old-exe" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $projectDataRoot "AxTools.settings.json") -Value "keep" -Encoding utf8NoBOM
    & $updaterScript `
        -PackagePath $zipPath `
        -TargetDirectory $installRoot `
        -MainProcessId 2147483646 `
        -EntryExe "AxTools.exe" `
        -TargetVersion "1.0.1-beta.1" `
        -ReadySignalPath $readyPath `
        -LogPath $updaterLog `
        -NoRestart
    if (!(Test-Path -LiteralPath $readyPath) -or !(Test-Path -LiteralPath (Join-Path $installRoot "update-package.json"))) {
        throw "外部更新覆盖未成功。"
    }
    if ((Get-Content -LiteralPath (Join-Path $projectDataRoot "AxTools.settings.json") -Raw).Trim() -ne "keep") {
        throw "外部更新错误修改了项目数据目录。"
    }

    $invalidZip = Join-Path $testRoot "invalid.zip"
    Compress-Archive -Path (Join-Path $projectDataRoot '*') -DestinationPath $invalidZip
    $oldContent = Get-Content -LiteralPath (Join-Path $installRoot "AxTools.exe") -Raw
    try {
        & $updaterScript `
            -PackagePath $invalidZip `
            -TargetDirectory $installRoot `
            -MainProcessId 2147483646 `
            -EntryExe "AxTools.exe" `
            -TargetVersion "1.0.1-beta.1" `
            -ReadySignalPath (Join-Path $testRoot "invalid-ready.signal") `
            -LogPath (Join-Path $testRoot "invalid-update.log") `
            -NoRestart
        throw "无效更新包未被拒绝。"
    }
    catch {
        if ($_.Exception.Message -eq "无效更新包未被拒绝。") { throw }
    }
    if ((Get-Content -LiteralPath (Join-Path $installRoot "AxTools.exe") -Raw) -ne $oldContent) {
        throw "无效更新包改变了现有程序。"
    }

    Write-Host "PASS: AxTools 发布脚本契约测试通过。"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
