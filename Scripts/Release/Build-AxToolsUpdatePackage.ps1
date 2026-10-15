# 出包：dotnet publish 建程序目录 → 压 zip → sha256.txt + toolbox-update.json + file-index.json + artifacts.json 台账。
#
# 与 ZD 工具箱（CrossingVoidZDTool\Scripts\Build-ZDToolboxUpdatePackage.ps1）同构：
# 同一套产物命名、同一套清单字段、同一套增量包规则、同一道 100 MiB 分卷墙，
# 只有"怎么把程序目录造出来"这一段是 AxTools 自己的（dotnet publish + WinUI 资源补齐）。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$StagingDirectory = '',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$Notes = '',

    [switch]$SkipVersionWrite,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$ForceRebuild,
    [int64]$PartSizeBytes = 0,
    [switch]$IncludeDebugArtifacts,
    [switch]$ExcludeAiRuntime,
    [string]$SourceDirectory = '',
    [string]$UpdaterScriptPath = '',
    [string]$PackageManifestFileName = 'update-package.json',
    # 演练：只打印将要做什么，不编译、不写任何文件（连产物目录都不建）。
    [switch]$DryRun,

    # 增量包：默认自动找产物根下"比本版低、且有 file-index.json 或程序目录"的最高版本做基准。
    # 显式给 -DeltaBaseVersion <x.y.z> 可指定基准；-NoDelta 则本版只发整包。
    [string]$DeltaBaseVersion = '',
    [switch]$NoDelta
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($null -ne (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue)) { $PSStyle.OutputRendering = 'PlainText' }
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$ToolboxStableKey = 'AxTools'
$DisplayName = 'AxTools'
$GiteeRepository = 'xiaojie578/AxTools'
$EntryExecutableName = 'AxTools.exe'
$AttachmentLimitBytes = 100MB
$DefaultPartSizeBytes = 90MB
$RuntimeHelperExecutableNames = @('createdump.exe', 'RestartAgent.exe')
$SkipFileNamePatterns = @('*.pdb')
$SkipDirectoryNames = @()
$AiRuntimeFileNames = @('onnxruntime.dll', 'Microsoft.ML.OnnxRuntime.dll', 'DirectML.dll')
$WinUiResourceNames = @('App.xbf', 'MainWindow.xbf', 'AxTools.pri')
$script:ProgressStart = 0
$script:ProgressEnd = 100
$script:StartedAt = Get-Date

