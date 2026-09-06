[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryUrl,
    [Parameter(Mandatory)][string]$DestinationPath,
    [Parameter(Mandatory)][string]$ExpectedDirectoryName,
    [Parameter(Mandatory)][string]$ExpectedMarkersJson
)

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $scriptsRoot 'Common\AxTaskProtocol.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1') -Force

function ConvertTo-NormalizedRemote([string]$Value) {
    $normalized = $Value.Trim().Replace('\', '/').TrimEnd('/')
    if ($normalized.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 4)
    }
    return $normalized
}

$target = [IO.Path]::GetFullPath($DestinationPath)
$parent = Split-Path -Parent $target
$targetExisted = Test-Path -LiteralPath $target -PathType Container
try {
    Set-AxTaskCancellationMode -Mode 'stop' -Message '可以停止项目下载。'
    if (!(Test-Path -LiteralPath $parent -PathType Container)) {
        throw "下载父目录不存在：$parent"
    }
    if (![string]::Equals(
        [IO.Path]::GetFileName($target),
        $ExpectedDirectoryName,
        [StringComparison]::Ordinal)) {
        throw "目标目录名称不匹配：$target"
    }
    if (Test-Path -LiteralPath $target) {
        if (!(Test-Path -LiteralPath $target -PathType Container)) {
            throw "目标路径已经被文件占用：$target"
        }
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
            throw "目标路径不是空目录：$target"
        }
    }

    $markers = @($ExpectedMarkersJson | ConvertFrom-Json)
    if ($markers.Count -eq 0) { throw '没有配置项目特征。' }
    $git = Resolve-AxCommand -Name 'git'
    Write-AxTaskProgress -Stage 'clone' -Percent 10 -Message '正在从 GitHub 下载项目...' -Detail $target
    $cloneExitCode = 0
    Invoke-AxExternalCommand `
        -FilePath $git `
        -ArgumentList @('clone','--origin','origin','--progress',$RepositoryUrl,$target) `
        -WorkingDirectory $parent `
        -ExitCode ([ref]$cloneExitCode)
    Assert-AxExternalSuccess -ExitCode $cloneExitCode -Operation 'GitHub 项目下载'

    if (!(Test-Path -LiteralPath (Join-Path $target '.git') -PathType Container)) {
        throw "下载结果缺少 Git 元数据：$target"
    }
    foreach ($marker in $markers) {
        if ([IO.Path]::IsPathRooted($marker) -or $marker.Split([char[]]'\/') -contains '..') {
            throw "项目特征路径不安全：$marker"
        }
        if (!(Test-Path -LiteralPath (Join-Path $target $marker))) {
            throw "下载结果缺少项目特征：$marker"
        }
    }

    $originOutput = @(& $git -C $target remote get-url origin 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0 -or $originOutput.Count -ne 1) {
        throw '无法读取下载项目的 origin。'
    }
    if (![string]::Equals(
        (ConvertTo-NormalizedRemote $originOutput[0]),
        (ConvertTo-NormalizedRemote $RepositoryUrl),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "下载项目的 origin 不匹配：$($originOutput[0])"
    }

    Write-AxTaskProgress -Stage 'verify' -Percent 100 -Message 'GitHub 项目下载并校验完成。' -Detail $target
    Write-AxTaskEvent -Data ([ordered]@{ type='artifact'; kind='source-root'; path=$target })
    Write-AxTaskResult -Status 'success' -ExitCode 0 -Message "项目已下载：$target"
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
