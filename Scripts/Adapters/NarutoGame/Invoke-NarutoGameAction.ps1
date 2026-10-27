[CmdletBinding()]
param(
    # 参数一律"可空 + 体内校验"：宿主在绑定阶段抛的错是系统本地代码页（GBK），
    # AxTools 按 UTF-8 读会变乱码；体内 throw 走任务协议才看得清。
    [string]$Action = '',
    [AllowEmptyString()][string]$ProjectRoot = '',
    [string]$DevelopmentExecutable = '',
    [string]$ReleaseExecutable = '',
    [string]$GamePackageRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$ReleaseNotes = '',
    [ValidateSet('Windows', 'Android')][string]$Platform = 'Windows',
    [AllowEmptyString()][string]$FragmentOutputDirectory = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
# 真正的实现在 ..\GameUpload\Invoke-GameUploadAction.ps1（三个游戏共用），
# 这里只带本游戏在下载站上的产品段名和版本标记文件名。
$shared = Join-Path (Split-Path -Parent $PSScriptRoot) 'GameUpload\Invoke-GameUploadAction.ps1'
& $shared @PSBoundParameters -ProductSegment 'narutobp' -VersionMarkerName 'NarutoBP.version.json'
exit $LASTEXITCODE
