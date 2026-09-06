[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BandizipExecutable = '',
    [Parameter(Mandatory)][string]$GameVersion,
    [ValidateRange(1, [long]::MaxValue)][long]$ChunkSizeBytes = [int64](500MB),
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force

function Assert-NotCancelled {
    if (Test-AxTaskCancellation) {
        throw [OperationCanceledException]::new('用户已停止游戏分片任务。')
    }
}

function Resolve-BandizipConsole {
    if (-not [string]::IsNullOrWhiteSpace($BandizipExecutable)) {
        $candidate = [IO.Path]::GetFullPath($BandizipExecutable)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        throw "Bandizip 控制台程序不存在：$candidate"
    }

    $standardPath = 'C:\Program Files\Bandizip\bz.exe'
    if (Test-Path -LiteralPath $standardPath -PathType Leaf) { return $standardPath }
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $install = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like '*Bandizip*' -and $_.InstallLocation } |
        Select-Object -First 1
    if ($null -ne $install) {
        $candidate = Join-Path ([string]$install.InstallLocation) 'bz.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw '没有找到 Bandizip 控制台程序 bz.exe。'
}

function Test-ExcludedPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    if ([string]::Equals([IO.Path]::GetExtension($RelativePath), '.pdb', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $segments = $RelativePath -split '[\\/]'
    for ($index = 0; $index -lt $segments.Count; $index++) {
        if ($segments[$index] -ieq '_download' -or $segments[$index] -ieq '.git') { return $true }
        if ($index + 1 -lt $segments.Count -and $segments[$index] -ieq 'Saved' -and
            ($segments[$index + 1] -ieq 'Logs' -or $segments[$index + 1] -ieq 'Crashes')) { return $true }
    }
    return $false
}

function Test-SameOrChildPath {
    param([string]$Candidate,[string]$Root)
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $candidatePath -ieq $rootPath -or $candidatePath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-LowerSha256 {
    param([Parameter(Mandatory)][string]$Path)
    Assert-NotCancelled
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Json {
    param([Parameter(Mandatory)]$Value,[Parameter(Mandatory)][string]$Path)
    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-AvailableBytes {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
    return [IO.DriveInfo]::new($root).AvailableFreeSpace
}

$completed = $false
$temporaryRoot = $null
try {
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止当前游戏分片任务；已生成的临时内容会保留用于诊断。'
    Write-AxTaskStage -Stage 'inspect' -Message '正在检查游戏包与 Bandizip...'
    $gameRoot = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
    if (!(Test-Path -LiteralPath $gameRoot -PathType Container)) { throw "游戏包目录不存在：$gameRoot" }
    if ([string]::IsNullOrWhiteSpace($GameVersion)) { throw '没有可用的游戏版本。' }
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    if (Test-SameOrChildPath -Candidate $outputRoot -Root $gameRoot) { throw '输出目录不能位于游戏包目录内部。' }
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $bz = Resolve-BandizipConsole

    $allFiles = @(Get-ChildItem -LiteralPath $gameRoot -File -Recurse -Force)
    $includedFiles = @()
    $excludedFiles = @()
    foreach ($file in $allFiles) {
        $relativePath = [IO.Path]::GetRelativePath($gameRoot, $file.FullName)
        if (Test-ExcludedPath $relativePath) { $excludedFiles += $file } else { $includedFiles += $file }
    }
    if ($includedFiles.Count -eq 0) { throw '游戏包目录中没有可打包文件。' }
    $includedBytes = [long](($includedFiles | Measure-Object Length -Sum).Sum)
    $excludedBytes = [long](($excludedFiles | Measure-Object Length -Sum).Sum)
    Write-AxTaskEvent -Data ([ordered]@{
        type='artifact'; kind='inspection'; path=$gameRoot
        detail="保留 $($includedFiles.Count) 个文件 / $includedBytes 字节；排除 $($excludedFiles.Count) 个文件 / $excludedBytes 字节"
    })

    $conflicts = @(
        (Join-Path $outputRoot 'update.json'),
        (Join-Path $outputRoot 'CrossingVoid.zip')) +
        @(Get-ChildItem -LiteralPath $outputRoot -Filter 'CrossingVoid.zip.part*' -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $conflicts = @($conflicts | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
    if ($conflicts.Count -gt 0 -and -not $Overwrite) {
        throw "输出目录已有同名结果，请确认覆盖：$($conflicts -join '; ')"
    }
    foreach ($path in $conflicts) { Remove-Item -LiteralPath $path -Force }

    $spaceReserveBytes = 134217728L
    $requiredOutputBytes = [Math]::Max($spaceReserveBytes, $includedBytes + $spaceReserveBytes)
    $temporaryRoot = Join-Path (Split-Path -Parent $gameRoot) ('.axtools-crossingvoid-' + [Guid]::NewGuid().ToString('N'))
    $stagingRoot = Join-Path $temporaryRoot 'payload'
    $zipPath = Join-Path $temporaryRoot 'CrossingVoid.zip'
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    $outputDrive = [IO.Path]::GetPathRoot($outputRoot)
    $temporaryDrive = [IO.Path]::GetPathRoot($temporaryRoot)
    if ($outputDrive -ieq $temporaryDrive) {
        if ((Get-AvailableBytes $outputRoot) -lt ($requiredOutputBytes * 2)) {
            throw '目标磁盘没有足够空间同时保存临时 ZIP 和输出分片。'
        }
    }
    else {
        if ((Get-AvailableBytes $outputRoot) -lt $requiredOutputBytes) { throw '输出磁盘可用空间不足。' }
        if ((Get-AvailableBytes $temporaryRoot) -lt $requiredOutputBytes) { throw '游戏包所在磁盘没有足够空间生成临时 ZIP。' }
    }

    Write-AxTaskProgress -Stage 'staging' -Percent 8 -Message '正在建立只读硬链接暂存目录...'
    $stagedCount = 0
    foreach ($file in $includedFiles) {
        Assert-NotCancelled
        $relativePath = [IO.Path]::GetRelativePath($gameRoot, $file.FullName)
        if ($relativePath -ieq 'CrossingVoid.version.json' -or $relativePath -ieq 'CrossingVoid.manifest.json') { continue }
        $target = Join-Path $stagingRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        New-Item -ItemType HardLink -Path $target -Target $file.FullName | Out-Null
        $stagedCount++
        if ($stagedCount % 100 -eq 0) {
            $percent = 8 + (12 * $stagedCount / [Math]::Max(1, $includedFiles.Count))
            Write-AxTaskProgress -Stage 'staging' -Percent $percent -Message "正在暂存文件 $stagedCount / $($includedFiles.Count)..."
        }
    }

    $sourceVersionPath = Join-Path $gameRoot 'CrossingVoid.version.json'
    $versionData = [ordered]@{}
    if (Test-Path -LiteralPath $sourceVersionPath -PathType Leaf) {
        try {
            $sourceVersion = Get-Content -LiteralPath $sourceVersionPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
            foreach ($key in $sourceVersion.Keys) { $versionData[$key] = $sourceVersion[$key] }
        } catch { $versionData = [ordered]@{} }
    }
    $versionData.productKey = 'crossingvoid-game'
    $versionData.runtime = 'Windows'
    $versionData.version = $GameVersion.Trim()
    if (-not $versionData.Contains('title') -or [string]::IsNullOrWhiteSpace([string]$versionData.title)) { $versionData.title = $GameVersion.Trim() }
    $versionData.archiveFileName = 'CrossingVoid.zip'
    Write-Utf8Json $versionData (Join-Path $stagingRoot 'CrossingVoid.version.json')

    Write-AxTaskProgress -Stage 'manifest' -Percent 22 -Message '正在生成逐文件 SHA-256 清单...'
    $manifestEntries = @()
    $manifestFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Force | Sort-Object FullName)
    for ($index = 0; $index -lt $manifestFiles.Count; $index++) {
        Assert-NotCancelled
        $file = $manifestFiles[$index]
        $relativePath = [IO.Path]::GetRelativePath($stagingRoot, $file.FullName).Replace('\','/')
        $manifestEntries += [ordered]@{
            path=$relativePath
            sizeBytes=[long]$file.Length
            sha256=(Get-LowerSha256 $file.FullName)
        }
        $percent = 22 + (18 * ($index + 1) / [Math]::Max(1, $manifestFiles.Count))
        Write-AxTaskProgress -Stage 'manifest' -Percent $percent -Message "正在校验文件 $($index + 1) / $($manifestFiles.Count)..." -Detail $relativePath
    }
    $fileManifest = [ordered]@{
        schemaVersion=1
        productKey='crossingvoid-game'
        runtime='Windows'
        version=$GameVersion.Trim()
        title=[string]$versionData.title
        files=$manifestEntries
    }
    Write-Utf8Json $fileManifest (Join-Path $stagingRoot 'CrossingVoid.manifest.json')

    Assert-NotCancelled
    Write-AxTaskProgress -Stage 'compress' -Percent 42 -Message 'Bandizip 正在生成 CrossingVoid.zip...'
    Push-Location $stagingRoot
    try {
        & $bz c -fmt:zip -l:5 -r -y $zipPath '.' 2>&1 | ForEach-Object { Write-Output $_.ToString() }
        $compressExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($compressExitCode -ne 0 -or !(Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "Bandizip 压缩失败，退出码 $compressExitCode。" }
    & $bz t $zipPath 2>&1 | ForEach-Object { Write-Output $_.ToString() }
    if ($LASTEXITCODE -ne 0) { throw "Bandizip 归档校验失败，退出码 $LASTEXITCODE。" }

    Write-AxTaskProgress -Stage 'archive-hash' -Percent 62 -Message '正在校验完整 ZIP...'
    $zipFile = Get-Item -LiteralPath $zipPath
    $zipHash = Get-LowerSha256 $zipPath
    $chunkCount = [int][Math]::Ceiling($zipFile.Length / [double]$ChunkSizeBytes)
    $chunkMetadata = @()
    $input = [IO.File]::OpenRead($zipPath)
    try {
        $buffer = [byte[]]::new([Math]::Min(8388608L, $ChunkSizeBytes))
        for ($chunkIndex = 1; $chunkIndex -le $chunkCount; $chunkIndex++) {
            Assert-NotCancelled
            $fileName = 'CrossingVoid.zip.part{0:D3}' -f $chunkIndex
            $partPath = Join-Path $outputRoot $fileName
            $remaining = [Math]::Min($ChunkSizeBytes, $input.Length - $input.Position)
            $part = [IO.File]::Create($partPath)
            try {
                while ($remaining -gt 0) {
                    Assert-NotCancelled
                    $requested = [int][Math]::Min($buffer.Length, $remaining)
                    $read = $input.Read($buffer, 0, $requested)
                    if ($read -le 0) { throw '读取完整 ZIP 时意外到达文件末尾。' }
                    $part.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            }
            finally { $part.Dispose() }
            $partFile = Get-Item -LiteralPath $partPath
            $chunkMetadata += [ordered]@{
                index=$chunkIndex
                count=$chunkCount
                fileName=$fileName
                objectKey=$fileName
                sha256=(Get-LowerSha256 $partPath)
                sizeBytes=[long]$partFile.Length
                contentType='application/octet-stream'
            }
            $percent = 64 + (28 * $chunkIndex / [Math]::Max(1, $chunkCount))
            Write-AxTaskProgress -Stage 'split' -Percent $percent -Message "已生成分片 $chunkIndex / $chunkCount" -Detail $fileName
        }
    }
    finally { $input.Dispose() }

    $asset = [ordered]@{
        runtime='Windows'
        fileName='CrossingVoid.zip'
        objectKey='CrossingVoid.zip'
        sha256=$zipHash
        sizeBytes=[long]$zipFile.Length
        contentType='application/octet-stream'
        chunks=$chunkMetadata
    }
    $release = [ordered]@{
        version=$GameVersion.Trim()
        title=[string]$versionData.title
        channel='stable'
        publishedAt=[DateTimeOffset]::UtcNow.ToString('o')
        assets=@($asset)
    }
    $updateManifest = [ordered]@{
        schemaVersion=1
        productKey='crossingvoid-game'
        latest=$release
        versions=@($release)
        channels=[ordered]@{ stable=$GameVersion.Trim(); beta=$null }
    }
    Write-Utf8Json $updateManifest (Join-Path $outputRoot 'update.json')
    Write-AxTaskEvent -Data ([ordered]@{ type='artifact'; kind='manifest'; path=(Join-Path $outputRoot 'update.json') })

    Write-AxTaskProgress -Stage 'cleanup' -Percent 96 -Message '正在清理临时完整 ZIP 与暂存目录...'
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    $temporaryRoot = $null
    $completed = $true
    Write-AxTaskProgress -Stage 'completed' -Percent 100 -Message "已生成 $chunkCount 个游戏分片。" -Detail $outputRoot
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "零境交错游戏分片包已生成：$outputRoot"
    exit 0
}
catch [OperationCanceledException] {
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'stopped' -ExitCode 130 -Message $_.Exception.Message
    exit 130
}
catch {
    Write-Output $_.Exception.Message
    if ($null -ne $temporaryRoot) { Write-Output "已保留临时目录：$temporaryRoot" }
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
