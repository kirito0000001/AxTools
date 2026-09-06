[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-Msvc-' + [Guid]::NewGuid().ToString('N'))
$visualStudioRoot = Join-Path $testRoot 'Visual Studio 18 Insiders'
$buildRoot = Join-Path $visualStudioRoot 'VC\Auxiliary\Build'
$linkRoot = Join-Path $visualStudioRoot 'VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64'
$sdkLibRoot = Join-Path $testRoot 'Windows Kits\10\Lib\10.0.26100.0\um\x64'

try {
    New-Item -ItemType Directory -Path $buildRoot,$linkRoot,$sdkLibRoot -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $linkRoot 'link.exe'),(Join-Path $sdkLibRoot 'shell32.lib') -Force | Out-Null
    $batch = @"
@echo off
set "VCToolsInstallDir=$visualStudioRoot\VC\Tools\MSVC\14.44.35207\"
set "VCToolsVersion=14.44.35207"
set "WindowsSdkDir=$testRoot\Windows Kits\10\"
set "WindowsSDKVersion=10.0.26100.0\"
set "PATH=$linkRoot;%PATH%"
set "LIB=$sdkLibRoot"
"@
    Set-Content -LiteralPath (Join-Path $buildRoot 'vcvarsall.bat') -Value $batch -Encoding ASCII
    $env:AXTOOLS_VISUAL_STUDIO_ROOT = $visualStudioRoot
    $env:AXTOOLS_MSVC_VERSION = '14.44.35207'
    Remove-Item Env:VCToolsInstallDir,Env:VCToolsVersion,Env:WindowsSdkDir,Env:WindowsSDKVersion,Env:LIB -ErrorAction SilentlyContinue

    Import-Module (Join-Path $PSScriptRoot '..\Common\AxAdapterCommon.psm1') -Force
    $result = Initialize-AxMsvcEnvironment

    if ($result.MsvcVersion -ne '14.44.35207') { throw "MSVC version mismatch: $($result.MsvcVersion)" }
    if ($result.LinkerPath -ne (Join-Path $linkRoot 'link.exe')) { throw "link.exe mismatch: $($result.LinkerPath)" }
    if ($result.Shell32LibraryPath -ne (Join-Path $sdkLibRoot 'shell32.lib')) { throw "shell32.lib mismatch: $($result.Shell32LibraryPath)" }
    'AX_MSVC_ENVIRONMENT_PASS'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