function Write-AxReleaseProgress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][double]$Percent,
        [Parameter(Mandatory)][string]$Message,
        [string]$Detail
    )
    $payload = [ordered]@{
        type    = 'progress'
        stage   = $Stage
        percent = [Math]::Max($script:ProgressStart, [Math]::Min($script:ProgressEnd, $Percent))
        message = $Message
    }
    if (![string]::IsNullOrWhiteSpace($Detail)) { $payload.detail = $Detail }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress))
}
function Set-AxCancellationMode {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('stop', 'locked')][string]$Mode, [string]$Message)
    $payload = [ordered]@{ type = 'cancellation'; mode = $Mode }
    if (![string]::IsNullOrWhiteSpace($Message)) { $payload.message = $Message }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress))
}
function Test-AxCancelled {
    return -not [string]::IsNullOrWhiteSpace($env:AXTOOLS_CANCEL_FILE) -and
        (Test-Path -LiteralPath $env:AXTOOLS_CANCEL_FILE -PathType Leaf)
}
function Get-AxNormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
}
function Get-AxSha256Text {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-AxNewestSourceWriteTime {
    # 源码里最新的写入时间。唯一用途：复用旧包时提醒一句"源码比包新"。
    param([Parameter(Mandatory)][string]$Root)
    $normalized = Get-AxNormalizedPath -Path $Root
    $skipDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('.git', '.vs', '.idea', 'bin', 'obj', 'artifacts', '.scratch', 'node_modules')) {
        [void]$skipDirs.Add($name)
    }
    $skipFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('AxToolsReleaseCommon.psm1', 'Build-AxToolsUpdatePackage.ps1', 'AxToolsPublish.ps1',
            'Publish-AxToolsRelease.ps1', 'Publish-AxToolsGiteeRelease.ps1', 'Publish-AxToolsGithubRelease.ps1',
            'Set-AxToolsVersion.ps1', 'Test-AxToolsPackage.ps1')) {
        [void]$skipFiles.Add($name)
    }
    $newest = [DateTime]::MinValue
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($normalized))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in $directory.EnumerateDirectories()) {
            if ($skipDirs.Contains($child.Name)) { continue }
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $pending.Push($child)
        }
        foreach ($file in $directory.EnumerateFiles()) {
            if ($skipFiles.Contains($file.Name)) { continue }
            if ($file.LastWriteTimeUtc -gt $newest) { $newest = $file.LastWriteTimeUtc }
        }
    }
    return $newest
}
function Split-AxUpdateArchive {
    # 把整包 zip 切成若干卷，每卷 ≤ PartSizeBytes，**严格按字节顺序切**：
    # 客户端按卷号顺序拼回来，拼完必须与整包逐字节相同（所有分卷的 sha256 都在 sha256.txt 里）。
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][int64]$PartSizeBytes
    )
    $source = Get-Item -LiteralPath $SourcePath
    $directory = $source.DirectoryName
    $baseName = [IO.Path]::GetFileNameWithoutExtension($FileName)
    $extension = [IO.Path]::GetExtension($FileName)
    $total = [int64]$source.Length
    if ($PartSizeBytes -le 0) { throw '分卷大小必须大于 0。' }
    $partCount = [int][Math]::Ceiling($total / [double]$PartSizeBytes)
    if ($partCount -gt 99) {
        throw ("要切成 $partCount 卷，超过两位编号上限（99）—— 请把 -PartSizeBytes 调大。" +
            "单卷上限 100 MiB，切到 99 卷意味着包已经接近 10 GB，那时该换发布方式了。")
    }
    $results = [Collections.Generic.List[object]]::new()
    $input = [IO.File]::OpenRead($source.FullName)
    try {
        $buffer = [byte[]]::new(4MB)
        $remaining = $total
        for ($index = 1; $index -le $partCount; $index++) {
            $partName = '{0}.part{1:D2}{2}' -f $baseName, $index, $extension
            $partPath = Join-Path $directory $partName
            if (Test-Path -LiteralPath $partPath) { Remove-Item -LiteralPath $partPath -Force }
            $thisPart = [Math]::Min([int64]$PartSizeBytes, $remaining)
            $written = [int64]0
            $sha = [Security.Cryptography.SHA256]::Create()
            $output = [IO.File]::Create($partPath)
            $hashBytes = $null
            try {
                while ($written -lt $thisPart) {
                    $want = [int][Math]::Min([int64]$buffer.Length, ($thisPart - $written))
                    $read = $input.Read($buffer, 0, $want)
                    if ($read -le 0) { throw "读取源文件提前结束（已读 $($total - $remaining + $written) / $total 字节）。" }
                    $output.Write($buffer, 0, $read)
                    [void]$sha.TransformBlock($buffer, 0, $read, $null, 0)
                    $written += $read
                }
                [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
                $hashBytes = $sha.Hash
            }
            finally {
                $output.Dispose()
                $sha.Dispose()
            }
            $remaining -= $written
            $hash = [BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
            $results.Add([pscustomobject]@{
                    FileName  = $partName
                    Path      = $partPath
                    Sha256    = $hash
                    SizeBytes = [int64]$written
                })
        }
    }
    finally { $input.Dispose() }
    $sum = [int64]0
    foreach ($item in $results) { $sum += $item.SizeBytes }
    if ($sum -ne $total) {
        throw "分卷后总字节数对不上：切出 $sum / 原文件 $total。已中止（绝不写出可能拼不回去的卷）。"
    }
    return $results
}
function Get-AxJsonProperty {
    # 安全读一个**可能不存在**的 JSON 字段。
    param(
        [Parameter(Mandatory)][AllowNull()]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Set-AxJsonProperty {
    # 同上：字段可能在旧台账里不存在，直接赋值会抛错，所以缺了就补一个。
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()]$Value
    )
    if ($null -ne $InputObject.PSObject.Properties[$Name]) { $InputObject.$Name = $Value }
    else { $InputObject | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}
function New-AxManifest {
    # 更新清单只有这一处构造：整包出包与"只刷新清单"两条路径共用，避免两边字段漂移。
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$Runtime,
        [AllowEmptyString()][string]$Notes,

        [Parameter(Mandatory)][string]$PublishedAt,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][int64]$SizeBytes,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$Repository,
        [AllowEmptyCollection()][object[]]$Parts = @()
    )
    $asset = [ordered]@{
        runtime     = $Runtime
        fileName    = $FileName
        sha256      = $Sha256
        sizeBytes   = $SizeBytes
        downloadUrl = ''
    }
    if ($Parts.Count -gt 0) { $asset['parts'] = @($Parts) }
    return [ordered]@{
        schemaVersion           = 1
        toolboxStableKey        = $ToolboxStableKey
        displayName             = $DisplayName
        version                 = $Version
        channel                 = $Channel
        publishedAt             = $PublishedAt

        releaseNotesUrl         = "https://gitee.com/$Repository/releases/tag/v$Version"
        releaseNotes            = $Notes
        requiresManualMigration = $false
        requiresRestart         = $true
        assets                  = @($asset)
    }
}
function Write-AxJsonFile {
    # Gitee / 客户端都按 UTF-8 解析；统一「无 BOM + 结尾换行」。
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Data)
    $json = $Data | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::UTF8.GetBytes($json + "`n"))
}
function Read-AxJsonFile {
    # 与 Write-AxJsonFile 配对的那个读。本脚本里必须有一份自己的：
    # 出包脚本单独跑时不会 dot-source 发布脚本，缺一个函数就会在最后一步炸掉。
    param([Parameter(Mandatory)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Write-AxRefreshedManifest {
    # 只按给定参数重写清单与台账：zip / sha256.txt 一律不动。
    param(
        [Parameter(Mandatory)]$Record,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$Runtime,
        [AllowEmptyString()][string]$Notes,

        [Parameter(Mandatory)][string]$ZipFileName,
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RecordPath
    )
    $zipBytes = [int64](Get-Item -LiteralPath $ZipPath).Length
    $sha256 = Get-AxSha256Text -Path $ZipPath
    $manifestText = if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
        [IO.File]::ReadAllText($ManifestPath, [Text.Encoding]::UTF8)
    }
    else { '' }
    $recordText = if (Test-Path -LiteralPath $RecordPath -PathType Leaf) {
        [IO.File]::ReadAllText($RecordPath, [Text.Encoding]::UTF8)
    }
    else { '' }
    $publishedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    $match = [regex]::Match($manifestText, '"publishedAt"\s*:\s*"([^"]+)"')
    if ($match.Success) { $publishedAt = $match.Groups[1].Value }
    $createdAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    $match = [regex]::Match($recordText, '"createdAt"\s*:\s*"([^"]+)"')
    if ($match.Success) { $createdAt = $match.Groups[1].Value }
    $recordParts = @(Get-AxJsonProperty -InputObject $Record -Name 'parts')
    $manifest = New-AxManifest -Version $Version -Channel $Channel -Runtime $Runtime -Notes $Notes `
        -PublishedAt $publishedAt -Sha256 $sha256 -SizeBytes $zipBytes `
        -FileName $ZipFileName -Repository $Repository `
        -Parts @($recordParts | ForEach-Object {
            [ordered]@{ fileName = $_.fileName; sha256 = $_.sha256; sizeBytes = [int64]$_.sizeBytes }
        })
    Write-AxJsonFile -Path $ManifestPath -Data $manifest

    Set-AxJsonProperty -InputObject $Record -Name 'notes' -Value $Notes
    Set-AxJsonProperty -InputObject $Record -Name 'manifestPath' -Value $ManifestPath
    Set-AxJsonProperty -InputObject $Record -Name 'createdAt' -Value $createdAt
    Write-AxJsonFile -Path $RecordPath -Data $Record
    return [pscustomobject]@{ ZipBytes = $zipBytes; Sha256 = $sha256; ManifestPath = $ManifestPath }
}
function Test-AxUtf8Bom {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 3) { return $false }
        $head = [byte[]]::new(3)
        [void]$stream.Read($head, 0, 3)
        return ($head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF)
    }
    finally { $stream.Dispose() }
}
function ConvertTo-AxUtf8BomFile {
    param([Parameter(Mandatory)][string]$Path)
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
}
function Get-AxPackageFiles {
    # 按「要进包的文件」枚举，顺手把下架的类别筛掉。
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][bool]$KeepDebugArtifacts,
        [bool]$ExcludeAiRuntime = $false
    )
    $normalized = Get-AxNormalizedPath -Path $Root
    $skipDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (!$KeepDebugArtifacts) { foreach ($name in $SkipDirectoryNames) { [void]$skipDirs.Add($name) } }
    $skipNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($ExcludeAiRuntime -and !$KeepDebugArtifacts) {
        foreach ($name in $AiRuntimeFileNames) { [void]$skipNames.Add($name) }
    }
    $results = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($normalized))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in $directory.EnumerateDirectories()) {
            if ($skipDirs.Contains($child.Name)) { continue }
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $pending.Push($child)
        }
        foreach ($file in $directory.EnumerateFiles()) {
            if ($skipNames.Contains($file.Name)) { continue }
            if (!$KeepDebugArtifacts) {
                $skip = $false
                foreach ($pattern in $SkipFileNamePatterns) {
                    if ($file.Name -like $pattern) { $skip = $true; break }
                }
                if ($skip) { continue }
            }
            $results.Add($file)
        }
    }
    return ($results | Sort-Object FullName)
}
function Get-AxEntryExecutable {
    # 入口 exe：客户端与覆盖器都按 update-package.json 里的 entryExe 启动，
    # 所以先认死名字，认不出来才退回"体积最大的非运行时辅助 exe"。
    param([Parameter(Mandatory)][string]$ProgramDirectory)
    $named = Join-Path $ProgramDirectory $EntryExecutableName
    if (Test-Path -LiteralPath $named -PathType Leaf) { return (Get-Item -LiteralPath $named) }
    $candidates = @(Get-ChildItem -LiteralPath $ProgramDirectory -Filter '*.exe' -File |
        Where-Object { $RuntimeHelperExecutableNames -notcontains $_.Name } |
        Sort-Object Length -Descending)
    if ($candidates.Count -eq 0) {
        throw "程序目录里找不到可执行文件：$ProgramDirectory"
    }
    return $candidates[0]
}
function New-AxZipArchive {
    # Bandizip 优先（-l:9 最高压缩，直接决定离 100 MiB 硬墙有多远），失败退到 .NET ZipFile。
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string[]]$Entries
    )
    $bandizip = @(
        'C:\Program Files\Bandizip\bz.exe',
        'C:\Program Files (x86)\Bandizip\bz.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($bandizip)) {
        $command = Get-Command 'bz.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) { $bandizip = $command.Source }
    }
    $root = Get-AxNormalizedPath -Path $SourceDirectory
    if (![string]::IsNullOrWhiteSpace($bandizip)) {
        Write-AxReleaseProgress -Stage 'compress' -Percent 68 -Message '正在用 Bandizip 最高压缩打包（体积直接撞 100 MiB 硬墙）' -Detail $bandizip
        $stage = Join-Path (Split-Path -Parent $ArchivePath) ('.axzip-stage-' + [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $stage -Force | Out-Null
            $linked = 0
            foreach ($entry in $Entries) {
                $full = Get-AxNormalizedPath -Path $entry
                $relative = $full.Substring($root.Length).TrimStart('\')
                $destination = Join-Path $stage $relative
                $directory = Split-Path -Parent $destination
                if (!(Test-Path -LiteralPath $directory)) {
                    New-Item -ItemType Directory -Path $directory -Force | Out-Null
                }
                New-Item -ItemType HardLink -Path $destination -Target $full -ErrorAction SilentlyContinue | Out-Null
                if (!(Test-Path -LiteralPath $destination)) {
                    Copy-Item -LiteralPath $full -Destination $destination -Force
                }
                $linked++
            }
            Write-Host "  已用硬链接摆好 $linked 个进包文件（staging：$stage）"
            & $bandizip c -fmt:zip -l:9 -r -y $ArchivePath "$stage\*" | ForEach-Object { Write-Host $_ }
            $bandizipExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($bandizipExit -eq 0 -and (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
                return 'Bandizip'
            }
            Write-Host "Bandizip 打包失败（exit $bandizipExit），改用 .NET ZipFile 兜底。" -ForegroundColor Yellow
            if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
        }
        finally {
            if (Test-Path -LiteralPath $stage) {
                Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        Write-Host '没有找到 Bandizip，改用 .NET ZipFile（压缩率略差）。' -ForegroundColor Yellow
    }
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue
    if (!('System.IO.Compression.ZipArchiveMode' -as [type])) {
        throw '无法加载 System.IO.Compression（.NET ZipFile 兜底路径不可用）。请安装 Bandizip 后重试。'
    }
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Entries) {
            $full = Get-AxNormalizedPath -Path $entry
            $relative = $full.Substring($root.Length).TrimStart('\')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $full,
                $relative,
                [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    return '.NET ZipFile'
}
function Get-AxFileIndex {
    # 目录 → 文件索引（相对路径 → 长度 + SHA-256）。增量包的一切都建立在这份索引上：
    # 下一版出包时拿它和新目录逐文件比，只把"变化/新增"的文件打进增量包，并算出"要删除"的清单。
    # 路径统一转小写做键（Windows 不区分大小写），输出时用原始大小写。
    param(
        [Parameter(Mandatory)][string]$Root,
        [object[]]$Files = @(),
        # 必须和打包时用的是同一组开关：索引里多出 .pdb 之类，增量包就会把"整包本来不发"的文件当成变化项发出去。
        [bool]$KeepDebugArtifacts = $false,
        [bool]$ExcludeAiRuntime = $false
    )
    $normalized = Get-AxNormalizedPath -Path $Root
    if ($Files.Count -eq 0) {
        $Files = @(Get-AxPackageFiles -Root $normalized -KeepDebugArtifacts $KeepDebugArtifacts -ExcludeAiRuntime $ExcludeAiRuntime)
    }
    $index = [ordered]@{}
    $lower = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        $relative = $file.FullName.Substring($normalized.Length).TrimStart('\')
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        if (!$lower.Add($relative)) { continue }
        $index[$relative] = [ordered]@{
            sizeBytes = [int64]$file.Length
            sha256    = (Get-AxSha256Text -Path $file.FullName)
        }
    }
    return $index
}
function Get-AxVersionProgramDirectory {
    # 在产物根下找某一版的程序目录（含 update-package.json 的那一层）。
    param([Parameter(Mandatory)][string]$ArtifactsRoot, [Parameter(Mandatory)][string]$Version)
    $candidate = Join-Path $ArtifactsRoot ('v' + $Version)
    if (!(Test-Path -LiteralPath $candidate -PathType Container)) { return $null }
    $manifest = @(Get-ChildItem -LiteralPath $candidate -Recurse -File -Filter 'update-package.json' -ErrorAction SilentlyContinue)
    if ($manifest.Count -eq 0) { return $null }
    return $manifest[0].DirectoryName
}
function Get-AxVersionFileIndex {
    # 拿某一版的文件索引：优先用出包时留下的 file-index.json（几毫秒），
    # 没有就从那一版还留着的程序目录现算（不依赖旧 zip，索引文件才是长期依据）。
    param(
        [Parameter(Mandatory)][string]$ArtifactsRoot,
        [Parameter(Mandatory)][string]$Version,
        [bool]$KeepDebugArtifacts = $false,
        [bool]$ExcludeAiRuntime = $false
    )
    $versionDir = Join-Path $ArtifactsRoot ('v' + $Version)
    $indexPath = Join-Path $versionDir 'file-index.json'
    if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
        $data = Read-AxJsonFile -Path $indexPath
        $index = [ordered]@{}
        foreach ($property in $data.files.PSObject.Properties) {
            $index[$property.Name] = [ordered]@{
                sizeBytes = [int64]$property.Value.sizeBytes
                sha256    = [string]$property.Value.sha256
            }
        }
        return [pscustomobject]@{ Index = $index; Source = $indexPath; ProgramDirectory = [string]$data.programDirectory }
    }
    $programDirectory = Get-AxVersionProgramDirectory -ArtifactsRoot $ArtifactsRoot -Version $Version
    if ([string]::IsNullOrWhiteSpace($programDirectory)) { return $null }
    return [pscustomobject]@{
        Index            = (Get-AxFileIndex -Root $programDirectory)
        Source           = $programDirectory
        ProgramDirectory = $programDirectory
    }
}
function Find-AxPreviousVersion {
    # 产物根下"比目标版本低、且有程序目录或索引"的最高版本 —— 增量包的基准版。
    param([Parameter(Mandatory)][string]$ArtifactsRoot, [Parameter(Mandatory)][string]$TargetVersion)
    if (!(Test-Path -LiteralPath $ArtifactsRoot -PathType Container)) { return $null }
    $target = [version]($TargetVersion -replace '-.*$', '')
    $best = $null
    $bestVersion = $null
    foreach ($directory in (Get-ChildItem -LiteralPath $ArtifactsRoot -Directory -ErrorAction SilentlyContinue)) {
        if ($directory.Name -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
        $candidate = $Matches[1]
        $parsed = $null
        if (![version]::TryParse($candidate, [ref]$parsed)) { continue }
        if ($parsed -ge $target) { continue }
        $hasIndex = Test-Path -LiteralPath (Join-Path $directory.FullName 'file-index.json') -PathType Leaf
        $hasProgram = $null -ne (Get-AxVersionProgramDirectory -ArtifactsRoot $ArtifactsRoot -Version $candidate)
        if (!$hasIndex -and !$hasProgram) { continue }
        if ($null -eq $bestVersion -or $parsed -gt $bestVersion) {
            $bestVersion = $parsed
            $best = $candidate
        }
    }
    return $best
}
function New-AxDeltaPackage {
    # 相对基础版产出增量包：只装"新增/变化"的文件，外加一份显式删除清单。
    # 包内 update-package.json 带 delta 段（baseVersion / files[] / delete[]），
    # 覆盖器据此逐文件核对 baseSha256 —— 对不上就拒绝，绝不混装。
    param(
        [Parameter(Mandatory)][string]$ProgramDirectory,
        [Parameter(Mandatory)]$PreviousIndex,
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$ManifestFileName,
        [AllowEmptyString()][string]$WindowsAppSdkRuntime = '',
        [Parameter(Mandatory)][int64]$FullPackageBytes,
        [int]$MaxRatioPercent = 70,
        [bool]$KeepDebugArtifacts = $false,
        [bool]$ExcludeAiRuntime = $false,
        [switch]$Force
    )
    $normalized = Get-AxNormalizedPath -Path $ProgramDirectory
    $files = @(Get-AxPackageFiles -Root $normalized -KeepDebugArtifacts $KeepDebugArtifacts -ExcludeAiRuntime $ExcludeAiRuntime)
    $current = [ordered]@{}
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($normalized.Length).TrimStart('\')
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        if ($relative -eq $ManifestFileName) { continue }
        $current[$relative] = $file
    }

    $changed = [Collections.Generic.List[object]]::new()
    $removed = [Collections.Generic.List[string]]::new()
    $previousKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $PreviousIndex.Keys) { [void]$previousKeys.Add([string]$key) }

    foreach ($relative in $current.Keys) {
        $file = $current[$relative]
        $hash = Get-AxSha256Text -Path $file.FullName
        $baseHash = $null
        if ($PreviousIndex.Contains($relative)) {
            $entry = $PreviousIndex[$relative]
            if (([int64]$entry.sizeBytes -eq [int64]$file.Length) -and ([string]$entry.sha256 -eq $hash)) {
                continue
            }
            $baseHash = [string]$entry.sha256
        }
        $changed.Add([pscustomobject]@{
                RelativePath = $relative
                FullName     = $file.FullName
                SizeBytes    = [int64]$file.Length
                Sha256       = $hash
                BaseSha256   = $baseHash
            })
    }
    foreach ($key in $PreviousIndex.Keys) {
        # 体检单自身不进删除清单（它只在"包"里有意义；装到盘上那份留着给排查的人看，不影响任何逻辑）。
        if ([string]$key -eq $ManifestFileName) { continue }
        if (!$current.Contains([string]$key)) { $removed.Add([string]$key) }
    }

    if ($changed.Count -eq 0 -and $removed.Count -eq 0) {
        Write-Host '增量包：本版与基础版逐文件相同，没有增量包（客户端直接复用同内容的整包）。'
        return $null
    }

    $rawDeltaBytes = ($changed | Measure-Object -Property SizeBytes -Sum).Sum
    $estimated = [int64]($rawDeltaBytes * 0.35)
    if (!$Force -and $FullPackageBytes -gt 0 -and ($estimated * 100) -gt ($FullPackageBytes * $MaxRatioPercent)) {
        Write-Host ("增量包估算 {0:N1} MB 超过整包的 {1}%（{2:N1} MB），跳过增量 —— 直接发整包更省事。" -f `
                ($estimated / 1MB), $MaxRatioPercent, ($FullPackageBytes / 1MB)) -ForegroundColor Yellow
        return $null
    }

    $staging = Join-Path (Split-Path -Parent $OutputPath) ('.ax-delta-stage-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void][IO.Directory]::CreateDirectory($staging)
        foreach ($item in $changed) {
            $destination = Join-Path $staging $item.RelativePath
            $directory = Split-Path -Parent $destination
            if (!(Test-Path -LiteralPath $directory)) { [void][IO.Directory]::CreateDirectory($directory) }
            Copy-Item -LiteralPath $item.FullName -Destination $destination -Force
        }

        $deltaManifest = [ordered]@{
            schemaVersion        = 1
            toolboxStableKey     = $ToolboxStableKey
            version              = $Version
            runtime              = $Runtime
            entryExe             = (Get-AxEntryExecutable -ProgramDirectory $normalized).Name
            windowsAppSdkRuntime = $WindowsAppSdkRuntime
            delta                = $true
            baseVersion          = $BaseVersion
            createdAt            = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
            files                = @($changed | ForEach-Object {
                    $entry = [ordered]@{
                        path      = $_.RelativePath
                        sizeBytes = $_.SizeBytes
                        sha256    = $_.Sha256
                    }
                    if (![string]::IsNullOrWhiteSpace($_.BaseSha256)) { $entry['baseSha256'] = $_.BaseSha256 }
                    $entry
                })
            delete               = @($removed)
        }
        Write-AxJsonFile -Path (Join-Path $staging $ManifestFileName) -Data $deltaManifest

        $entries = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object { $_.FullName })
        if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
        [void](New-AxZipArchive -SourceDirectory $staging -ArchivePath $OutputPath -Entries $entries)
        $deltaBytes = (Get-Item -LiteralPath $OutputPath).Length
        $saved = if ($FullPackageBytes -gt 0) { 100 - [int](($deltaBytes * 100) / $FullPackageBytes) } else { 0 }
        Write-Host ("增量包：{0} → {1}，变化 {2} 个 / 删除 {3} 个，{4:N2} MB（整包 {5:N2} MB，省 {6}%）" -f `
                $BaseVersion, $Version, $changed.Count, $removed.Count, ($deltaBytes / 1MB), ($FullPackageBytes / 1MB), $saved) -ForegroundColor Green
        return [pscustomobject]@{
            Path         = $OutputPath
            FileName     = (Split-Path -Leaf $OutputPath)
            SizeBytes    = [int64]$deltaBytes
            Sha256       = (Get-AxSha256Text -Path $OutputPath)
            BaseVersion  = $BaseVersion
            ChangedCount = $changed.Count
            DeleteCount  = $removed.Count
        }
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
function Update-AxDeltaArtifacts {
    # 增量包 + 本版文件索引。全量出包与"复用已有包"两条路径**都必须调用**：
    # 复用路径会重生成清单（Write-AxRefreshedManifest），不在这里补 delta 段的话，
    # 一次重发就会把增量段抹掉（而且客户端与发布库读 $manifest.delta 都会翻车）。
    param(
        [Parameter(Mandatory)][string]$ProgramDirectory,
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$TargetVersion,
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$ManifestFileName,
        [Parameter(Mandatory)][string]$ZipFileName,
        [Parameter(Mandatory)][int64]$FullPackageBytes,
        [AllowEmptyString()][string]$ProjectRoot = '',
        [string]$DeltaBaseVersion = '',
        [string]$RecordPath = '',
        [switch]$NoDelta,
        [bool]$KeepDebugArtifacts = $false,
        [bool]$ExcludeAiRuntime = $false
    )

    # 程序目录没了（有人手工清了产物、或只留了三件产物）：不做增量、不写索引，
    # 但**不能**让发布失败 —— 整包该发照发。
    if (!(Test-Path -LiteralPath $ProgramDirectory -PathType Container)) {
        Write-Host ('增量包：程序目录不在了（' + $ProgramDirectory + '），本版只发整包。') -ForegroundColor Yellow
        return [pscustomobject]@{ Delta = $null; IndexPath = '' }
    }

    # ① 本版文件索引（留给下一次出包算增量；不依赖旧 zip 是否还在）
    $indexPath = Join-Path $StagingDirectory 'file-index.json'
    $indexFiles = @(Get-AxPackageFiles -Root $ProgramDirectory -KeepDebugArtifacts $KeepDebugArtifacts -ExcludeAiRuntime $ExcludeAiRuntime)
    $index = Get-AxFileIndex -Root $ProgramDirectory -Files $indexFiles `
        -KeepDebugArtifacts $KeepDebugArtifacts -ExcludeAiRuntime $ExcludeAiRuntime
    Write-AxJsonFile -Path $indexPath -Data ([ordered]@{
            schemaVersion    = 1
            toolboxStableKey = $ToolboxStableKey
            version          = $TargetVersion
            runtime          = $Runtime
            programDirectory = $ProgramDirectory
            fileCount        = $index.Count
            createdAt        = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
            files            = $index
        })
    Write-Host ("文件索引：$indexPath（$($index.Count) 个文件）")

    # ② 增量包（相对上一版）
    $deltaRecord = $null
    if (!$NoDelta) {
        $artifactsRoot = Split-Path -Parent $StagingDirectory
        $baseVersion = $DeltaBaseVersion
        if ([string]::IsNullOrWhiteSpace($baseVersion)) {
            $baseVersion = Find-AxPreviousVersion -ArtifactsRoot $artifactsRoot -TargetVersion $TargetVersion
        }
        if ([string]::IsNullOrWhiteSpace($baseVersion)) {
            Write-Host '增量包：产物根下找不到可用的上一版（没有 file-index.json 或程序目录），本版只发整包。' -ForegroundColor Yellow
        }
        else {
            $previous = Get-AxVersionFileIndex -ArtifactsRoot $artifactsRoot -Version $baseVersion `
                -KeepDebugArtifacts $KeepDebugArtifacts -ExcludeAiRuntime $ExcludeAiRuntime
            if ($null -eq $previous) {
                Write-Host ("增量包：v$baseVersion 的索引拿不到，本版只发整包。") -ForegroundColor Yellow
            }
            else {
                Write-AxReleaseProgress -Stage 'delta' -Percent 92 -Message "正在生成增量包（基准 v$baseVersion）"
                Write-Host ("增量基准：v$baseVersion（索引来源：$($previous.Source)）")
                # 不能在这里读清单取整包名：全量出包路径调用本函数时清单刚刚落盘，复用路径里清单可能刚被重写过，
                # 所以整包名由调用方传进来。
                $zipBaseName = [IO.Path]::GetFileNameWithoutExtension($ZipFileName)
                $deltaPath = Join-Path $StagingDirectory ($zipBaseName + "-delta-from-v$baseVersion.zip")
                $wasdkRuntime = Get-AxWindowsAppSdkRuntimeRequirement -ProgramDirectory $ProgramDirectory -ProjectRoot $ProjectRoot
                $deltaRecord = New-AxDeltaPackage `
                    -ProgramDirectory $ProgramDirectory `
                    -PreviousIndex $previous.Index `
                    -BaseVersion $baseVersion `
                    -Version $TargetVersion `
                    -Runtime $Runtime `
                    -OutputPath $deltaPath `
                    -ManifestFileName $ManifestFileName `
                    -WindowsAppSdkRuntime $wasdkRuntime `
                    -FullPackageBytes $FullPackageBytes `
                    -KeepDebugArtifacts $KeepDebugArtifacts `
                    -ExcludeAiRuntime $ExcludeAiRuntime
            }
        }
    }

    # ③ 把 delta 段写进清单（老客户端不认这个字段，直接忽略走整包）；
    #    顺带：**公告为空时补一句自动说明** —— 否则 Release 页与客户端都显示「本次更新未填写公告。」
    $manifest = Read-AxJsonFile -Path $ManifestPath
    $manifestChanged = $false
    if ($null -ne $deltaRecord) {
        $manifest | Add-Member -NotePropertyName delta -NotePropertyValue ([ordered]@{
                baseVersion  = $deltaRecord.BaseVersion
                fileName     = $deltaRecord.FileName
                sha256       = $deltaRecord.Sha256
                sizeBytes    = $deltaRecord.SizeBytes
                changedCount = $deltaRecord.ChangedCount
                deleteCount  = $deltaRecord.DeleteCount
            }) -Force
        $manifestChanged = $true
        Write-Host ("清单已带 delta 段：$($deltaRecord.FileName)（{0:N2} MB）" -f ($deltaRecord.SizeBytes / 1MB))
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.releaseNotes)) {
        $packageText = if ($null -ne $deltaRecord) {
            ('增量包 {0:N2} MB（变化 {1} 个文件 / 删除 {2} 个）' -f ($deltaRecord.SizeBytes / 1MB), $deltaRecord.ChangedCount, $deltaRecord.DeleteCount)
        }
        else {
            '完整包'
        }
        $note = ('本次更新：{0}；完整包 {1:N2} MB。' -f $packageText, ($FullPackageBytes / 1MB))
        $manifest | Add-Member -NotePropertyName releaseNotes -NotePropertyValue $note -Force
        $manifestChanged = $true
        Write-Host ("清单公告为空，已自动写入：$note")
    }
    if ($manifestChanged) { Write-AxJsonFile -Path $ManifestPath -Data $manifest }

    # ④ 顺手把台账补上（复用路径也会走到这里）
    if (![string]::IsNullOrWhiteSpace($RecordPath) -and (Test-Path -LiteralPath $RecordPath -PathType Leaf)) {
        try {
            $record = Read-AxJsonFile -Path $RecordPath
            $record | Add-Member -NotePropertyName fileIndexPath -NotePropertyValue $indexPath -Force
            $deltaValue = $null
            if ($null -ne $deltaRecord) {
                $deltaValue = [ordered]@{
                    baseVersion  = $deltaRecord.BaseVersion
                    fileName     = $deltaRecord.FileName
                    path         = $deltaRecord.Path
                    sha256       = $deltaRecord.Sha256
                    sizeBytes    = $deltaRecord.SizeBytes
                    changedCount = $deltaRecord.ChangedCount
                    deleteCount  = $deltaRecord.DeleteCount
                }
            }
            $record | Add-Member -NotePropertyName delta -NotePropertyValue $deltaValue -Force
            Write-AxJsonFile -Path $RecordPath -Data $record
        }
        catch {
            Write-Host ('台账里没能补上增量记录（不影响发布）：' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    return [pscustomobject]@{ Delta = $deltaRecord; IndexPath = $indexPath }
}
function Get-AxWindowsAppSdkRuntimeRequirement {
    # 框架依赖 ⇒ 返回要写进体检单的 major.minor（例如 "2.3"）；自包含 ⇒ 空串。
    # 判据取自程序目录本身：有投影 Microsoft.WinUI.dll 但没有原生 Microsoft.ui.xaml.dll。
    # AxTools 目前是 --self-contained + WindowsAppSDKSelfContained=true，所以恒为空串；
    # 这个字段留着是因为覆盖器要按它决定"升级前要不要先确认运行时在不在"。
    param(
        [Parameter(Mandatory)][string]$ProgramDirectory,
        [AllowEmptyString()][string]$ProjectRoot = ''
    )
    if (!(Test-Path -LiteralPath (Join-Path $ProgramDirectory 'Microsoft.WinUI.dll') -PathType Leaf)) { return '' }
    if (Test-Path -LiteralPath (Join-Path $ProgramDirectory 'Microsoft.ui.xaml.dll') -PathType Leaf) { return '' }
    $projectFile = if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { '' } else { Join-Path $ProjectRoot 'AxTools.csproj' }
    return (Get-AxWindowsAppSdkRelease -ProjectFile $projectFile)
}
function Get-AxWindowsAppSdkRelease {
    # 从 csproj 的 `PackageReference Microsoft.WindowsAppSDK Version="2.3.1"` 推 major.minor（→ "2.3"）。
    param([string]$ProjectFile)
    $fallback = '2.3'
    if ([string]::IsNullOrWhiteSpace($ProjectFile) -or !(Test-Path -LiteralPath $ProjectFile -PathType Leaf)) { return $fallback }
    $text = [IO.File]::ReadAllText($ProjectFile, [Text.Encoding]::UTF8)
    $m = [regex]::Match($text, 'Microsoft\.WindowsAppSDK"\s+Version="(\d+\.\d+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $fallback
}
function Write-AxPackageManifest {
    # 包内体检单。客户端校验与覆盖器都直接依赖 entryExe：
    param(
        [Parameter(Mandatory)][string]$ProgramDirectory,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$FileName
    )
    $entry = Get-AxEntryExecutable -ProgramDirectory $ProgramDirectory
    if ($entry.Length -le 0) {
        throw "入口程序体积为 0，客户端会判为坏包：$($entry.FullName)"
    }
    $path = Join-Path $ProgramDirectory $FileName
    $wasdkRuntime = Get-AxWindowsAppSdkRuntimeRequirement -ProgramDirectory $ProgramDirectory -ProjectRoot $root
    $record = [ordered]@{
        schemaVersion        = 1
        toolboxStableKey     = $ToolboxStableKey
        version              = $Version
        runtime              = $Runtime
        entryExe             = $entry.Name
        windowsAppSdkRuntime = $wasdkRuntime
        createdAt            = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    }
    Write-AxJsonFile -Path $path -Data $record
    Write-Host "包内体检单：$FileName（entryExe=$($entry.Name)，$([Math]::Round($entry.Length / 1MB, 2)) MB）"
    return $entry
}
function Assert-AxPackageStructure {
    # 压包**之前**把客户端真正依赖的项点一遍：
    param(
        [Parameter(Mandatory)][string]$ProgramDirectory,
        [Parameter(Mandatory)][string]$PackageManifestFileName,
        [Parameter(Mandatory)][string]$UpdaterRelativePath
    )
    $required = @($PackageManifestFileName, $EntryExecutableName) + $WinUiResourceNames + @($UpdaterRelativePath)
    $missing = @($required | Where-Object { !(Test-Path -LiteralPath (Join-Path $ProgramDirectory $_) -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw "更新包缺少必需内容（客户端或更新器会拒绝）：`n  - " + ($missing -join "`n  - ")
    }
    $nested = Join-Path $ProgramDirectory (Split-Path -Leaf $ProgramDirectory)
    if (Test-Path -LiteralPath $nested -PathType Container) {
        throw "程序目录里又套了一层自身目录（zip 根必须就是程序目录）：$nested"
    }
}

$exitCode = 0
try {
    $root = Get-AxNormalizedPath -Path $ProjectRoot
    $projectFile = Join-Path $root 'AxTools.csproj'
    if (!(Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "找不到项目文件：$projectFile"
    }
    $targetVersion = $Version.Trim()
    if ($targetVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?$') {
        throw "版本号必须形如 1.0.1 或 1.0.1-Beta：$targetVersion"
    }
    $isBeta = $targetVersion.Contains('-')
    $channel = if ($isBeta) { 'beta' } else { 'stable' }
    $staging = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
        Join-Path $root (Join-Path 'Artifacts\ReleaseAssets' "v$targetVersion")
    }
    else { Get-AxNormalizedPath -Path $StagingDirectory }
    $zipFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.zip"
    $shaFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.sha256.txt"
    $manifestPath = Join-Path $staging 'toolbox-update.json'
    $recordPath = Join-Path $staging 'artifacts.json'
    $zipPath = Join-Path $staging $zipFileName
    $shaPath = Join-Path $staging $shaFileName
    $effectivePartSize = if ($PartSizeBytes -gt 0) { [int64]$PartSizeBytes } else { [int64]$DefaultPartSizeBytes }

    # 构建工作目录固定落在统一工作区输出根下的版本目录里（不写进源码树、也不写进临时目录）。
    $workRoot = Join-Path $staging ".work\$targetVersion"
    $publishRoot = Join-Path $workRoot $Runtime
    $binLayout = Join-Path $root 'bin\x64\Release\net8.0-windows10.0.19041.0'

    $updaterRelative = 'Scripts\Release\Update-AxTools.ps1'
    $updaterSource = if ([string]::IsNullOrWhiteSpace($UpdaterScriptPath)) {
        Join-Path $root $updaterRelative
    }
    else { Get-AxNormalizedPath -Path $UpdaterScriptPath }
    if (!(Test-Path -LiteralPath $updaterSource -PathType Leaf)) {
        throw ("找不到客户端更新器脚本：$updaterSource`n" +
            "  zip 内必须带 $updaterRelative —— 客户端就是靠它覆盖程序目录的。")
    }

    if ($DryRun) {
        # 演练：一个字节都不写，只把真实出包会做的事说清楚。
        $deltaBase = ''
        if (!$NoDelta) {
            $deltaBase = if (![string]::IsNullOrWhiteSpace($DeltaBaseVersion)) { $DeltaBaseVersion }
            else { [string](Find-AxPreviousVersion -ArtifactsRoot (Split-Path -Parent $staging) -TargetVersion $targetVersion) }
        }
        Write-Host "== 出包演练 $DisplayName v$targetVersion（$Runtime，通道 $channel）==" -ForegroundColor Cyan
        Write-Host "   产物目录：$staging（演练不创建）"
        Write-Host '真实出包会执行 ——' -ForegroundColor Yellow
        Write-Host ("  1. 版本号写入 AxTools.csproj：$targetVersion" + $(if ($SkipVersionWrite) { '（已用 -SkipVersionWrite 跳过）' } else { '' }))
        if ($SkipBuild) {
            Write-Host "  2. 复用已有程序目录（-SkipBuild）：$publishRoot"
        }
        elseif (![string]::IsNullOrWhiteSpace($SourceDirectory)) {
            Write-Host "  2. 用指定源目录当程序目录（不编译）：$(Get-AxNormalizedPath -Path $SourceDirectory)"
        }
        else {
            Write-Host "  2. dotnet test（Release）→ dotnet publish（Release x64 自包含）→ $publishRoot"
        }
        Write-Host "  3. 压包（Bandizip 最高压缩）→ $zipPath"
        Write-Host ("  4. 超过 100 MiB 自动分卷（每卷 ≤{0:N0} MB）" -f ($effectivePartSize / 1MB))
        Write-Host '  5. 写 sha256.txt / toolbox-update.json / file-index.json / artifacts.json'
        Write-Host $(if ([string]::IsNullOrWhiteSpace($deltaBase)) { '  6. 增量包：没有可用基准，本版只发整包' }
            else { "  6. 增量包：基准 v$deltaBase" })
        Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message "出包演练完成：$zipFileName（未写任何文件）"
        exit 0
    }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Set-AxCancellationMode -Mode 'stop' -Message '出包阶段可停止；停止后已完成的构建产物会保留。'
    if ($PartSizeBytes -gt 0 -and !$ForceRebuild) {
        Write-Host '指定了 -PartSizeBytes：本次不复用已有产物，按要求的卷大小重切。' -ForegroundColor DarkGray
    }
    if (!$ForceRebuild -and $PartSizeBytes -le 0 -and [string]::IsNullOrWhiteSpace($SourceDirectory)) {
        # 复用已有产物：台账、zip、三件产物、分卷全都核对一遍，任一不符就转为重新构建。
        $existingRecord = $null
        if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
            try {
                $existingRecord = [IO.File]::ReadAllText($recordPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            }
            catch { $existingRecord = $null }
        }
        $reuseProblem = $null
        if ($null -eq $existingRecord) { $reuseProblem = '没有产物台账' }
        elseif ([string]$existingRecord.version -ne $targetVersion) {
            $reuseProblem = "台账版本 $($existingRecord.version) 与目标 $targetVersion 不一致"
        }
        elseif (!(Test-Path -LiteralPath $zipPath -PathType Leaf) -or
            !(Test-Path -LiteralPath $shaPath -PathType Leaf) -or
            !(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            $reuseProblem = '三件产物不全'
        }
        else {
            $zipBytes = [int64](Get-Item -LiteralPath $zipPath).Length
            if ([int64]$existingRecord.sizeBytes -ne $zipBytes -or
                ![string]::Equals([string]$existingRecord.sha256, (Get-AxSha256Text -Path $zipPath), [StringComparison]::OrdinalIgnoreCase)) {
                $reuseProblem = 'zip 与台账对不上（大小/哈希不一致）'
            }
            else {
                foreach ($part in @(Get-AxJsonProperty -InputObject $existingRecord -Name 'parts')) {
                    if (!(Test-Path -LiteralPath (Join-Path $staging ([string]$part.fileName)) -PathType Leaf)) {
                        $reuseProblem = "台账里的分卷不在产物目录里：$($part.fileName)"
                        break
                    }
                }
            }
        }
        if ($null -eq $reuseProblem) {
            [void](Write-AxRefreshedManifest -Record $existingRecord -Version $targetVersion `
                    -Channel $channel -Runtime $Runtime -Notes $Notes `
                    -ZipFileName $zipFileName -ZipPath $zipPath -Repository $GiteeRepository `
                    -ManifestPath $manifestPath -RecordPath $recordPath)
            Write-Host "复用已有更新包（台账校验通过），清单已重生成：$zipFileName（$([Math]::Round($zipBytes / 1MB, 2)) MB）" -ForegroundColor Green
            # 复用路径也要产出增量包与索引：否则重发一次，清单里的 delta 段就没了。
            # ★ 索引必须按"当初打包时那组开关"重算：台账里记着 keepDebugArtifacts / excludeAiRuntime。
            #   若改用本次命令行的开关，索引就会与包的实际内容脱节 —— 下次增量会把包里明明有的文件
            #   列进删除表（实测：同一版先带 -ExcludeAiRuntime 打包、再不带它复用，删除项从 1 变 2）。
            $recordKeepDebug = Get-AxJsonProperty -InputObject $existingRecord -Name 'keepDebugArtifacts'
            $recordExcludeAi = Get-AxJsonProperty -InputObject $existingRecord -Name 'excludeAiRuntime'
            if ($null -eq $recordKeepDebug) { $recordKeepDebug = $IncludeDebugArtifacts.IsPresent }
            if ($null -eq $recordExcludeAi) { $recordExcludeAi = $ExcludeAiRuntime.IsPresent }
            if ([bool]$recordKeepDebug -ne $IncludeDebugArtifacts.IsPresent -or [bool]$recordExcludeAi -ne $ExcludeAiRuntime.IsPresent) {
                Write-Host ("注意：本次命令行的打包开关与台账不一致（台账：keepDebugArtifacts=$recordKeepDebug / excludeAiRuntime=$recordExcludeAi）；" +
                    "索引与增量按台账算，保证与这个包的内容一致。") -ForegroundColor Yellow
            }
            [void](Update-AxDeltaArtifacts `
                    -ProgramDirectory ([string]$existingRecord.programDirectory) `
                    -StagingDirectory $staging `
                    -TargetVersion $targetVersion `
                    -Runtime $Runtime `
                    -ManifestPath $manifestPath `
                    -ManifestFileName $PackageManifestFileName `
                    -ZipFileName $zipFileName `
                    -FullPackageBytes $zipBytes `
                    -ProjectRoot $root `
                    -DeltaBaseVersion $DeltaBaseVersion `
                    -RecordPath $recordPath `
                    -NoDelta:$NoDelta `
                    -KeepDebugArtifacts ([bool]$recordKeepDebug) `
                    -ExcludeAiRuntime ([bool]$recordExcludeAi))
            Write-Host '想强制重建请加 -ForceRebuild。' -ForegroundColor DarkGray
            $newestSource = Get-AxNewestSourceWriteTime -Root $root
            $zipWrittenAt = (Get-Item -LiteralPath $zipPath).LastWriteTime
            if ($newestSource -gt $zipWrittenAt.ToUniversalTime()) {
                Write-Host ("  提醒：源码（{0}）比这个包（{1}）新 —— 要重打进包请加 -ForceRebuild。" -f `
                        $newestSource.ToLocalTime().ToString('MM-dd HH:mm'), $zipWrittenAt.ToString('MM-dd HH:mm')) -ForegroundColor Yellow
            }
            Write-AxReleaseProgress -Stage 'reuse' -Percent 90 -Message "复用已有更新包：$zipFileName"
            Write-AxReleaseProgress -Stage 'completed' -Percent 95 `
                -Message "出包完成（复用）：$zipFileName（$([Math]::Round($zipBytes / 1MB, 2)) MB）"
            Write-Host ''
            Write-Host '产物（复用）：' -ForegroundColor Green
            Write-Host "  zip     ：$zipPath"
            Write-Host "  sha256  ：$shaPath"
            Write-Host "  清单    ：$manifestPath"
            Write-Host "  台账    ：$recordPath"
            $reusedParts = @(Get-AxJsonProperty -InputObject $existingRecord -Name 'parts')
            if ($reusedParts.Count -gt 0) {
                Write-Host ("  分卷    ：{0} 卷（按卷号顺序拼接后与整包逐字节相同）" -f $reusedParts.Count)
            }
            exit 0
        }
        Write-Host "$reuseProblem，改为重新构建。" -ForegroundColor Yellow
    }

    Write-Host "== 出包 $DisplayName v$targetVersion（$Runtime，通道 $channel）==" -ForegroundColor Cyan
    Write-Host "   工程根：$root"
    Write-Host "   产物目录：$staging"

    Write-AxReleaseProgress -Stage 'prepare' -Percent 2 -Message "准备出包 $DisplayName v$targetVersion"
    if (!$SkipVersionWrite) {
        Write-AxReleaseProgress -Stage 'version' -Percent 4 -Message "正在把版本号写入项目：$targetVersion"
        $setVersion = Join-Path $PSScriptRoot 'Set-AxToolsVersion.ps1'
        if (!(Test-Path -LiteralPath $setVersion -PathType Leaf)) {
            throw "找不到版本写入脚本：$setVersion"
        }
        & $setVersion -Version $targetVersion -ProjectRoot $root
    }

    $programDir = $null
    $packageRoot = $null
    if (![string]::IsNullOrWhiteSpace($SourceDirectory)) {
        $programDir = Get-AxNormalizedPath -Path $SourceDirectory
        if (!(Test-Path -LiteralPath $programDir -PathType Container)) {
            throw "指定的源目录不存在：$programDir"
        }
        Write-Host "使用指定源目录作为程序目录（跳过编译）：$programDir" -ForegroundColor Yellow
    }
    elseif (!$SkipBuild) {
        $dotnet = (Get-Command 'dotnet' -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        if ([string]::IsNullOrWhiteSpace($dotnet)) { throw '没有找到 dotnet CLI，无法编译 AxTools。' }
        if (!$SkipTests) {
            Write-AxReleaseProgress -Stage 'test' -Percent 6 -Message '正在跑 AxTools 单元测试（Release）'
            # 原生命令的输出必须显式走 Write-Host：否则它会顺管道变成本脚本的"返回值"，
            # 而发布编排层拿返回值当退出码用（踩过：报错里夹着整段构建日志）。
            & $dotnet test (Join-Path $root 'AxTools.Tests\AxTools.Tests.csproj') -c Release |
                ForEach-Object { Write-Host $_ }
            $testExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($testExit -ne 0) { throw "单元测试失败，退出码 $testExit。" }
        }
        Write-AxReleaseProgress -Stage 'build' -Percent 8 -Message '正在 dotnet publish（Release x64，自包含）'
        if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
        New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
        & $dotnet publish (Join-Path $root 'AxTools.csproj') -c Release -p:Platform=x64 -r $Runtime `
            --self-contained true -o $publishRoot `
            -p:Version=$targetVersion -p:InformationalVersion=$targetVersion |
            ForEach-Object { Write-Host $_ }
        $buildExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        if ($buildExit -ne 0) { throw "dotnet publish 失败，退出码 $buildExit。" }
        # WinUI 的 xbf/pri 不在 publish 输出里，从 bin 布局补齐（缺了客户端起不来）。
        foreach ($resourceName in $WinUiResourceNames) {
            $resourcePath = Join-Path $binLayout $resourceName
            if (!(Test-Path -LiteralPath $resourcePath -PathType Leaf)) { throw "Release 布局缺少 WinUI 资源：$resourcePath" }
            Copy-Item -LiteralPath $resourcePath -Destination (Join-Path $publishRoot $resourceName) -Force
        }
        $programDir = $publishRoot
        $packageRoot = $publishRoot
    }
    else {
        if (!(Test-Path -LiteralPath $publishRoot -PathType Container)) {
            throw ("-SkipBuild 需要已有程序目录：$publishRoot 不存在。" +
                "先跑一次不带 -SkipBuild 的出包，或用 -SourceDirectory 指向已有的发布目录。")
        }
        $programDir = $publishRoot
        $packageRoot = $publishRoot
        Write-Host "复用已有程序目录：$programDir" -ForegroundColor Yellow
    }
    Write-Host "程序目录：$programDir"
    if (Test-AxCancelled) { throw '用户取消了出包（构建阶段）。' }
    Write-AxReleaseProgress -Stage 'assemble' -Percent 55 -Message '正在补齐包内体检单与更新器脚本'
    $updaterTarget = Join-Path $programDir $updaterRelative
    $updaterDir = Split-Path -Parent $updaterTarget
    New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null
    Copy-Item -LiteralPath $updaterSource -Destination $updaterTarget -Force
    # 客户端可能会用 Windows PowerShell 5.1 起这个脚本，BOM 是它读懂中文注释的前提；
    # AxTools 侧走 pwsh 7 不需要，但顺手转一下零成本（源文件本来就是带 BOM 的）。
    if (!(Test-AxUtf8Bom -Path $updaterTarget)) {
        ConvertTo-AxUtf8BomFile -Path $updaterTarget
        if (!(Test-AxUtf8Bom -Path $updaterTarget)) {
            throw "无法把更新器脚本转成 UTF-8 with BOM：$updaterTarget"
        }
        Write-Host '（包内更新器已转成 UTF-8 with BOM）' -ForegroundColor DarkGray
    }
    $entry = Write-AxPackageManifest `
        -ProgramDirectory $programDir `
        -Version $targetVersion `
        -Runtime $Runtime `
        -FileName $PackageManifestFileName
    Assert-AxPackageStructure `
        -ProgramDirectory $programDir `
        -PackageManifestFileName $PackageManifestFileName `
        -UpdaterRelativePath $updaterRelative
    $files = @(Get-AxPackageFiles -Root $programDir -KeepDebugArtifacts:$IncludeDebugArtifacts.IsPresent -ExcludeAiRuntime:$ExcludeAiRuntime.IsPresent)
    if ($files.Count -eq 0) { throw "程序目录里没有可打包的文件：$programDir" }
    $rawBytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("进包 {0} 个文件，未压缩 {1:N1} MB" -f $files.Count, ($rawBytes / 1MB))
    if ($ExcludeAiRuntime.IsPresent -and !$IncludeDebugArtifacts.IsPresent) {
        # 实测（2026-10 最新包）：WindowsAppSDK 传递进来的 onnxruntime.dll 20.66 MB +
        # DirectML.dll 17.83 MB + Microsoft.ML.OnnxRuntime.dll 0.23 MB（zip 内合计约 16 MB）。
        # 全工程 grep 不到任何 OnnxRuntime / DirectML / Windows.AI 用法，AxTools 也没引用
        # Microsoft.Windows.AI.*，所以这是纯陪跑体积。将来真做本地推理时必须去掉这个开关。
        Write-Host ('已剔除 AI 推理运行时：' + ($AiRuntimeFileNames -join '、')) -ForegroundColor DarkGray
    }
    $debugLeaks = @($files | Where-Object { $_.Name -like '*.pdb' })
    if ($debugLeaks.Count -gt 0) {
        Write-Host "警告：包里仍有 $($debugLeaks.Count) 个 .pdb（-IncludeDebugArtifacts 生效中）。" -ForegroundColor Yellow
    }
    $zipPath = Join-Path $staging $zipFileName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $compressor = New-AxZipArchive -SourceDirectory $programDir -ArchivePath $zipPath -Entries $files.FullName
    if (!(Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "压缩没有产出文件：$zipPath" }
    $zipItem = Get-Item -LiteralPath $zipPath
    $zipBytes = [int64]$zipItem.Length
    Write-Host ("压缩完成（{0}）：{1:N2} MB / {2} 字节" -f $compressor, ($zipBytes / 1MB), $zipBytes)
    $baseName = [IO.Path]::GetFileNameWithoutExtension($zipFileName)
    Get-ChildItem -LiteralPath $staging -Filter "$baseName.part*.zip" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    if ($effectivePartSize -gt $AttachmentLimitBytes) {
        throw ("单卷大小 $([Math]::Round($effectivePartSize / 1MB, 2)) MB 超过 Gitee 单附件上限 100 MiB，" +
            "切了也传不上去。请把 -PartSizeBytes 调到 100 MiB 以下。")
    }
    $mustSplit = $zipBytes -gt $AttachmentLimitBytes -or ($PartSizeBytes -gt 0 -and $zipBytes -gt $PartSizeBytes)
    $parts = @()
    if ($mustSplit) {
        Write-AxReleaseProgress -Stage 'package' -Percent 80 `
            -Message ("整包 $([Math]::Round($zipBytes / 1MB, 2)) MB 超过单附件 100 MiB，正在分卷（每卷 ≤$([Math]::Round($effectivePartSize / 1MB, 0)) MB）")
        $parts = @(Split-AxUpdateArchive -SourcePath $zipPath -FileName $zipFileName -PartSizeBytes $effectivePartSize)
        Write-Host ("已分卷：{0} 卷（拼接顺序即卷号顺序，字节级拼接后与整包逐字节相同）" -f $parts.Count)
        foreach ($part in $parts) {
            Write-Host ("  {0}  {1,8:N2} MB  {2}" -f $part.FileName, ($part.SizeBytes / 1MB), $part.Sha256)
        }
    }
    else {
        $headroom = ($AttachmentLimitBytes - $zipBytes) / 1MB
        if ($headroom -lt 8) {
            Write-AxReleaseProgress -Stage 'package' -Percent 84 `
                -Message ("距 100 MiB 单附件上限只剩 {0:N2} MB：下个版本大概会自动分卷" -f $headroom)
            Write-Host ("注意：距 100 MiB 上限只剩 {0:N2} MB；下个版本一旦超过就会自动分卷。" -f $headroom) -ForegroundColor Yellow
        }
    }
    Write-AxReleaseProgress -Stage 'hash' -Percent 84 -Message '正在计算 SHA256'
    $sha256 = Get-AxSha256Text -Path $zipPath
    $shaLines = [Collections.Generic.List[string]]::new()
    $shaLines.Add("$sha256  $zipFileName")
    foreach ($part in $parts) { $shaLines.Add("$($part.Sha256)  $($part.FileName)") }
    [IO.File]::WriteAllBytes(
        $shaPath,
        [Text.Encoding]::ASCII.GetBytes(($shaLines -join "`n") + "`n"))
    Write-Host "SHA256：$sha256"
    if ($parts.Count -gt 0) { Write-Host ("（分卷：sha256.txt 共 {0} 行 = 整包 + {1} 卷）" -f $shaLines.Count, $parts.Count) }
    Write-AxReleaseProgress -Stage 'manifest' -Percent 90 -Message '正在生成更新清单 toolbox-update.json'
    $manifest = New-AxManifest -Version $targetVersion -Channel $channel -Runtime $Runtime -Notes $Notes `
        -PublishedAt (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz') `
        -Sha256 $sha256 -SizeBytes $zipBytes -FileName $zipFileName -Repository $GiteeRepository `
        -Parts @($parts | ForEach-Object {
            [ordered]@{ fileName = $_.FileName; sha256 = $_.Sha256; sizeBytes = $_.SizeBytes }
        })
    Write-AxJsonFile -Path $manifestPath -Data $manifest

    # 增量包 + 本版文件索引：必须在清单落盘**之后**（函数要读清单补 delta 段）。
    # 两条路径（全量出包 / 复用已有包）共用同一个函数，避免"复用重发"把 delta 段抹掉。
    $deltaStep = Update-AxDeltaArtifacts `
        -ProgramDirectory $programDir `
        -StagingDirectory $staging `
        -TargetVersion $targetVersion `
        -Runtime $Runtime `
        -ManifestPath $manifestPath `
        -ManifestFileName $PackageManifestFileName `
        -ZipFileName $zipFileName `
        -FullPackageBytes $zipBytes `
        -ProjectRoot $root `
        -DeltaBaseVersion $DeltaBaseVersion `
        -RecordPath $recordPath `
        -NoDelta:$NoDelta `
        -KeepDebugArtifacts $IncludeDebugArtifacts.IsPresent `
        -ExcludeAiRuntime $ExcludeAiRuntime.IsPresent
    $deltaRecord = $deltaStep.Delta
    $indexPath = $deltaStep.IndexPath

    $record = [ordered]@{
        schemaVersion     = 1
        toolboxStableKey  = $ToolboxStableKey
        version           = $targetVersion
        channel           = $channel
        runtime           = $Runtime
        projectRoot       = $root
        programDirectory  = $programDir
        packageDirectory  = $packageRoot
        entryExe          = $entry.Name
        zipPath           = $zipPath
        zipFileName       = $zipFileName
        sha256            = $sha256
        sizeBytes         = $zipBytes
        sha256FilePath    = $shaPath
        manifestPath      = $manifestPath
        notes             = $Notes
        fileIndexPath     = $indexPath
        delta             = if ($null -eq $deltaRecord) { $null } else { [ordered]@{
                baseVersion  = $deltaRecord.BaseVersion
                fileName     = $deltaRecord.FileName
                path         = $deltaRecord.Path
                sha256       = $deltaRecord.Sha256
                sizeBytes    = $deltaRecord.SizeBytes
                changedCount = $deltaRecord.ChangedCount
                deleteCount  = $deltaRecord.DeleteCount
            } }

        fileCount         = $files.Count
        uncompressedBytes = [int64]$rawBytes
        compressor        = $compressor
        # 打包时用的开关也记下来：复用路径要靠它把索引算得与包内容一致（见下面的复用分支）。
        keepDebugArtifacts = [bool]$IncludeDebugArtifacts.IsPresent
        excludeAiRuntime   = [bool]$ExcludeAiRuntime.IsPresent
        splitPartBytes    = [int64]$effectivePartSize
        parts             = @($parts | ForEach-Object {
                [ordered]@{ fileName = $_.FileName; path = $_.Path; sha256 = $_.Sha256; sizeBytes = $_.SizeBytes }
            })
        createdAt         = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    }
    Write-AxJsonFile -Path $recordPath -Data $record
    Write-AxReleaseProgress -Stage 'completed' -Percent 95 -Message "出包完成：$zipFileName（$([Math]::Round($zipBytes / 1MB, 2)) MB）"
    Write-Host ''
    Write-Host '出包完成，发布物：' -ForegroundColor Green
    Write-Host "  zip     ：$zipPath"
    Write-Host "  sha256  ：$shaPath"
    Write-Host "  清单    ：$manifestPath"
    Write-Host "  台账    ：$recordPath"
    Write-Host "  索引    ：$indexPath"
    Write-Host "  版本    ：$targetVersion（$channel）"
    Write-Host "  耗时    ：$([Math]::Round((Get-Date).Subtract($script:StartedAt).TotalSeconds, 1))s"
}
catch {
    $exitCode = 1
    Write-AxReleaseProgress -Stage 'failed' -Percent 100 -Message ("出包失败：" + $_.Exception.Message)
    [Console]::Error.WriteLine('出包失败：' + $_.Exception.Message)
    [Console]::Error.WriteLine($_.ScriptStackTrace)
}
exit $exitCode
