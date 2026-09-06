[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-SmartDotNetLaunch-' + [Guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT
$originalLog = $env:AXTOOLS_FAKE_DOTNET_LOG

function Invoke-Adapter {
    param([string]$Entry,[string]$ProjectRoot,[string]$Executable,[string]$Action = 'BuildAndRun')

    $lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $Entry `
        -Action $Action -ProjectRoot $ProjectRoot -DevelopmentExecutable $Executable 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "适配器执行失败：$Entry`n$($lines -join [Environment]::NewLine)"
    }
    return $lines
}

function Get-BuildCount {
    if (!(Test-Path -LiteralPath $env:AXTOOLS_FAKE_DOTNET_LOG -PathType Leaf)) { return 0 }
    return @(
        Get-Content -LiteralPath $env:AXTOOLS_FAKE_DOTNET_LOG |
        Where-Object { $_ -match '^build\s' }
    ).Count
}

function Assert-SmartLaunch {
    param(
        [string]$Name,
        [string]$Entry,
        [string]$ProjectRoot,
        [string]$Executable,
        [string]$ChangedSource,
        [string]$ExecutableToRemove = ''
    )

    Clear-Content -LiteralPath $env:AXTOOLS_FAKE_DOTNET_LOG
    $first = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 1) { throw "$Name 首次启动没有执行一次 build。" }

    $second = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 1) { throw "$Name 源码未变化时仍然执行了 build。" }
    if (!($second | Where-Object { $_ -match '源码未变化.*直接启动' })) {
        throw "$Name 快速启动缺少明确日志。"
    }

    $forced = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable -Action ForceBuildAndRun
    if ((Get-BuildCount) -ne 2) { throw "$Name 强制重新编译没有执行 build。" }
    if (!($forced | Where-Object { $_ -match '已选择强制重新编译' })) {
        throw "$Name 强制重新编译缺少明确日志。"
    }

    Set-Content -LiteralPath $ChangedSource -Value "changed-$([Guid]::NewGuid())" -Encoding UTF8
    $third = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 3) { throw "$Name 源码变化后没有重新执行 build。" }
    if (!($third | Where-Object { $_ -match '源码或配置变化.*增量编译|构建状态不可用.*增量编译' })) {
        throw "$Name 重新编译缺少明确日志。"
    }

    $removePath = if ([string]::IsNullOrWhiteSpace($ExecutableToRemove)) {
        $Executable
    } else {
        $ExecutableToRemove
    }
    Remove-Item -LiteralPath $removePath -Force
    $fourth = Invoke-Adapter -Entry $Entry -ProjectRoot $ProjectRoot -Executable $Executable
    if ((Get-BuildCount) -ne 4) { throw "$Name 开发产物被清理后没有重新执行 build。" }
}

