Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

function Assert-AxProjectRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string[]]$RequiredPaths
    )

    $root = [IO.Path]::GetFullPath($ProjectRoot)
    foreach ($relativePath in $RequiredPaths) {
        $path = Join-Path $root $relativePath
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "缺少项目特征：$relativePath"
        }
    }
    return $root
}

function Assert-AxPathOutsideRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        [Parameter(Mandatory)][string]$ForbiddenRoot,
        [Parameter(Mandatory)][string]$Message
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $forbidden = [IO.Path]::GetFullPath($ForbiddenRoot).TrimEnd('\')
    if ([string]::Equals($candidate, $forbidden, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($forbidden + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw $Message
    }
}

function Resolve-AxCommand {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { throw "缺少命令：$Name" }
    return $command.Source
}

function Initialize-AxMsvcEnvironment {
    [CmdletBinding()]
    param()

    if ([string]::IsNullOrWhiteSpace($env:AXTOOLS_VISUAL_STUDIO_ROOT)) {
        throw 'AxTools 尚未配置 Visual Studio C++ 环境，请先在整体设置中重新扫描环境。'
    }
    if ([string]::IsNullOrWhiteSpace($env:AXTOOLS_MSVC_VERSION)) {
        throw 'AxTools 尚未配置 MSVC 版本，请先在整体设置中重新扫描环境。'
    }

    $visualStudioRoot = [IO.Path]::GetFullPath($env:AXTOOLS_VISUAL_STUDIO_ROOT)
    $vcvarsPath = Join-Path $visualStudioRoot 'VC\Auxiliary\Build\vcvarsall.bat'
    if (!(Test-Path -LiteralPath $vcvarsPath -PathType Leaf)) {
        throw "Visual Studio 开发者环境脚本不存在：$vcvarsPath"
    }

    $commandInterpreter = Join-Path $env:SystemRoot 'System32\cmd.exe'
    if (!(Test-Path -LiteralPath $commandInterpreter -PathType Leaf)) {
        throw "Windows 命令解释器不存在：$commandInterpreter"
    }
    $commandLine = 'call "' + $vcvarsPath + '" x64 -vcvars_ver=' +
        $env:AXTOOLS_MSVC_VERSION + ' >nul && set'
    $environmentOutput = & $commandInterpreter /d /s /c $commandLine
    if ($LASTEXITCODE -ne 0) {
        throw "加载 MSVC $env:AXTOOLS_MSVC_VERSION 开发者环境失败，退出码 $LASTEXITCODE。"
    }

    foreach ($line in $environmentOutput) {
        if ($line -notmatch '^([^=]+)=(.*)$') { continue }
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }

    $linker = Get-Command 'link.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $linker) {
        throw "MSVC $env:AXTOOLS_MSVC_VERSION 环境已加载，但找不到 x64 link.exe。"
    }
    $shell32 = @($env:LIB -split [IO.Path]::PathSeparator) |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { Join-Path $_ 'shell32.lib' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($shell32)) {
        throw 'MSVC 环境已加载，但 Windows SDK LIB 中找不到 shell32.lib。'
    }

    return [pscustomobject]@{
        VisualStudioRoot = $visualStudioRoot
        MsvcVersion = [string]$env:VCToolsVersion
        WindowsSdkVersion = [string]$env:WindowsSDKVersion
        LinkerPath = $linker.Source
        Shell32LibraryPath = $shell32
    }
}

function Invoke-AxExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][ref]$ExitCode
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList 2>&1 | ForEach-Object { Write-Output $_.ToString() }
        $ExitCode.Value = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
}

function Assert-AxExternalSuccess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ExitCode,
        [Parameter(Mandatory)][string]$Operation
    )

    if ($ExitCode -ne 0) { throw "$Operation 失败，退出码 $ExitCode。" }
}

