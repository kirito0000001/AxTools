[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryName,
    [Parameter(Mandatory)][string]$DestinationPath,
    [Parameter(Mandatory)][string]$ExpectedDirectoryName
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

$target = [IO.Path]::GetFullPath($DestinationPath)
$parent = Split-Path -Parent $target
$targetExisted = Test-Path -LiteralPath $target -PathType Container
try {
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止正式版下载。'
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw "下载父目录不存在：$parent" }
    if (![string]::Equals([IO.Path]::GetFileName($target), $ExpectedDirectoryName, [StringComparison]::Ordinal)) {
        throw "目标目录名称不匹配：$target"
    }
    if (Test-Path -LiteralPath $target) {
        if (!(Test-Path -LiteralPath $target -PathType Container)) { throw "目标路径已经被文件占用：$target" }
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) { throw "目标路径不是空目录：$target" }
    }

    $gh = Resolve-AxCommand -Name 'gh'
    Write-AxTaskProgress -Stage 'release' -Percent 10 -Message '正在读取 GitHub 最新正式版...' -Detail $RepositoryName
    $releaseJson = @(& $gh release view --repo $RepositoryName --json tagName,isPrerelease 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "GitHub 没有可下载的正式版：$RepositoryName" }
    $release = ($releaseJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($release.isPrerelease -eq $true -or [string]::IsNullOrWhiteSpace([string]$release.tagName)) {
        throw "GitHub 最新 Release 不是正式版：$RepositoryName"
    }

    Write-AxTaskProgress -Stage 'download' -Percent 30 -Message "正在下载正式版 $($release.tagName)..." -Detail $target
    $downloadExitCode = 0
    Invoke-AxExternalCommand `
        -FilePath $gh `
        -ArgumentList @('release','download',[string]$release.tagName,'--repo',$RepositoryName,'--dir',$target) `
        -WorkingDirectory $parent `
        -ExitCode ([ref]$downloadExitCode)
    Assert-AxExternalSuccess -ExitCode $downloadExitCode -Operation 'GitHub 正式版下载'

    $files = @(Get-ChildItem -LiteralPath $target -File)
    if ($files.Count -eq 0 -or @($files | Where-Object Length -LE 0).Count -gt 0) {
        throw '下载目录没有有效的正式版附件。'
    }
    Write-AxTaskProgress -Stage 'verify' -Percent 100 -Message "正式版 $($release.tagName) 下载完成。" -Detail $target
    Write-AxTaskEvent -Data ([ordered]@{ type='artifact'; kind='release-download'; path=$target; version=[string]$release.tagName })
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "正式版已下载：$target"
    exit 0
}
catch {
    if (!$targetExisted -and (Test-Path -LiteralPath $target -PathType Container)) {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output $_.Exception.Message
    Write-AxTaskResult -Status 'failed' -ExitCode 1 -Message $_.Exception.Message
    exit 1
}