try {
    $fakeBin = Join-Path $testRoot 'fake-bin'
    New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
    $env:AXTOOLS_FAKE_DOTNET_LOG = Join-Path $testRoot 'dotnet.log'
    Set-Content -LiteralPath $env:AXTOOLS_FAKE_DOTNET_LOG -Value '' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $fakeBin 'dotnet.cmd') -Value @(
        '@echo %*>>"%AXTOOLS_FAKE_DOTNET_LOG%"',
        '@if /I "%1"=="build" copy /Y "%WINDIR%\System32\where.exe" "%AXTOOLS_FAKE_BUILD_EXE%" >nul',
        '@if /I "%1"=="build" echo pri>"%AXTOOLS_FAKE_BUILD_PRI%"',
        '@if /I "%1"=="build" echo app>"%AXTOOLS_FAKE_BUILD_APP_XBF%"',
        '@if /I "%1"=="build" echo main>"%AXTOOLS_FAKE_BUILD_MAIN_XBF%"',
        '@exit /b 0') -Encoding ASCII
    $env:PATH = "$fakeBin;$originalPath"
    $env:AXTOOLS_BUILD_STATE_ROOT = Join-Path $testRoot 'state'

    $axRoot = Join-Path $testRoot 'AxTools'
    $axOutput = Join-Path $axRoot 'bin\x64\Debug'
    New-Item -ItemType Directory -Path $axOutput -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $axRoot 'AxTools.csproj') -Value '<Project />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $axRoot 'AxTools.sln') -Value '' -Encoding UTF8
    $axSource = Join-Path $axRoot 'Main.cs'
    Set-Content -LiteralPath $axSource -Value 'class App {}' -Encoding UTF8
    $axExe = Join-Path $axOutput 'AxTools.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $axExe
    foreach ($name in @('AxTools.pri','App.xbf','MainWindow.xbf')) {
        Set-Content -LiteralPath (Join-Path $axOutput $name) -Value $name -Encoding UTF8
    }
    $env:AXTOOLS_FAKE_BUILD_EXE = $axExe
    $env:AXTOOLS_FAKE_BUILD_PRI = Join-Path $axOutput 'AxTools.pri'
    $env:AXTOOLS_FAKE_BUILD_APP_XBF = Join-Path $axOutput 'App.xbf'
    $env:AXTOOLS_FAKE_BUILD_MAIN_XBF = Join-Path $axOutput 'MainWindow.xbf'
    Assert-SmartLaunch -Name 'AxTools' `
        -Entry (Join-Path $scriptsRoot 'Adapters\AxTools\Invoke-AxToolsAction.ps1') `
        -ProjectRoot $axRoot -Executable $axExe -ChangedSource $axSource

    $fantasyRoot = Join-Path $testRoot 'FantasyTools'
    $fantasyOutput = Join-Path $fantasyRoot 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
    New-Item -ItemType Directory -Path $fantasyOutput,(Join-Path $fantasyRoot 'Scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fantasyRoot 'FantasyTools.csproj') -Value '<Project />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fantasyRoot 'FantasyTools.sln') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fantasyRoot 'Scripts\打包工具箱.ps1') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fantasyRoot 'Scripts\发布新版本.ps1') -Value '' -Encoding UTF8
    $fantasySource = Join-Path $fantasyRoot 'MainWindow.xaml'
    Set-Content -LiteralPath $fantasySource -Value '<Window />' -Encoding UTF8
    $fantasyExe = Join-Path $fantasyOutput '幻杀工具箱.exe'
    $fantasyConfiguredExe = Join-Path $fantasyOutput 'AppX\FantasyTools.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $fantasyConfiguredExe) -Force | Out-Null
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $fantasyConfiguredExe
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $fantasyExe
    foreach ($name in @('幻杀工具箱.pri','App.xbf','MainWindow.xbf')) {
        Set-Content -LiteralPath (Join-Path $fantasyOutput $name) -Value $name -Encoding UTF8
    }
    $env:AXTOOLS_FAKE_BUILD_EXE = $fantasyExe
    $env:AXTOOLS_FAKE_BUILD_PRI = Join-Path $fantasyOutput '幻杀工具箱.pri'
    $env:AXTOOLS_FAKE_BUILD_APP_XBF = Join-Path $fantasyOutput 'App.xbf'
    $env:AXTOOLS_FAKE_BUILD_MAIN_XBF = Join-Path $fantasyOutput 'MainWindow.xbf'
    Assert-SmartLaunch -Name 'FantasyTools' `
        -Entry (Join-Path $scriptsRoot 'Adapters\FantasyTools\Invoke-FantasyToolsAction.ps1') `
        -ProjectRoot $fantasyRoot -Executable $fantasyConfiguredExe -ChangedSource $fantasySource `
        -ExecutableToRemove $fantasyExe

    $galRoot = Join-Path $testRoot 'GalExcleTools'
    $galOutput = Join-Path $galRoot 'bin\x64\Debug'
    New-Item -ItemType Directory -Path $galOutput,(Join-Path $galRoot 'Scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $galRoot 'GalExcleTools.csproj') -Value '<Project />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $galRoot 'GalExcleTools.sln') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $galRoot 'Scripts\Test-SourceHealth.ps1') -Value '' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $galRoot 'Scripts\Package-App.ps1') -Value '' -Encoding UTF8
    $galSource = Join-Path $galRoot 'MainWindow.xaml'
    Set-Content -LiteralPath $galSource -Value '<Window />' -Encoding UTF8
    $galExe = Join-Path $galOutput 'TFAC剧情箱-轮椅版.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination $galExe
    foreach ($name in @('TFAC剧情箱-轮椅版.pri','App.xbf','MainWindow.xbf')) {
        Set-Content -LiteralPath (Join-Path $galOutput $name) -Value $name -Encoding UTF8
    }
    $env:AXTOOLS_FAKE_BUILD_EXE = $galExe
    $env:AXTOOLS_FAKE_BUILD_PRI = Join-Path $galOutput 'TFAC剧情箱-轮椅版.pri'
    $env:AXTOOLS_FAKE_BUILD_APP_XBF = Join-Path $galOutput 'App.xbf'
    $env:AXTOOLS_FAKE_BUILD_MAIN_XBF = Join-Path $galOutput 'MainWindow.xbf'
    Assert-SmartLaunch -Name 'GalExcleTools' `
        -Entry (Join-Path $scriptsRoot 'Adapters\GalExcleTools\Invoke-GalExcleToolsAction.ps1') `
        -ProjectRoot $galRoot -Executable $galExe -ChangedSource $galSource
}
finally {
    $env:PATH = $originalPath
    $env:AXTOOLS_BUILD_STATE_ROOT = $originalStateRoot
    $env:AXTOOLS_FAKE_DOTNET_LOG = $originalLog
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'PASS: 三个 .NET/WinUI 工具智能开发启动合同通过。'
exit 0
