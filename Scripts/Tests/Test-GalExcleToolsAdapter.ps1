[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $scriptsRoot 'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1'
Assert-True (Test-Path -LiteralPath $entry -PathType Leaf) "剧情工具箱适配入口不存在：$entry"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-GalExcleTools-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $testRoot 'GalExcleTools'
$outputRoot = Join-Path $testRoot 'DabaoV'
$developmentRoot = Join-Path $projectRoot 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
$developmentExecutable = Join-Path $developmentRoot 'TFAC剧情箱-轮椅版.exe'
$fakeBin = Join-Path $testRoot 'fake-bin'
$dotnetLog = Join-Path $testRoot 'dotnet.log'
$previousPath = $env:PATH
$previousDotnetLog = $env:AXTOOLS_TEST_DOTNET_LOG
$previousStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'Scripts'),$outputRoot | Out-Null

try {
    $env:AXTOOLS_BUILD_STATE_ROOT = Join-Path $testRoot 'build-state'
    Set-Content -LiteralPath (Join-Path $projectRoot 'GalExcleTools.csproj') -Value '<Project />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $projectRoot 'GalExcleTools.sln') -Value '' -Encoding UTF8
    @'
param([switch]$Build)
$ErrorActionPreference = 'Stop'
Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'source-health.called') -Value ([string]$Build.IsPresent)
'@ | Set-Content -LiteralPath (Join-Path $projectRoot 'Scripts\Test-SourceHealth.ps1') -Encoding UTF8
    @'
param([string]$Configuration,[string]$Runtime,[string]$OutputRoot,[string]$Version,[switch]$Clean,[switch]$KeepWorkFolder)
$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $OutputRoot 'TFAC剧情箱-轮椅版V2.1.0'
$programRoot = Join-Path $packageRoot 'TFAC剧情箱-轮椅版'
New-Item -ItemType Directory -Force -Path $programRoot | Out-Null
foreach($name in @('TFAC剧情箱-轮椅版.exe','TFAC剧情箱-轮椅版.pri','App.xbf','MainWindow.xbf','TFAC剧情箱-轮椅版.runtimeconfig.json','TFAC剧情箱-轮椅版.deps.json')) {
    Set-Content -LiteralPath (Join-Path $programRoot $name) -Value $name -Encoding UTF8
}
Set-Content -LiteralPath (Join-Path $packageRoot 'TFAC剧情箱-轮椅版.lnk') -Value 'shortcut' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $OutputRoot 'package.called') -Value "$Configuration|$Runtime|$Clean"
'@ | Set-Content -LiteralPath (Join-Path $projectRoot 'Scripts\Package-App.ps1') -Encoding UTF8

    [IO.Directory]::CreateDirectory($developmentRoot) | Out-Null
    [IO.Directory]::CreateDirectory($fakeBin) | Out-Null
    foreach ($name in @('TFAC剧情箱-轮椅版.exe','TFAC剧情箱-轮椅版.pri','App.xbf','MainWindow.xbf')) {
        Set-Content -LiteralPath (Join-Path $developmentRoot $name) -Value $name -Encoding UTF8
    }
    $fakeDotnetPath = Join-Path $fakeBin 'dotnet.cmd'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $fakeDotnetPath)) | Out-Null
    @'
@echo off
echo %*>>"%AXTOOLS_TEST_DOTNET_LOG%"
exit /b 0
'@ | Set-Content -LiteralPath $fakeDotnetPath -Encoding ASCII
    $env:PATH = "$fakeBin;$previousPath"
    $env:AXTOOLS_TEST_DOTNET_LOG = $dotnetLog

    $packageRoot = Join-Path $testRoot 'packages'
    $packagePath = Join-Path $packageRoot 'example\1.0.0'
    $objRoot = Join-Path $projectRoot 'obj'
    [IO.Directory]::CreateDirectory($packagePath) | Out-Null
    [IO.Directory]::CreateDirectory($objRoot) | Out-Null
    $assets = [ordered]@{
        packageFolders = [ordered]@{ ($packageRoot.TrimEnd('\') + '\') = @{} }
        libraries = [ordered]@{
            'example/1.0.0' = [ordered]@{ type = 'package'; path = 'example/1.0.0' }
        }
    }
    $assets | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $objRoot 'project.assets.json') -Encoding UTF8

    $buildLines = @(& $entry -Action Build -ProjectRoot $projectRoot -DevelopmentExecutable $developmentExecutable 2>&1 |
        ForEach-Object { $_.ToString() })
    Assert-True ($LASTEXITCODE -eq 0) "剧情工具箱缓存构建失败：$($buildLines -join [Environment]::NewLine)"
    $dotnetCalls = @(Get-Content -LiteralPath $dotnetLog)
    Assert-True ($dotnetCalls.Count -eq 1) '依赖缓存有效时不应单独执行 restore。'
    Assert-True ($dotnetCalls[0] -match '--no-restore') '依赖缓存有效时 build 必须使用 --no-restore。'
    Assert-True ([bool]($buildLines | Where-Object { $_ -match '"percent":10' })) '构建应上报依赖缓存检查进度。'
    Assert-True ([bool]($buildLines | Where-Object { $_ -match '"percent":90' })) '构建应上报产物校验进度。'

    Remove-Item -LiteralPath (Join-Path $objRoot 'project.assets.json') -Force
    Remove-Item -LiteralPath $dotnetLog -Force
    $restoreLines = @(& $entry -Action Build -ProjectRoot $projectRoot -DevelopmentExecutable $developmentExecutable 2>&1 |
        ForEach-Object { $_.ToString() })
    Assert-True ($LASTEXITCODE -eq 0) "剧情工具箱恢复后构建失败：$($restoreLines -join [Environment]::NewLine)"
    $dotnetCalls = @(Get-Content -LiteralPath $dotnetLog)
    Assert-True ($dotnetCalls.Count -eq 2) '依赖缓存缺失时应执行一次 restore 和一次 build。'
    Assert-True ($dotnetCalls[0] -match '^restore ') '依赖缓存缺失时第一次调用必须是 restore。'
    Assert-True ($dotnetCalls[1] -match '^build .*--no-restore') '恢复后 build 必须使用 --no-restore。'

    foreach ($action in @('SourceHealthCheck','PackageX64','ValidatePackage')) {
        $lines = @(& $entry -Action $action -ProjectRoot $projectRoot -OutputRoot $outputRoot 2>&1 |
            ForEach-Object { $_.ToString() })
        Assert-True ($LASTEXITCODE -eq 0) "剧情工具箱 $action 失败：$($lines -join [Environment]::NewLine)"
        Assert-True ([bool]($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) "剧情工具箱 $action 缺少成功 result。"
    }

    Assert-True ((Get-Content -LiteralPath (Join-Path $projectRoot 'source-health.called') -Raw).Trim() -eq 'False') '源码健康检查不应重复传入 -Build。'
    Assert-True ((Get-Content -LiteralPath (Join-Path $outputRoot 'package.called') -Raw).Trim() -eq 'Release|win-x64|False') '打包参数没有保持 Release/win-x64/非 Clean。'
    Write-Output 'PASS: 剧情工具箱适配器契约通过。'
}
finally {
    $env:PATH = $previousPath
    $env:AXTOOLS_TEST_DOTNET_LOG = $previousDotnetLog
    $env:AXTOOLS_BUILD_STATE_ROOT = $previousStateRoot
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