function Start-AxExecutable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { throw '尚未配置程序路径。' }
    $fullPath = [IO.Path]::GetFullPath($ExecutablePath)
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "程序不存在：$fullPath" }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fullPath
    $startInfo.WorkingDirectory = Split-Path -Parent $fullPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    [Diagnostics.Process]::Start($startInfo) | Out-Null
}

function Get-AxNormalizedPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
}

function Get-AxDevelopmentBuildStatePath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ToolKey)

    if ($ToolKey -notmatch '^[A-Za-z0-9._-]+$') { throw "工具标识格式无效：$ToolKey" }
    $stateRoot = if (![string]::IsNullOrWhiteSpace($env:AXTOOLS_BUILD_STATE_ROOT)) {
        [IO.Path]::GetFullPath($env:AXTOOLS_BUILD_STATE_ROOT)
    }
    else {
        $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($localAppData)) { throw '无法确定 LocalAppData 目录。' }
        Join-Path $localAppData 'AxTools\BuildState'
    }
    return Join-Path $stateRoot "$ToolKey.json"
}

function Get-AxStableFileHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        catch [IO.IOException] {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Get-AxDevelopmentInputFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][ValidateSet('DotNet','Tauri')][string]$Profile
    )

    $root = Get-AxNormalizedPath -Path $ProjectRoot
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "项目目录不存在：$root" }

    $excludedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        '.git','.vs','.idea','bin','obj','Tests','node_modules','dist','target','logs','log',
        'artifacts','packages','publish','release','dist-launcher-update','DabaoV')) {
        [void]$excludedDirectories.Add($name)
    }

    $dotNetExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(
        '.sln','.slnx','.csproj','.props','.targets','.cs','.xaml','.resw','.json','.config',
        '.xml','.manifest','.png','.jpg','.jpeg','.ico','.xbf','.pri','.ps1')) {
        [void]$dotNetExtensions.Add($extension)
    }
    $tauriExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(
        '.ts','.tsx','.js','.jsx','.vue','.css','.scss','.sass','.less','.html','.json','.toml',
        '.lock','.rs','.png','.jpg','.jpeg','.ico','.svg','.xml','.ps1')) {
        [void]$tauriExtensions.Add($extension)
    }
    $specialNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('.editorconfig','nuget.config','directory.build.props','directory.build.targets')) {
        [void]$specialNames.Add($name)
    }
    $extensions = if ($Profile -eq 'DotNet') { $dotNetExtensions } else { $tauriExtensions }

    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $pendingDirectories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pendingDirectories.Push([IO.DirectoryInfo]::new($root))
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($file in $directory.EnumerateFiles()) {
            if ($extensions.Contains($file.Extension) -or $specialNames.Contains($file.Name)) {
                $files.Add($file)
            }
        }
        foreach ($child in $directory.EnumerateDirectories()) {
            if ($excludedDirectories.Contains($child.Name) -or
                $child.Name.StartsWith('.axtools-', [StringComparison]::OrdinalIgnoreCase) -or
                ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }
            $pendingDirectories.Push($child)
        }
    }
    $files = @($files | Sort-Object FullName)

    $builder = [Text.StringBuilder]::new()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        $hash = Get-AxStableFileHash -Path $file.FullName
        [void]$builder.Append($relative).Append([char]0).Append($file.Length).Append([char]0).Append($hash).Append("`n")
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-AxDevelopmentFileIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = Get-AxNormalizedPath -Path $Path
    $item = Get-Item -LiteralPath $fullPath -ErrorAction Stop
    if ($item.PSIsContainer) { throw "开发产物不是文件：$fullPath" }
    return [ordered]@{
        path = $fullPath
        length = [long]$item.Length
        lastWriteTimeUtcTicks = [long]$item.LastWriteTimeUtc.Ticks
    }
}

