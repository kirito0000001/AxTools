[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet("stable", "beta")][string]$Channel = "stable",
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet("win-x64")][string]$Runtime = "win-x64",
    [string]$ReleaseNotes = "",
    [string]$SourceDirectory = "",
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$stableKey = "AxTools"
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$'
if ($Version -notmatch $versionPattern) { throw "版本格式无效：$Version" }
if ($Channel -eq "stable" -and $Version.Contains('-')) { throw "正式版通道不能使用预发布版本号。" }

$projectFull = [IO.Path]::GetFullPath($ProjectRoot)
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$workRoot = Join-Path $outputFull ".work\$Version"
$publishRoot = Join-Path $workRoot $Runtime
$zipName = "AxTools-v$Version-$Runtime.zip"
$zipPath = Join-Path $outputFull $zipName
$shaPath = Join-Path $outputFull "AxTools-v$Version-$Runtime.sha256.txt"
$updateManifestPath = Join-Path $outputFull "toolbox-update.json"

if ($DryRun) {
    Write-Host "DryRun：将生成 $zipPath"
    return
}

New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    if (!$SkipBuild) {
        & dotnet test (Join-Path $projectFull "AxTools.Tests\AxTools.Tests.csproj") -c Release
        if ($LASTEXITCODE -ne 0) { throw "单元测试失败：$LASTEXITCODE" }
    }
    if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
    & dotnet publish (Join-Path $projectFull "AxTools.csproj") -c Release -p:Platform=x64 -r $Runtime --self-contained true -o $publishRoot -p:Version=$Version -p:InformationalVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
    $layoutRoot = Join-Path $projectFull "bin\x64\Release\net8.0-windows10.0.19041.0"
    foreach ($resourceName in @('App.xbf', 'MainWindow.xbf', 'AxTools.pri')) {
        $resourcePath = Join-Path $layoutRoot $resourceName
        if (!(Test-Path -LiteralPath $resourcePath -PathType Leaf)) { throw "Release 布局缺少 WinUI 资源：$resourcePath" }
        Copy-Item -LiteralPath $resourcePath -Destination (Join-Path $publishRoot $resourceName) -Force
    }
}
else {
    $publishRoot = [IO.Path]::GetFullPath($SourceDirectory)
}

$entryPath = Join-Path $publishRoot "AxTools.exe"
if (!(Test-Path -LiteralPath $entryPath -PathType Leaf)) { throw "发布目录缺少 AxTools.exe：$publishRoot" }
foreach ($resourceName in @('App.xbf', 'MainWindow.xbf', 'AxTools.pri')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot $resourceName) -PathType Leaf)) { throw "发布目录缺少 WinUI 资源：$resourceName" }
}
$updaterSource = Join-Path $projectFull "Scripts\Release\Update-AxTools.ps1"
$updaterTarget = Join-Path $publishRoot "Scripts\Release\Update-AxTools.ps1"
if (!(Test-Path -LiteralPath $updaterTarget) -and (Test-Path -LiteralPath $updaterSource)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $updaterTarget) -Force | Out-Null
    Copy-Item -LiteralPath $updaterSource -Destination $updaterTarget -Force
}

$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object { $_.Name -ne 'update-package.json' } | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishRoot, $_.FullName).Replace('\', '/')
    [ordered]@{ path = $relative; sizeBytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$packageManifest = [ordered]@{
    schemaVersion = 1; toolboxStableKey = $stableKey; version = $Version; channel = $Channel
    runtime = $Runtime; entryExe = "AxTools.exe"; generatedAt = (Get-Date).ToUniversalTime().ToString('o'); files = $files
}
$packageManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publishRoot "update-package.json") -Encoding utf8NoBOM

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item -LiteralPath $zipPath
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipSha  $zipName" | Set-Content -LiteralPath $shaPath -Encoding ascii
$manifest = [ordered]@{
    schemaVersion = 1; toolboxStableKey = $stableKey; displayName = "AxTools"; version = $Version; channel = $Channel
    publishedAt = (Get-Date).ToUniversalTime().ToString('o'); minSupportedVersion = "1.0.0"; requiresManualMigration = $false; requiresRestart = $true
    releaseNotes = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        if ($Channel -eq 'beta') { "AxTools $Version 测试版" } else { "AxTools $Version 正式版" }
    } else {
        $ReleaseNotes.Trim()
    }
    assets = @([ordered]@{ runtime = $Runtime; fileName = $zipName; sha256 = $zipSha; sizeBytes = $zipItem.Length })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $updateManifestPath -Encoding utf8NoBOM
Write-Host "AxTools 发布包已生成：$zipPath"
$global:LASTEXITCODE = 0
