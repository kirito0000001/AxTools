# 把版本号写进 AxTools.csproj 的四个版本属性（只许前进）。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,
    [switch]$AllowDowngrade,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$versionPattern = '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:-(?<suffix>[0-9A-Za-z][0-9A-Za-z.\-]*))?$'

function Assert-AxVersionText {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Field)
    if ($Text -notmatch $versionPattern) {
        throw "$Field 必须形如 1.0.1 或 1.0.1-Beta：$Text"
    }
}
function Get-AxProjectPath {
    param([Parameter(Mandatory)][string]$Root)
    $full = [IO.Path]::GetFullPath($Root)
    $path = Join-Path $full 'AxTools.csproj'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "找不到项目文件：$path"
    }
    return $path
}
function Get-AxProjectText {
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $detected = [Text.Encoding]::UTF8.GetString($bytes)
    if ($detected.IndexOf([char]0xFFFD) -ge 0) {
        throw "项目文件不是有效的 UTF-8：$Path"
    }
    return $detected
}
function Write-AxProjectText {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Get-AxVersionProperty {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Name)
    $match = [regex]::Match($Text, "<$Name>([^<]*)</$Name>")
    return $(if ($match.Success) { $match.Groups[1].Value.Trim() } else { '' })
}
function Set-AxVersionProperty {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )
    $pattern = "<$Name>[^<]*</$Name>"
    if ($Text -notmatch $pattern) {
        throw "项目文件里没有 <$Name> 属性，无法写入版本号。"
    }
    return [regex]::Replace($Text, $pattern, "<$Name>$Value</$Name>", 1)
}
function Compare-AxVersionText {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)
    $leftMatch = [regex]::Match($Left, $versionPattern)
    $rightMatch = [regex]::Match($Right, $versionPattern)
    $leftParts = @([int64]$leftMatch.Groups['major'].Value, [int64]$leftMatch.Groups['minor'].Value, [int64]$leftMatch.Groups['build'].Value)
    $rightParts = @([int64]$rightMatch.Groups['major'].Value, [int64]$rightMatch.Groups['minor'].Value, [int64]$rightMatch.Groups['build'].Value)
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftParts[$index] -ne $rightParts[$index]) {
            return $leftParts[$index].CompareTo($rightParts[$index])
        }
    }
    $leftSuffix = [string]$leftMatch.Groups['suffix'].Value
    $rightSuffix = [string]$rightMatch.Groups['suffix'].Value
    if ([string]::IsNullOrEmpty($leftSuffix) -and ![string]::IsNullOrEmpty($rightSuffix)) { return 1 }
    if (![string]::IsNullOrEmpty($leftSuffix) -and [string]::IsNullOrEmpty($rightSuffix)) { return -1 }
    if ([string]::IsNullOrEmpty($leftSuffix)) { return 0 }
    return [string]::Compare($leftSuffix, $rightSuffix, [StringComparison]::OrdinalIgnoreCase)
}

$projectPath = Get-AxProjectPath -Root $ProjectRoot
$requested = $Version.Trim()
Assert-AxVersionText -Text $requested -Field '目标版本'
$requestedMatch = [regex]::Match($requested, $versionPattern)
$numeric = '{0}.{1}.{2}' -f $requestedMatch.Groups['major'].Value, $requestedMatch.Groups['minor'].Value, $requestedMatch.Groups['build'].Value
$text = Get-AxProjectText -Path $projectPath
$currentVersion = Get-AxVersionProperty -Text $text -Name 'Version'
$currentInformational = Get-AxVersionProperty -Text $text -Name 'InformationalVersion'
if (![string]::IsNullOrWhiteSpace($currentVersion)) {
    Assert-AxVersionText -Text $currentVersion -Field '当前 <Version>'
    $comparison = Compare-AxVersionText -Left $requested -Right $currentVersion
    if ($comparison -lt 0 -and !$AllowDowngrade) {
        throw "新版本号必须不低于当前版本。当前：$currentVersion；要求：$requested。确实要回退请加 -AllowDowngrade。"
    }
    if ($comparison -eq 0) {
        Write-Host "当前已是 $currentVersion，无需重复写入。项目：$projectPath" -ForegroundColor DarkGray
        if ([string]::Equals($currentVersion, $requested, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "版本属性未改动。" -ForegroundColor DarkGray
            return
        }
    }
}
$updates = [ordered]@{
    Version              = $requested
    AssemblyVersion      = "$numeric.0"
    FileVersion          = "$numeric.0"
    InformationalVersion = $requested
}
foreach ($name in $updates.Keys) {
    $text = Set-AxVersionProperty -Text $text -Name $name -Value $updates[$name]
}
$before = "当前：Version=$currentVersion；InformationalVersion=$currentInformational"
Write-Host $before -ForegroundColor DarkGray
Write-Host ("写入：Version={0}；AssemblyVersion={1}；FileVersion={2}；InformationalVersion={3}" -f `
        $updates.Version, $updates.AssemblyVersion, $updates.FileVersion, $updates.InformationalVersion)
if ($Preview) {
    Write-Host "Preview：未写入项目文件。" -ForegroundColor Yellow
    return
}
Write-AxProjectText -Path $projectPath -Text $text
$verdict = Get-AxProjectText -Path $projectPath
foreach ($name in $updates.Keys) {
    $written = Get-AxVersionProperty -Text $verdict -Name $name
    if (![string]::Equals($written, $updates[$name], [StringComparison]::Ordinal)) {
        throw "写入校验失败：<$name> 期望 $($updates[$name])，实际 $written。"
    }
}
# 编码损坏的金丝雀：AxTools.csproj 没有 <Product>，用同样稳定的 AssemblyName 顶替 ZD 侧那一句。
if ($verdict -notmatch '<AssemblyName>AxTools</AssemblyName>') {
    throw "写入后 <AssemblyName> 异常，疑似编码损坏。请用 git 还原 $projectPath。"
}
Write-Host "版本已写入：$projectPath" -ForegroundColor Green
