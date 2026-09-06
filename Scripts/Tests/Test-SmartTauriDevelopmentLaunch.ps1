[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-SmartTauriLaunch-' + [Guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT
$originalLog = $env:AXTOOLS_FAKE_NPM_LOG
$originalVisualStudioRoot = $env:AXTOOLS_VISUAL_STUDIO_ROOT
$originalMsvcVersion = $env:AXTOOLS_MSVC_VERSION

function Invoke-Adapter {
    param([string]$Entry,[string]$ProjectRoot,[string]$Executable,[string]$Action = 'RunDevelopment')

    $lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $Entry `
        -Action $Action -ProjectRoot $ProjectRoot -DevelopmentExecutable $Executable 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "适配器执行失败：$Entry`n$($lines -join [Environment]::NewLine)"
    }
    return $lines
}

function Get-BuildCount {
    return @(
        Get-Content -LiteralPath $env:AXTOOLS_FAKE_NPM_LOG |
        Where-Object { $_ -match '^run tauri -- build --debug --no-bundle$' }
    ).Count
}

function Assert-SmartLaunch {
    param([string]$Name,[string]$Entry,[string]$ProjectRoot,[string]$Executable,[string]$ChangedSource)

    Clear-Content -LiteralPath $env:AXTOOLS_FAKE_NPM_LOG
    $first = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 1) { throw "$Name 首次启动没有执行一次 Tauri build。" }

    $second = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 1) { throw "$Name 源码未变化时仍然执行了 Tauri build。" }
    if (!($second | Where-Object { $_ -match '源码未变化.*直接启动' })) {
        throw "$Name 快速启动缺少明确日志。"
    }

    $forced = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable -Action ForceBuildAndRun
    if ((Get-BuildCount) -ne 2) { throw "$Name 强制重新编译没有执行 Tauri build。" }
    if (!($forced | Where-Object { $_ -match '已选择强制重新编译' })) {
        throw "$Name 强制重新编译缺少明确日志。"
    }

    Set-Content -LiteralPath $ChangedSource -Value "changed-$([Guid]::NewGuid())" -Encoding UTF8
    $third = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 3) { throw "$Name 源码变化后没有重新执行 Tauri build。" }
}

function Initialize-TauriProject {
    param([string]$Root,[switch]$Fantasy)

    New-Item -ItemType Directory -Path (Join-Path $Root 'src'),(Join-Path $Root 'src-tauri\target\debug'),(Join-Path $Root 'Scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Root 'package.json') -Value '{}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root 'src\App.vue') -Value '<template />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root 'src-tauri\Cargo.toml') -Value '[package]' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root 'src-tauri\main.rs') -Value 'fn main() {}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root 'Scripts\Build-LauncherUpdaterPackage.ps1') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root 'Scripts\Publish-LauncherGiteePackage.ps1') -Value '' -Encoding UTF8
    if ($Fantasy) {
        Set-Content -LiteralPath (Join-Path $Root 'launcher.config.json') -Value '{}' -Encoding UTF8
    }
}

try {
    $fakeBin = Join-Path $testRoot 'fake-bin'
    New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
    $env:AXTOOLS_FAKE_NPM_LOG = Join-Path $testRoot 'npm.log'
    Set-Content -LiteralPath $env:AXTOOLS_FAKE_NPM_LOG -Value '' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fakeBin 'npm.cmd') -Value @(
        '@echo %*>>"%AXTOOLS_FAKE_NPM_LOG%"',
        '@exit /b 0') -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fakeBin 'cargo.cmd') -Value '@exit /b 0' -Encoding ASCII
    $env:PATH = "$fakeBin;$originalPath"
    $env:AXTOOLS_BUILD_STATE_ROOT = Join-Path $testRoot 'state'
    $env:AXTOOLS_VISUAL_STUDIO_ROOT = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
    $env:AXTOOLS_MSVC_VERSION = '14.44.35207'

    $fantasyRoot = Join-Path $testRoot 'FantasyProject-PC'
    Initialize-TauriProject -Root $fantasyRoot -Fantasy
    $fantasyExe = Join-Path $fantasyRoot 'src-tauri\target\debug\fantasyproject-pc-launcher.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $fantasyExe
    Assert-SmartLaunch -Name 'FantasyProject-PC' `
        -Entry (Join-Path $scriptsRoot 'Adapters\FantasyProjectPc\Invoke-FantasyProjectPcAction.ps1') `
        -ProjectRoot $fantasyRoot -Executable $fantasyExe -ChangedSource (Join-Path $fantasyRoot 'src\App.vue')

    $crossingRoot = Join-Path $testRoot 'CrossingVoidinitiator-PC'
    Initialize-TauriProject -Root $crossingRoot
    $crossingExe = Join-Path $crossingRoot 'src-tauri\target\debug\tauri-vue-launcher.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $crossingExe
    Assert-SmartLaunch -Name 'CrossingVoidinitiator-PC' `
        -Entry (Join-Path $scriptsRoot 'Adapters\CrossingVoidPc\Invoke-CrossingVoidPcAction.ps1') `
        -ProjectRoot $crossingRoot -Executable $crossingExe -ChangedSource (Join-Path $crossingRoot 'src-tauri\main.rs')
}
finally {
    $env:PATH = $originalPath
    $env:AXTOOLS_BUILD_STATE_ROOT = $originalStateRoot
    $env:AXTOOLS_FAKE_NPM_LOG = $originalLog
    $env:AXTOOLS_VISUAL_STUDIO_ROOT = $originalVisualStudioRoot
    $env:AXTOOLS_MSVC_VERSION = $originalMsvcVersion
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'PASS: 两个 Tauri 工具智能开发启动合同通过。'
exit 0