function Write-AxDevelopmentBuildState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ToolKey,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][ValidateSet('DotNet','Tauri')][string]$Profile,
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [string[]]$RequiredResourcePaths = @()
    )

    $statePath = Get-AxDevelopmentBuildStatePath -ToolKey $ToolKey
    $stateDirectory = Split-Path -Parent $statePath
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $resources = @($RequiredResourcePaths | ForEach-Object { Get-AxDevelopmentFileIdentity -Path $_ })
    $state = [ordered]@{
        schemaVersion = 1
        toolKey = $ToolKey
        projectRoot = Get-AxNormalizedPath -Path $ProjectRoot
        profile = $Profile
        configuration = $Configuration
        inputFingerprint = Get-AxDevelopmentInputFingerprint -ProjectRoot $ProjectRoot -Profile $Profile
        executable = Get-AxDevelopmentFileIdentity -Path $ExecutablePath
        resources = $resources
        succeededAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    $temporaryPath = "$statePath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        Move-Item -LiteralPath $temporaryPath -Destination $statePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Test-AxDevelopmentFileIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Identity)

    try {
        $actual = Get-AxDevelopmentFileIdentity -Path ([string]$Identity.path)
        return [string]::Equals([string]$actual.path, [string]$Identity.path, [StringComparison]::OrdinalIgnoreCase) -and
            [long]$actual.length -eq [long]$Identity.length -and
            [long]$actual.lastWriteTimeUtcTicks -eq [long]$Identity.lastWriteTimeUtcTicks
    }
    catch {
        return $false
    }
}

function Test-AxDevelopmentBuildCurrent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ToolKey,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][ValidateSet('DotNet','Tauri')][string]$Profile,
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [string[]]$RequiredResourcePaths = @()
    )

    try {
        $statePath = Get-AxDevelopmentBuildStatePath -ToolKey $ToolKey
        if (!(Test-Path -LiteralPath $statePath -PathType Leaf)) { return $false }
        $state = Get-Content -Raw -Encoding UTF8 -LiteralPath $statePath | ConvertFrom-Json
        if ([int]$state.schemaVersion -ne 1 -or
            ![string]::Equals([string]$state.toolKey, $ToolKey, [StringComparison]::Ordinal) -or
            ![string]::Equals([string]$state.projectRoot, (Get-AxNormalizedPath -Path $ProjectRoot), [StringComparison]::OrdinalIgnoreCase) -or
            ![string]::Equals([string]$state.profile, $Profile, [StringComparison]::Ordinal) -or
            ![string]::Equals([string]$state.configuration, $Configuration, [StringComparison]::Ordinal) -or
            ![string]::Equals([string]$state.executable.path, (Get-AxNormalizedPath -Path $ExecutablePath), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        if (!(Test-AxDevelopmentFileIdentity -Identity $state.executable)) { return $false }

        $expectedResources = @($RequiredResourcePaths | ForEach-Object { Get-AxNormalizedPath -Path $_ })
        $savedResources = @($state.resources)
        if ($savedResources.Count -ne $expectedResources.Count) { return $false }
        foreach ($resourcePath in $expectedResources) {
            $identity = $savedResources | Where-Object {
                [string]::Equals([string]$_.path, $resourcePath, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            if ($null -eq $identity -or !(Test-AxDevelopmentFileIdentity -Identity $identity)) { return $false }
        }

        $fingerprint = Get-AxDevelopmentInputFingerprint -ProjectRoot $ProjectRoot -Profile $Profile
        return [string]::Equals([string]$state.inputFingerprint, $fingerprint, [StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Clear-AxDevelopmentBuildState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ToolKey)

    $statePath = Get-AxDevelopmentBuildStatePath -ToolKey $ToolKey
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        Remove-Item -LiteralPath $statePath -Force
    }
}

function Test-AxStringSetEqual {
    param([string[]]$Left,[string[]]$Right)

    $leftValues = @($Left |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    $rightValues = @($Right |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    if ($leftValues.Count -ne $rightValues.Count) { return $false }
    foreach ($value in $leftValues) {
        $match = $rightValues | Where-Object {
            [string]::Equals($_, $value, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $match) { return $false }
    }
    return $true
}

function ConvertFrom-AxNuGetSnapshotVersion {
    param([string]$Version)

    $value = $Version.Trim()
    $minimum = [regex]::Match($value, '^\[([^,\]]+),\s*\)$')
    return $(if ($minimum.Success) { $minimum.Groups[1].Value } else { $value })
}

function Test-AxProjectRestoreSnapshotCurrent {
    param(
        [Parameter(Mandatory)][IO.FileInfo]$Project,
        [Parameter(Mandatory)][string]$ProjectRoot
    )

    $snapshotPath = Join-Path $Project.DirectoryName "obj\$($Project.Name).nuget.dgspec.json"
    if (!(Test-Path -LiteralPath $snapshotPath -PathType Leaf)) { return $false }

    try {
        [xml]$projectXml = Get-Content -Raw -Encoding UTF8 -LiteralPath $Project.FullName
        $snapshot = Get-Content -Raw -Encoding UTF8 -LiteralPath $snapshotPath |
            ConvertFrom-Json -AsHashtable
        $projectKey = $snapshot.projects.Keys | Where-Object {
            [string]::Equals(
                [IO.Path]::GetFullPath([string]$_),
                $Project.FullName,
                [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $projectKey) { return $false }
        $savedProject = $snapshot.projects[$projectKey]

        $targetFrameworks = @($projectXml.SelectNodes(
            "//*[local-name()='TargetFramework' or local-name()='TargetFrameworks']") |
            ForEach-Object { $_.InnerText -split ';' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ })
        $savedTargetFrameworks = @($savedProject.restore.originalTargetFrameworks)
        if (!(Test-AxStringSetEqual -Left $targetFrameworks -Right $savedTargetFrameworks)) { return $false }

        $runtimeIdentifiers = @($projectXml.SelectNodes(
            "//*[local-name()='RuntimeIdentifier' or local-name()='RuntimeIdentifiers']") |
            ForEach-Object { $_.InnerText -split ';' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ })
        $savedRuntimeIdentifiers = if ($null -eq $savedProject.runtimes) {
            @()
        } else {
            @($savedProject.runtimes.Keys)
        }
        if (!(Test-AxStringSetEqual -Left $runtimeIdentifiers -Right $savedRuntimeIdentifiers)) { return $false }

        $currentPackages = @{}
        foreach ($node in $projectXml.SelectNodes("//*[local-name()='PackageReference']")) {
            $name = $node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($name)) { $name = $node.GetAttribute('Update') }
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $version = $node.GetAttribute('Version')
            if ([string]::IsNullOrWhiteSpace($version)) {
                $versionNode = $node.SelectSingleNode("*[local-name()='Version']")
                $version = if ($null -eq $versionNode) { '' } else { $versionNode.InnerText.Trim() }
            }
            $currentPackages[$name] = $version
        }

        $savedPackages = @{}
        foreach ($framework in $savedProject.frameworks.Values) {
            if ($null -eq $framework.dependencies) { continue }
            foreach ($entry in $framework.dependencies.GetEnumerator()) {
                $isAutoReferenced = $entry.Value.ContainsKey('autoReferenced') -and
                    [bool]$entry.Value.autoReferenced
                if ([string]$entry.Value.target -ne 'Package' -or $isAutoReferenced) { continue }
                $savedPackages[$entry.Key] = ConvertFrom-AxNuGetSnapshotVersion ([string]$entry.Value.version)
            }
        }
        if (!(Test-AxStringSetEqual -Left @($currentPackages.Keys) -Right @($savedPackages.Keys))) { return $false }
        foreach ($name in $currentPackages.Keys) {
            if ([string]::IsNullOrWhiteSpace([string]$currentPackages[$name])) { return $false }
            if (![string]::Equals(
                [string]$currentPackages[$name],
                [string]$savedPackages[$name],
                [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        $currentProjectReferences = @($projectXml.SelectNodes("//*[local-name()='ProjectReference']") |
            ForEach-Object {
                $include = $_.GetAttribute('Include')
                if (![string]::IsNullOrWhiteSpace($include)) {
                    [IO.Path]::GetFullPath((Join-Path $Project.DirectoryName $include))
                }
            })
        $savedProjectReferences = @($savedProject.restore.frameworks.Values |
            ForEach-Object { if ($null -ne $_.projectReferences) { $_.projectReferences.Keys } })
        if (!(Test-AxStringSetEqual -Left $currentProjectReferences -Right $savedProjectReferences)) { return $false }

        $snapshotFile = Get-Item -LiteralPath $snapshotPath
        $configFilePaths = if ($savedProject.restore.ContainsKey('configFilePaths')) {
            @($savedProject.restore.configFilePaths)
        } else {
            @()
        }
        foreach ($configPath in $configFilePaths) {
            if (Test-Path -LiteralPath $configPath -PathType Leaf) {
                if ((Get-Item -LiteralPath $configPath).LastWriteTimeUtc -gt $snapshotFile.LastWriteTimeUtc) {
                    return $false
                }
            }
        }
        $directory = $Project.Directory
        $normalizedRoot = Get-AxNormalizedPath -Path $ProjectRoot
        while ($null -ne $directory -and
            $directory.FullName.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.config','global.json')) {
                $path = Join-Path $directory.FullName $name
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and
                    (Get-Item -LiteralPath $path).LastWriteTimeUtc -gt $snapshotFile.LastWriteTimeUtc) {
                    return $false
                }
            }
            $directory = $directory.Parent
        }
        return $true
    }
    catch {
        return $false
    }
}

function Test-AxDotNetRestoreCache {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot)

    $root = Get-AxNormalizedPath -Path $ProjectRoot
    $excludedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        '.git','.vs','.idea','bin','obj','Tests','node_modules','dist','target','logs','log',
        'artifacts','packages','publish','release','dist-launcher-update','DabaoV')) {
        [void]$excludedDirectories.Add($name)
    }

    $projects = [Collections.Generic.List[IO.FileInfo]]::new()
    $pendingDirectories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pendingDirectories.Push([IO.DirectoryInfo]::new($root))
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($project in $directory.EnumerateFiles('*.csproj', [IO.SearchOption]::TopDirectoryOnly)) {
            $projects.Add($project)
        }
        foreach ($child in $directory.EnumerateDirectories()) {
            if ($excludedDirectories.Contains($child.Name) -or
                $child.Name.StartsWith('.axtools-', [StringComparison]::OrdinalIgnoreCase) -or
                ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }
            $pendingDirectories.Push($child)
        }
    }
    if ($projects.Count -eq 0) { return $false }

    foreach ($project in $projects) {
        $assetsPath = Join-Path $project.DirectoryName 'obj\project.assets.json'
        if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { return $false }
        $assetsFile = Get-Item -LiteralPath $assetsPath
        if ($assetsFile.LastWriteTimeUtc -lt $project.LastWriteTimeUtc -and
            !(Test-AxProjectRestoreSnapshotCurrent -Project $project -ProjectRoot $root)) {
            return $false
        }

        try {
            $assets = Get-Content -Raw -Encoding UTF8 -LiteralPath $assetsPath |
                ConvertFrom-Json -AsHashtable
            $packageFolders = @($assets.packageFolders.Keys)
            if ($packageFolders.Count -eq 0) { return $false }
            foreach ($entry in $assets.libraries.GetEnumerator()) {
                if ($entry.Value.type -ne 'package') { continue }
                $packagePath = [string]$entry.Value.path
                $available = $packageFolders | Where-Object {
                    Test-Path -LiteralPath (Join-Path $_ $packagePath) -PathType Container
                } | Select-Object -First 1
                if ($null -eq $available) { return $false }
            }
        }
        catch {
            return $false
        }
    }

    return $true
}

function Invoke-AxDotNetIncrementalBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [string]$Configuration = 'Debug',
        [string[]]$AdditionalArguments = @(),
        [Parameter(Mandatory)][string]$Operation,
        [string]$DependencyStepPrefix = '',
        [string]$BuildStepPrefix = '',
        [switch]$ForceRestore,
        [ValidateSet('quiet','minimal','normal','detailed','diagnostic')]
        [string]$BuildVerbosity = 'minimal'
    )

    Write-AxTaskProgress `
        -Stage 'dependencies' `
        -Percent 10 `
        -Message "${DependencyStepPrefix}依赖检查 · 正在扫描项目缓存..." `
        -Detail "项目目录：$ProjectRoot"
    if ($ForceRestore -or !(Test-AxDotNetRestoreCache -ProjectRoot $ProjectRoot)) {
        Write-AxTaskProgress `
            -Stage 'dependencies' `
            -Percent 15 `
            -Message "${DependencyStepPrefix}依赖检查 · 缓存需要更新" `
            -Detail '检测到缺失或过期的 project.assets.json，将运行 dotnet restore。'
        Write-AxTaskProgress `
            -Stage 'restore' `
            -Percent 20 `
            -Message "${DependencyStepPrefix}依赖恢复 · 正在更新 NuGet 缓存..." `
            -Detail "目标：$TargetPath；仅在依赖缓存失效时访问包源。"
        $restoreExitCode = 0
        Invoke-AxExternalCommand -FilePath $DotNetPath `
            -ArgumentList (@('restore', '--force-evaluate', $TargetPath) + $AdditionalArguments) `
            -WorkingDirectory $ProjectRoot `
            -ExitCode ([ref]$restoreExitCode)
        Assert-AxExternalSuccess -ExitCode $restoreExitCode -Operation "$Operation 依赖恢复"
        Write-AxTaskProgress `
            -Stage 'restore' `
            -Percent 30 `
            -Message "${DependencyStepPrefix}依赖恢复 · NuGet 缓存已更新" `
            -Detail '依赖恢复成功，下一步进入本地增量编译。'
    }
    else {
        Write-AxTaskProgress `
            -Stage 'dependencies' `
            -Percent 30 `
            -Message "${DependencyStepPrefix}依赖检查 · 缓存有效" `
            -Detail '所有项目的 project.assets.json 与 NuGet 包均可用，无需联网恢复。'
    }

    Write-AxTaskProgress `
        -Stage 'build' `
        -Percent 40 `
        -Message "${BuildStepPrefix}增量编译 · 正在构建 $Operation..." `
        -Detail "配置：$Configuration；目标：$TargetPath；MSBuild 正在处理代码与项目资源。"
    $buildExitCode = 0
    Invoke-AxExternalCommand -FilePath $DotNetPath `
        -ArgumentList (@('build', $TargetPath, '-c', $Configuration, '--no-restore', '--verbosity', $BuildVerbosity) + $AdditionalArguments) `
        -WorkingDirectory $ProjectRoot `
        -ExitCode ([ref]$buildExitCode)
    Assert-AxExternalSuccess -ExitCode $buildExitCode -Operation $Operation
    Write-AxTaskProgress `
        -Stage 'build' `
        -Percent 80 `
        -Message "${BuildStepPrefix}增量编译 · 构建成功" `
        -Detail 'MSBuild 已成功结束，下一步将由工具适配器校验可执行文件与运行资源。'
}

Export-ModuleMember -Function @(
    'Assert-AxProjectRoot',
    'Assert-AxPathOutsideRoot',
    'Resolve-AxCommand',
    'Initialize-AxMsvcEnvironment',
    'Invoke-AxExternalCommand',
    'Assert-AxExternalSuccess',
    'Get-AxDevelopmentBuildStatePath',
    'Get-AxDevelopmentInputFingerprint',
    'Test-AxDevelopmentBuildCurrent',
    'Write-AxDevelopmentBuildState',
    'Clear-AxDevelopmentBuildState',
    'Test-AxDotNetRestoreCache',
    'Invoke-AxDotNetIncrementalBuild',
    'Start-AxExecutable'
)
