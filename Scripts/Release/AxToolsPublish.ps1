# AxTools 发布链共享实现：共享工具 + Gitee/GitHub 两个通道。入口见 Publish-AxTools*.ps1。
#
# 与 ZD 工具箱（CrossingVoidZDTool\Scripts\ZDToolboxPublish.ps1）同构，差异只有身份与仓库：
#   · Gitee 仓库/分支：xiaojie578/AxTools，master（Gitee 侧仓库是 auto_init 建的，默认分支是 master）
#   · GitHub 仓库/分支：kirito0000001/AxTools，main
#   · 三件产物名：AxTools-v<ver>-win-x64.zip / .sha256.txt / toolbox-update.json
# 两条通道的差异是**有意**的，和 ZD 一致：
#   · Gitee 每仓库附件合计 1 GB，所以发新版本会清理旧 Release 的附件（只留最近 3 个）；
#   · GitHub 没有这条约束，只上传、从不删除，旧版本永久保留。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($null -ne (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue)) { $PSStyle.OutputRendering = 'PlainText' }
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Import-Module (Join-Path $PSScriptRoot 'AxToolsReleaseCommon.psm1') -Force
$axScriptRoot = $PSScriptRoot
$ToolboxStableKey = 'AxTools'
$DisplayName = 'AxTools'
$AttachmentLimitBytes = 100MB
$RepositoryAttachmentBudgetBytes = 1GB
$RateLimitBodySignature = 'Rate Limit Exceeded'
$ToolboxManifestFileName = 'toolbox-update.json'
$ArtifactRecordFileName = 'artifacts.json'
$GiteeApiBase = 'https://gitee.com/api/v5/repos'
$GiteeWebBase = 'https://gitee.com'
$Script:RequestCount = 0
$Script:RateLimitRetries = 0
$Script:MaxRateLimitRetries = 6
$Script:TransientRetries = 0
$Script:MaxTransientRetries = 4
$TransientStatusCodes = @(408, 429, 500, 502, 503, 504)
$Script:ProgressFrom = 0
$Script:ProgressTo = 100
$Script:LastPercent = 0
$Script:Token = ''

function Set-AxProgressRange {
    # 编排层用它把两条通道映射到 0–50 / 50–100，整条"完整发布"才是一条单调前进的进度条。
    param([Parameter(Mandatory)][double]$From, [Parameter(Mandatory)][double]$To)
    $Script:ProgressFrom = $From
    $Script:ProgressTo = $To
}
function Write-AxReleaseProgress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][double]$Percent,
        [Parameter(Mandatory)][string]$Message,
        [string]$Detail
    )
    $scaled = $Script:ProgressFrom + (($Script:ProgressTo - $Script:ProgressFrom) * ([Math]::Max(0, [Math]::Min(100, $Percent)) / 100.0))
    $Script:LastPercent = $scaled
    $payload = [ordered]@{
        type    = 'progress'
        stage   = $Stage
        percent = [Math]::Round($scaled, 1)
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
function Read-AxJsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Get-AxJsonProperty {
    # 安全读一个**可能不存在**的 JSON 字段：本脚本开着 StrictMode，
    # 直接写 $manifest.delta 会在"这一版没有增量包"时抛「找不到属性 delta」。
    param(
        [Parameter(Mandatory)][AllowNull()]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Get-AxErrorBodyText {
    param($ErrorRecord)
    try {
        if ($null -ne $ErrorRecord.ErrorDetails -and
            ![string]::IsNullOrWhiteSpace([string]$ErrorRecord.ErrorDetails.Message)) {
            return [string]$ErrorRecord.ErrorDetails.Message
        }
    }
    catch { }
    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -ne $response) {
            $stream = $response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = [IO.StreamReader]::new($stream)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
    }
    catch { }
    return [string]$ErrorRecord.Exception.Message
}
function Get-AxBodySummary {
    # 错误正文可能是 HTML，日志里不能直接吐几 KB 的页面。
    param([string]$Body, [int]$MaxLength = 400)
    if ([string]::IsNullOrWhiteSpace($Body)) { return '(空响应体)' }
    $text = $Body -replace '<[^>]+>', ' '
    $text = ($text -replace '\s+', ' ').Trim()
    if ($text.Length -gt $MaxLength) { $text = $text.Substring(0, $MaxLength) + '…' }
    return $text
}
function Get-AxStatusCode {
    param($ErrorRecord)
    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -ne $response) { return [int]$response.StatusCode }
    }
    catch { }
    return 0
}
function Invoke-AxGiteeRequest {
    # 所有 Gitee API 调用的唯一出口：限流（403 + Rate Limit Exceeded）与瞬时故障（5xx / 无响应）都在这里退避重试。
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Patch', 'Delete')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Query = @{},
        $Body = $null,
        [string]$Operation = '',
        [switch]$AllowNotFound
    )
    $uri = "$GiteeApiBase/$GiteeRepository"
    if (![string]::IsNullOrWhiteSpace($Path)) { $uri = "$uri/$Path" }
    $attempt = 0
    while ($true) {
        $attempt++
        $Script:RequestCount++
        try {
            if ($Method -eq 'Get') {
                $parameters = @{} + $Query
                $parameters.access_token = $Script:Token
                return Invoke-RestMethod `
                    -Method Get `
                    -Uri $uri `
                    -Body $parameters `
                    -Headers @{ 'User-Agent' = 'AxTools-ReleasePublish/1.0' } `
                    -TimeoutSec 60 `
                    -UseBasicParsing
            }
            $payload = if ($null -eq $Body) { @{} } else { @{} + $Body }
            $payload.access_token = $Script:Token
            return Invoke-RestMethod `
                -Method $Method `
                -Uri $uri `
                -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
                -Body $payload `
                -Headers @{ 'User-Agent' = 'AxTools-ReleasePublish/1.0' } `
                -TimeoutSec 120 `
                -UseBasicParsing
        }
        catch {
            $status = Get-AxStatusCode -ErrorRecord $_
            $bodyText = Get-AxErrorBodyText -ErrorRecord $_
            $label = if ([string]::IsNullOrWhiteSpace($Operation)) { "$Method $Path" } else { $Operation }
            if ($status -eq 403 -and $bodyText -like "*$RateLimitBodySignature*") {
                $Script:RateLimitRetries++
                if ($Script:RateLimitRetries -gt $Script:MaxRateLimitRetries) {
                    throw "$label 被 Gitee 限流，已重试 $Script:MaxRateLimitRetries 次仍未通过：$(Get-AxBodySummary -Body $bodyText)"
                }
                $wait = [Math]::Min(30, 5 * $attempt) * $attempt
                Write-AxReleaseProgress -Stage 'rate-limit' -Percent $Script:LastPercent `
                    -Message "Gitee 限流，等待 $wait 秒后重试（第 $Script:RateLimitRetries 次）" `
                    -Detail $label
                Start-Sleep -Seconds $wait
                continue
            }
            if ($status -eq 403) {
                throw ("$label 被 Gitee 拒绝（403，无权限或令牌无效）。服务器说：$(Get-AxBodySummary -Body $bodyText)`n" +
                    "  排查：令牌是否过期、是否属于仓库 $GiteeRepository、是否有仓库读写权限。")
            }
            if ($status -eq 404) {
                if ($AllowNotFound) { return $null }
                throw "$label 返回 404。服务器说：$(Get-AxBodySummary -Body $bodyText)"
            }
            if ($TransientStatusCodes -contains $status -or $status -eq 0) {
                $Script:TransientRetries++
                $statusLabel = if ($status -eq 0) { '无 HTTP 响应' } else { "HTTP $status" }
                if ($attempt -le $Script:MaxTransientRetries) {
                    $wait = [int][Math]::Pow(2, $attempt)
                    Write-AxReleaseProgress -Stage 'retry' -Percent $Script:LastPercent `
                        -Message "Gitee 瞬时故障（$statusLabel），$wait 秒后重试（$attempt/$Script:MaxTransientRetries）" `
                        -Detail $label
                    Start-Sleep -Seconds $wait
                    continue
                }
                if ($status -eq 0) {
                    throw ("$label —— 连不上 Gitee（重试 $Script:MaxTransientRetries 次都没有响应）：" +
                        "$([string]$_.Exception.Message)`n" +
                        "  先确认本机能打开 gitee.com，然后原样重跑：已传成功的附件会核对内容后跳过。")
                }
                throw ("$label —— Gitee 服务端错误 HTTP $status（重试 $Script:MaxTransientRetries 次都没过）：" +
                    "$(Get-AxBodySummary -Body $bodyText -MaxLength 160)`n" +
                    "  这不是本机或脚本的问题，原样重跑同一条命令即可：" +
                    "已传成功的附件会核对内容后跳过，清单仍是最后一步。")
            }
            throw "$label 失败（HTTP $status）。服务器说：$(Get-AxBodySummary -Body $bodyText)"
        }
    }
}
function Get-AxGiteeList {
    # 列表接口默认只返回 20 条，必须带 per_page=100；
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$PerPage = 100,
        [int]$MaxPages = 30
    )
    $all = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le $MaxPages; $page++) {
        $item = Invoke-AxGiteeRequest -Method Get -Path $Path -Query @{ per_page = $PerPage; page = $page } `
            -Operation "$Path（第 $page 页）"
        $batch = @($item)
        if ($batch.Count -eq 0) { break }
        foreach ($entry in $batch) { $all.Add($entry) }
        if ($batch.Count -lt $PerPage) { break }
    }
    return $all
}
function Get-AxAttachmentUrl {
    param([Parameter(Mandatory)][int]$ReleaseId, [Parameter(Mandatory)][string]$FileName)
    $assets = Get-AxGiteeList -Path "releases/$ReleaseId/attach_files"
    $match = @($assets | Where-Object { [string]$_.name -eq $FileName } | Select-Object -First 1)
    if ($match.Count -eq 0) { return $null }
    return [string]$match[0].browser_download_url
}
function Get-AxReleaseByTag {
    param([Parameter(Mandatory)][string]$Tag)
    $releases = Get-AxGiteeList -Path 'releases'
    $match = @($releases | Where-Object { [string]$_.tag_name -eq $Tag } | Select-Object -First 1)
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}
function Confirm-AxRelease {
    # 幂等确保 Release 存在：按 tag 精确匹配，存在就 PATCH，不存在才 POST。
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][AllowEmptyString()][string]$BodyText,
        [Parameter(Mandatory)][bool]$Prerelease
    )
    $existing = Get-AxReleaseByTag -Tag $Tag
    $effectiveBody = if ([string]::IsNullOrWhiteSpace($BodyText)) { "本次更新未填写公告。（$Tag）" } else { $BodyText }
    $payload = @{
        tag_name         = $Tag
        name             = $Title
        body             = $effectiveBody
        prerelease       = $Prerelease.ToString().ToLowerInvariant()
        target_commitish = $GiteeBranch
    }
    if ($null -eq $existing) {
        Write-Host "创建 Gitee Release：$Tag（prerelease=$Prerelease）" -ForegroundColor Cyan
        return Invoke-AxGiteeRequest -Method Post -Path 'releases' -Body $payload -Operation "创建 Release $Tag"
    }
    $sameName = [string]$existing.name -eq $Title
    $sameBody = [string]$existing.body -eq $effectiveBody
    $samePrerelease = [bool]$existing.prerelease -eq [bool]$Prerelease
    if ($sameName -and $sameBody -and $samePrerelease) {
        Write-Host "Gitee Release 已存在且字段一致，跳过更新：$Tag（prerelease=$Prerelease）" -ForegroundColor DarkGray
        return $existing
    }
    Write-Host ("更新已存在的 Gitee Release：$Tag（prerelease $($existing.prerelease) → $Prerelease" +
        "；名称/公告同步中）") -ForegroundColor Cyan
    return Invoke-AxGiteeRequest -Method Patch -Path ("releases/{0}" -f [int]$existing.id) -Body $payload `
        -Operation "更新 Release $Tag"
}
function Remove-AxGiteeAsset {
    param([Parameter(Mandatory)][int]$ReleaseId, [Parameter(Mandatory)][int]$AssetId, [Parameter(Mandatory)][string]$Name)
    Write-Host "  删除同名旧附件：$Name" -ForegroundColor Yellow
    Invoke-AxGiteeRequest -Method Delete -Path ("releases/{0}/attach_files/{1}" -f $ReleaseId, $AssetId) `
        -Operation "删除附件 $Name" -AllowNotFound | Out-Null
}
function Send-AxGiteeAsset {
    # 上传一个附件：必须 curl.exe + --fail-with-body + 显式 ;filename=（Gitee 不看文件名就存成随机名）。
    param(
        [Parameter(Mandatory)][int]$ReleaseId,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ExistingAssets,
        [Parameter(Mandatory)][double]$Progress,
        [switch]$ReplaceAlways
    )
    if (!(Test-Path -LiteralPath $LocalPath -PathType Leaf)) { throw "要上传的文件不存在：$LocalPath" }
    $file = Get-Item -LiteralPath $LocalPath
    if ($file.Length -gt $AttachmentLimitBytes) {
        throw ("附件超过 Gitee 单附件上限 100 MiB：$($file.Name) = " +
            "$([Math]::Round($file.Length / 1MB, 2)) MB。请剔除可下架内容、或让出包脚本自动分卷。")
    }
    $existing = @($ExistingAssets | Where-Object { [string]$_.name -eq $AssetName } | Select-Object -First 1)
    if ($existing.Count -gt 0) {
        if ([int64]$existing[0].size -eq [int64]$file.Length -and !$ReplaceAlways) {
            Write-Host "  已存在同名同大小附件，跳过上传：$AssetName（$([Math]::Round($file.Length / 1MB, 2)) MB）" -ForegroundColor DarkGray
            return $false
        }
        Write-AxReleaseProgress -Stage 'checkpoint-reset' -Percent $Progress `
            -Message "先删后传：$AssetName" `
            -Detail ($(if ($ReplaceAlways) { '清单类文件每次都重传' }
                else { "大小不同：线上 $([int64]$existing[0].size) / 本地 $([int64]$file.Length) 字节" }))
        Remove-AxGiteeAsset -ReleaseId $ReleaseId -AssetId ([int]$existing[0].id) -Name $AssetName
    }
    $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $curl) { throw '没有找到 curl.exe，无法上传 Gitee Release 附件。' }
    Write-AxReleaseProgress -Stage 'upload' -Percent $Progress -Message "正在上传 $AssetName（$([Math]::Round($file.Length / 1MB, 2)) MB）"
    $uri = "$GiteeApiBase/$GiteeRepository/releases/$ReleaseId/attach_files"
    & $curl.Source --http1.1 --fail-with-body --silent --show-error `
        --retry 10 --retry-delay 5 --retry-all-errors `
        -H 'Expect:' `
        --request POST `
        --form "access_token=$Script:Token" `
        --form "file=@$($file.FullName);filename=$AssetName" `
        $uri
    $exit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exit -ne 0) {
        $advice = switch ($exit) {
            22 { 'HTTP 层失败 —— 上面那行正文就是平台的答复（403 限流/无权限、5xx 服务端故障）。' }
            55 { '发送中断（连接被重置）—— Gitee 在长时间大包上传上不稳。' }
            56 { '接收中断（连接被重置）。' }
            28 { '请求超时。' }
            default { '多为网络中断。' }
        }
        throw ("上传 $AssetName 失败（curl 退出码 $exit）。$advice`n" +
            "  重跑是安全的：已传好的附件会核对内容后跳过，清单仍是最后一步。")
    }
    Write-Host "  已上传：$AssetName" -ForegroundColor Green
    return $true
}
function Assert-AxReleaseAssets {
    # 传完复查附件名齐全、大小与本地一致，缺一即中止。
    param(
        [Parameter(Mandatory)][int]$ReleaseId,
        [Parameter(Mandatory)][AllowEmptyCollection()][hashtable[]]$Expected
    )
    $assets = Get-AxGiteeList -Path "releases/$ReleaseId/attach_files"
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($item in $Expected) {
        $match = @($assets | Where-Object { [string]$_.name -eq $item.name } | Select-Object -First 1)
        if ($match.Count -eq 0) {
            $problems.Add("线上缺少附件：$($item.name)")
            continue
        }
        if ([int64]$match[0].size -ne [int64]$item.size) {
            $problems.Add("附件大小不一致：$($item.name) 线上 $([int64]$match[0].size) / 本地 $([int64]$item.size)")
        }
    }
    if ($problems.Count -gt 0) {
        throw "附件复查未通过，已中止（绝不更新清单）：`n  - " + ($problems -join "`n  - ")
    }
    Write-Host ("  附件复查通过：$($Expected.Count) 个（" + (($Expected | ForEach-Object { $_.name }) -join '、') + '）') -ForegroundColor Green
}
function Remove-AxGiteeTag {
    # 删掉 Gitee 上的一个 tag。本地没有配 Gitee remote 时直接推 URL。
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$Repository
    )
    $env:GIT_TERMINAL_PROMPT = '0'
    $remoteName = ''
    try {
        foreach ($line in @(& git -C $ProjectRoot remote -v 2>$null)) {
            if ($line -match '^(\S+)\s+(\S+)\s+\(push\)$' -and $Matches[2] -match [regex]::Escape($Repository)) {
                $remoteName = $Matches[1]
                break
            }
        }
    }
    catch { }
    $target = if (![string]::IsNullOrWhiteSpace($remoteName)) { $remoteName } else { "https://gitee.com/$Repository.git" }
    $output = & git -C $ProjectRoot push $target ":refs/tags/$Tag" 2>&1 | Out-String
    $exit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exit -ne 0) {
        Write-Host ("    警告：删 tag $Tag 失败（git 退出码 $exit）——可稍后在 Gitee 网页「标签」页手动删。") -ForegroundColor Yellow
        return $false
    }
    Write-Host "    已删除远端 tag：$Tag" -ForegroundColor DarkGray
    return $true
}
function Prune-AxOldReleaseAssets {
    # 只保留最近 N 个**带附件的** Release（Gitee 每仓库附件合计 1 GB）。
    param(
        [Parameter(Mandatory)][int]$KeepCount,
        [Parameter(Mandatory)][string]$CurrentTag,
        [switch]$DeleteReleaseRecords,
        [string]$ProjectRoot = '',
        [string]$Repository = '',
        [switch]$DryRun
    )
    $releases = @(Get-AxGiteeList -Path 'releases')
    if ($releases.Count -eq 0) { return }
    $withAssets = [Collections.Generic.List[object]]::new()
    $totalBytes = [int64]0
    foreach ($release in $releases) {
        $assets = @(Get-AxGiteeList -Path ("releases/{0}/attach_files" -f [int]$release.id))
        if ($assets.Count -eq 0) { continue }
        $size = [int64]0
        foreach ($asset in $assets) { $size += [int64]$asset.size }
        $totalBytes += $size
        $withAssets.Add([pscustomobject]@{
                Id      = [int]$release.id
                Tag     = [string]$release.tag_name
                Created = [string]$release.created_at
                Size    = $size
                Assets  = $assets
            })
    }
    Write-Host ("仓库附件总量：{0:N2} MB（上限 1024 MB）；带附件的 Release {1} 个" -f ($totalBytes / 1MB), $withAssets.Count)
    $sorted = @($withAssets | Sort-Object Created -Descending)
    $keepTags = @($CurrentTag) + @($sorted | Select-Object -First $KeepCount | ForEach-Object { $_.Tag })
    $victims = @($sorted | Where-Object { $keepTags -notcontains $_.Tag })
    if ($victims.Count -eq 0) {
        Write-Host ("未超过保留数（{0} 个带附件的 Release），无需清理。" -f $KeepCount) -ForegroundColor DarkGray
        return
    }
    Write-Host ("保留最近 {0} 个（含本次 $CurrentTag），清理 {1} 个更旧的版本：" -f $KeepCount, $victims.Count) -ForegroundColor Yellow
    if ($DryRun) {
        foreach ($victim in $victims) {
            Write-Host ("  [演练] 会删 $($victim.Tag)：{0} 个附件（{1:N2} MB）→ 附件 + Release 记录 + 远端 tag" -f `
                    $victim.Assets.Count, ($victim.Size / 1MB)) -ForegroundColor DarkGray
        }
        Write-Host '演练结束：什么都没删。' -ForegroundColor Yellow
        return
    }
    foreach ($victim in $victims) {
        Write-Host ("  清理 $($victim.Tag)：{0} 个附件（{1:N2} MB）" -f $victim.Assets.Count, ($victim.Size / 1MB))
        foreach ($asset in $victim.Assets) {
            Remove-AxGiteeAsset -ReleaseId $victim.Id -AssetId ([int]$asset.id) -Name ([string]$asset.name)
        }
        if ($DeleteReleaseRecords) {
            Invoke-AxGiteeRequest -Method Delete -Path ("releases/{0}" -f $victim.Id) `
                -Operation "删除旧 Release $($victim.Tag)" -AllowNotFound | Out-Null
            Write-Host "    已删除 Release 记录：$($victim.Tag)" -ForegroundColor DarkGray
            if (![string]::IsNullOrWhiteSpace($ProjectRoot) -and ![string]::IsNullOrWhiteSpace($Repository)) {
                [void](Remove-AxGiteeTag -Tag $victim.Tag -ProjectRoot $ProjectRoot -Repository $Repository)
            }
        }
    }
}
function Invoke-AxAnonymousRequest {
    # 不带任何凭据的公开请求，只用于收尾自检那条下载路径。
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('Get', 'Head')][string]$Method = 'Get'
    )
    # 只试一次、20 秒超时：这一步只是"读一次线上清单"，产物早就上传完了。
    try {
        return Invoke-WebRequest -Method $Method -Uri $Uri `
            -Headers @{ 'User-Agent' = 'AxTools-ReleasePublish/1.0'; 'Accept-Encoding' = 'identity' } `
            -TimeoutSec 20 -UseBasicParsing
    }
    catch {
        $status = Get-AxStatusCode -ErrorRecord $_
        $summary = Get-AxBodySummary -Body (Get-AxErrorBodyText -ErrorRecord $_)
        throw ("自检读不到线上清单（HTTP $status）：$Uri → $summary`n" +
            "  已上传的附件不受影响；可重跑一次，或加 -SkipSelfCheck / -TolerateSelfCheckFailure 跳过这一步。")
    }
}
function Invoke-AxSelfCheck {
    # 模拟客户端：匿名读一次线上清单，核对版本 / 通道 / 整包 sha256。
    # **只读清单**，不逐个 HEAD 附件、不打 API（Gitee 匿名 API 是 60 次/小时按 IP 计的）。
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ManifestFileName,
        [Parameter(Mandatory)][string]$LocalZipPath,
        [Parameter(Mandatory)][string]$LocalManifestPath
    )
    Write-AxReleaseProgress -Stage 'self-check' -Percent 96 -Message '正在匿名读一次线上清单核对（只读清单，不打 API）'
    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $manifestUrl = "$GiteeWebBase/$GiteeRepository/releases/download/$Tag/$ManifestFileName`?t=$cacheBuster"
    $response = Invoke-AxAnonymousRequest -Uri $manifestUrl
    $remoteText = if ($response.Content -is [byte[]]) {
        [Text.Encoding]::UTF8.GetString($response.Content)
    }
    else { [string]$response.Content }
    $remote = $remoteText | ConvertFrom-Json
    $local = Read-AxJsonFile -Path $LocalManifestPath
    if ([string]$remote.version -ne [string]$local.version) {
        throw "自检失败：线上清单版本 $($remote.version) 与本地 $($local.version) 不一致。"
    }
    if ([string]$remote.channel -ne [string]$local.channel) {
        throw "自检失败：线上清单通道 $($remote.channel) 与本地 $($local.channel) 不一致。"
    }
    $remoteSha = [string]$remote.assets[0].sha256
    $localSha = Get-AxSha256Text -Path $LocalZipPath
    if (![string]::Equals($remoteSha, $localSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "自检失败：线上清单里的 sha256=$remoteSha，本地 zip 实际=$localSha。"
    }
    Write-Host '自检通过（匿名、只读清单）：线上清单可匿名读取，版本 / 通道 / 整包 sha256 与本地产物一致。' -ForegroundColor Green
}

function Invoke-AxToolsGiteePublish {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [string]$Notes = '',
        [string]$StagingDirectory = '',
        [string]$ProjectRoot = (Resolve-Path (Join-Path $axScriptRoot '..\..')).Path,
        [string]$GiteeRepository = 'xiaojie578/AxTools',
        [string]$GiteeBranch = 'master',
        [string]$GiteeAccessToken = '',
        [ValidateSet('win-x64')]
        [string]$Runtime = 'win-x64',
        [switch]$DryRun,
        [switch]$ForceRebuild,
        [int64]$PartSizeBytes = 0,
        [switch]$ExcludeAiRuntime,
        # 跳过出包步骤：产物目录里必须已有三件产物（GUI 的「单独上传」走这条）。
        [switch]$SkipBuild,
        [switch]$SkipTests,
        [switch]$SkipVersionWrite,
        [switch]$NoDelta,
        [int]$KeepReleasesWithAssets = 3,
        [switch]$SkipPrune,
        [switch]$KeepOldReleaseRecords,
        [switch]$PruneOnly,
        [switch]$SkipSelfCheck,
        [switch]$TolerateSelfCheckFailure
    )
    $exitCode = 0
    $tag = "v$($Version.Trim())"
    try {
        $targetVersion = $Version.Trim().TrimStart('v')
        if ($targetVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?$') {
            throw "版本号必须形如 1.0.1 或 1.0.1-Beta：$targetVersion"
        }
        $prerelease = $targetVersion.Contains('-')
        $channel = if ($prerelease) { 'beta' } else { 'stable' }
        $root = Get-AxNormalizedPath -Path $ProjectRoot
        $staging = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
            Join-Path $root (Join-Path 'Artifacts\ReleaseAssets' "v$targetVersion")
        }
        else { Get-AxNormalizedPath -Path $StagingDirectory }
        $zipFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.zip"
        $shaFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.sha256.txt"
        $zipPath = Join-Path $staging $zipFileName
        $shaPath = Join-Path $staging $shaFileName
        $manifestPath = Join-Path $staging $ToolboxManifestFileName
        $buildScript = Join-Path $axScriptRoot 'Build-AxToolsUpdatePackage.ps1'
        Write-Host "== 发布 $DisplayName $tag（通道 $channel）==" -ForegroundColor Cyan
        Write-Host "   产物目录：$staging"
        if ($PruneOnly) {
            Write-Host ("== 只清理旧版本（不发版）：保留最近 {0} 个带附件的 Release，保护 {1} ==" -f `
                    $KeepReleasesWithAssets, $tag) -ForegroundColor Cyan
            $Script:Token = Get-AxToolsGiteeToken -Explicit $GiteeAccessToken
            Prune-AxOldReleaseAssets -KeepCount $KeepReleasesWithAssets -CurrentTag $tag `
                -DeleteReleaseRecords:(!$KeepOldReleaseRecords) -ProjectRoot $root -Repository $GiteeRepository `
                -DryRun:$DryRun
            Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message "旧版本清理完成（受保护的 tag：$tag）"
            Write-Host '清理完成。' -ForegroundColor Green
            return 0
        }

        if (!$SkipBuild) {
            if (!(Test-Path -LiteralPath $buildScript -PathType Leaf)) {
                throw "找不到出包脚本：$buildScript"
            }
            $buildArguments = @{
                Version          = $targetVersion
                ProjectRoot      = $root
                StagingDirectory = $staging
                Runtime          = $Runtime
                Notes            = $Notes
            }
            if ($ExcludeAiRuntime) { $buildArguments.ExcludeAiRuntime = $true }
            if ($ForceRebuild) { $buildArguments.ForceRebuild = $true }
            if ($SkipTests) { $buildArguments.SkipTests = $true }
            if ($SkipVersionWrite) { $buildArguments.SkipVersionWrite = $true }
            if ($NoDelta) { $buildArguments.NoDelta = $true }
            if ($PartSizeBytes -gt 0) { $buildArguments.PartSizeBytes = $PartSizeBytes }
            # 显式丢弃"成功流"：出包脚本的进度都走 Write-Host / ::axtools 协议行，
            # 真正会落到成功流上的只有原生命令的残留输出 —— 那种东西一旦顺着管道回来，
            # 就会把本函数的返回值（退出码）变成一串混合内容。
            & $buildScript @buildArguments | Out-Null
            $buildExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($buildExit -ne 0) { throw "出包失败，退出码 $buildExit。" }
        }
        else {
            Write-Host '按 -SkipBuild：不重新出包，直接用产物目录里已有的三件产物。' -ForegroundColor DarkGray
        }
        foreach ($path in @($zipPath, $shaPath, $manifestPath)) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发布物：$path" }
        }
        $zipItem = Get-Item -LiteralPath $zipPath
        $zipSha = Get-AxSha256Text -Path $zipPath
        $manifest = Read-AxJsonFile -Path $manifestPath

        # 公告为空 → 用清单里那句自动说明（出包脚本在没给 -Notes 时会写一句）。
        $manifestNotes = [string](Get-AxJsonProperty -InputObject $manifest -Name 'releaseNotes')
        if ([string]::IsNullOrWhiteSpace($Notes) -and ![string]::IsNullOrWhiteSpace($manifestNotes)) {
            $Notes = $manifestNotes
        }
        if ([string]$manifest.toolboxStableKey -ne $ToolboxStableKey) {
            throw "清单里的 toolboxStableKey 不是 $ToolboxStableKey：$($manifest.toolboxStableKey)"
        }
        if ([string]$manifest.version -ne $targetVersion) {
            throw "清单版本 $($manifest.version) 与目标版本 $targetVersion 不一致。"
        }
        $manifestChannel = [string](Get-AxJsonProperty -InputObject $manifest -Name 'channel')
        if ($manifestChannel -ne $channel) {
            throw "清单通道 $($manifestChannel) 与版本号判定的 $channel 不一致（通道必须与 prerelease 一致）。"
        }
        if ([string]$manifest.assets[0].fileName -ne $zipFileName) {
            throw "清单里的文件名 $($manifest.assets[0].fileName) 与附件名 $zipFileName 不一致。"
        }
        if ([string]$manifest.assets[0].sha256 -ne $zipSha) {
            throw "清单里的 sha256 与 zip 实际哈希不一致。"
        }
        if ([int64]$manifest.assets[0].sizeBytes -ne [int64]$zipItem.Length) {
            throw "清单里的 sizeBytes 与 zip 实际大小不一致。"
        }
        if (![string]::IsNullOrWhiteSpace([string]$manifest.assets[0].downloadUrl)) {
            throw "清单里的 downloadUrl 必须留空字符串（客户端自己拼下载地址）。"
        }
        $parts = @()
        $partsProperty = $manifest.assets[0].PSObject.Properties['parts']
        if ($null -ne $partsProperty -and $null -ne $partsProperty.Value) {
            $parts = @($partsProperty.Value)
        }
        $payloads = [Collections.Generic.List[hashtable]]::new()
        if ($parts.Count -gt 0) {
            $partSum = [int64]0
            foreach ($part in $parts) {
                $partName = [string]$part.fileName
                $partPath = Join-Path $staging $partName
                if (!(Test-Path -LiteralPath $partPath -PathType Leaf)) {
                    throw "清单里列了分卷 $partName，但产物目录里没有它：$partPath"
                }
                $partItem = Get-Item -LiteralPath $partPath
                if ($partItem.Length -gt $AttachmentLimitBytes) {
                    throw ("分卷 $partName = $([Math]::Round($partItem.Length / 1MB, 2)) MB，超过单附件 100 MiB。")
                }
                if ([int64]$partItem.Length -ne [int64]$part.sizeBytes) {
                    throw "分卷 $partName 大小与清单不一致：本地 $($partItem.Length) / 清单 $($part.sizeBytes)。"
                }
                $partSha = Get-AxSha256Text -Path $partPath
                if (![string]::Equals($partSha, [string]$part.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "分卷 $partName 的 SHA256 与清单不一致。"
                }
                $partSum += [int64]$partItem.Length
                $payloads.Add(@{ name = $partName; path = $partPath; size = [int64]$partItem.Length })
            }
            if ($partSum -ne [int64]$zipItem.Length) {
                throw "各分卷字节数之和（$partSum）与整包（$($zipItem.Length)）不一致，停止发布。"
            }
        }
        else {
            if ([int64]$zipItem.Length -gt $AttachmentLimitBytes) {
                throw ("zip 超过 Gitee 单附件上限 100 MiB：$([Math]::Round($zipItem.Length / 1MB, 2)) MB，" +
                    "且清单里没有 parts。请重新出包（出包脚本会自动分卷）。")
            }
            $payloads.Add(@{ name = $zipFileName; path = $zipPath; size = [int64]$zipItem.Length })
        }
        # 增量包：清单里声明了 delta 就把它一起传上去。
        # 客户端只在"本机版本 == baseVersion"时才用它；整包仍在 assets[] 里兜底。
        $deltaNode = Get-AxJsonProperty -InputObject $manifest -Name 'delta'
        $deltaName = if ($null -eq $deltaNode) { '' } else { [string]$deltaNode.fileName }
        if (![string]::IsNullOrWhiteSpace($deltaName)) {
            $deltaPath = Join-Path $staging $deltaName
            if (!(Test-Path -LiteralPath $deltaPath -PathType Leaf)) {
                throw "清单里声明了增量包 $deltaName，但产物目录里没有它：$deltaPath（请重新出包，或出包时加 -NoDelta）。"
            }
            $deltaItem = Get-Item -LiteralPath $deltaPath
            if ([int64]$deltaItem.Length -gt $AttachmentLimitBytes) {
                throw ("增量包 $deltaName = $([Math]::Round($deltaItem.Length / 1MB, 2)) MB，超过单附件 100 MiB。")
            }
            if ([int64]$deltaItem.Length -ne [int64]$deltaNode.sizeBytes) {
                throw "增量包大小与清单不一致：本地 $($deltaItem.Length) / 清单 $($deltaNode.sizeBytes)。"
            }
            $deltaSha = Get-AxSha256Text -Path $deltaPath
            if (![string]::Equals($deltaSha, [string]$deltaNode.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "增量包 SHA256 与清单不一致（本地 $deltaSha / 清单 $($deltaNode.sha256)）。"
            }
            $payloads.Add(@{ name = $deltaName; path = $deltaPath; size = [int64]$deltaItem.Length })
            Write-Host ("   增量包：$deltaName（{0:N2} MB，基准 v{1}，变化 {2} 个 / 删除 {3} 个）" -f `
                    ($deltaItem.Length / 1MB), [string]$deltaNode.baseVersion, [int]$deltaNode.changedCount, [int]$deltaNode.deleteCount)
        }

        $shaFileText = ([IO.File]::ReadAllText($shaPath, [Text.Encoding]::UTF8)).TrimEnd("`r", "`n")
        $shaLines = @($shaFileText -split "`r?`n")
        if ($shaLines.Count -lt 1 -or $shaLines[0] -ne "$zipSha  $zipFileName") {
            throw "sha256.txt 第一行必须逐字是 '{整包sha}  {zip文件名}'（两个空格）：$shaFileText"
        }
        $expectedShaLines = $parts.Count + 1
        if ($shaLines.Count -ne $expectedShaLines) {
            throw ("sha256.txt 行数（$($shaLines.Count)）不对：应当是「整包 1 行 + 每卷 1 行」= " +
                "$expectedShaLines 行（清单里 $($parts.Count) 卷）。")
        }
        for ($i = 1; $i -lt $shaLines.Count; $i++) {
            $expected = "$($parts[$i - 1].sha256)  $($parts[$i - 1].fileName)"
            if ($shaLines[$i] -ne $expected) {
                throw "sha256.txt 第 $($i + 1) 行与清单里的分卷对不上：$($shaLines[$i])"
            }
        }
        Write-Host "   版本：$targetVersion（$channel，prerelease=$prerelease）"

        Write-Host "   zip：$([Math]::Round($zipItem.Length / 1MB, 2)) MB / $($zipItem.Length) 字节"
        Write-Host "   sha256：$zipSha"

        if ($parts.Count -gt 0) {
            $maxPart = ($parts | Measure-Object sizeBytes -Maximum).Maximum
            Write-Host "   分卷：$($parts.Count) 卷（最大一卷 $([Math]::Round([int64]$maxPart / 1MB, 2)) MB），客户端按顺序拼回整包"
        }
        if ($DryRun) {
            Set-AxCancellationMode -Mode 'stop' -Message '演练不碰网络。'
            Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message '演练完成：本地产物与清单校验通过，未接触 Gitee'
            Write-Host ''
            Write-Host '演练完成（-DryRun）：以下动作为真实发布时会执行 ——' -ForegroundColor Yellow
            Write-Host "  1. 幂等确保 Release $tag（tag_name=$tag，name=$DisplayName $tag，prerelease=$prerelease，target_commitish=$GiteeBranch）"
            $step = 2
            foreach ($payload in $payloads) {
                Write-Host ("  {0}. 传 {1}（{2:N2} MB）" -f $step, $payload.name, ($payload.size / 1MB))
                $step++
            }
            Write-Host ("  {0}. 传 {1}" -f $step, $shaFileName); $step++
            Write-Host ("  {0}. 复查附件名与大小（共 {1} 个）" -f $step, ($payloads.Count + 2)); $step++
            Write-Host ("  {0}. 最后传 {1}（此后 locked，不可中断）" -f $step, $ToolboxManifestFileName); $step++
            Write-Host ("  {0}. 匿名自检（只读清单）+ 旧附件清理" -f $step)
            Write-Host "  清单内容："
            Write-Host ([IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8))
            return 0
        }
        if (Test-AxCancelled) { throw '用户在发布前取消了操作。' }
        $Script:Token = Get-AxToolsGiteeToken -Explicit $GiteeAccessToken
        Write-AxReleaseProgress -Stage 'release' -Percent 20 -Message "正在幂等确保 Gitee Release：$tag"
        $release = Confirm-AxRelease `
            -Tag $tag `
            -Title "$DisplayName $tag" `
            -BodyText $Notes `
            -Prerelease $prerelease
        $releaseId = [int]$release.id
        if ($releaseId -le 0) { throw "Gitee 没有返回有效的 Release ID：$($release | ConvertTo-Json -Compress)" }

        Set-AxCancellationMode -Mode 'stop' -Message '上传阶段可停止；再次执行会跳过已传好的附件。'
        $assets = @(Get-AxGiteeList -Path "releases/$releaseId/attach_files")
        $expectedNames = [Collections.Generic.List[hashtable]]::new()
        foreach ($payload in $payloads) { $expectedNames.Add(@{ name = $payload.name; size = [int64]$payload.size }) }
        $expectedNames.Add(@{ name = $shaFileName; size = (Get-Item -LiteralPath $shaPath).Length })
        $expectedNames.Add(@{ name = $ToolboxManifestFileName; size = (Get-Item -LiteralPath $manifestPath).Length })
        $missing = [Collections.Generic.List[string]]::new()
        foreach ($expected in $expectedNames) {
            $hit = @($assets | Where-Object {
                    [string]$_.name -eq [string]$expected.name -and [int64]$_.size -eq [int64]$expected.size
                })
            if ($hit.Count -eq 0) { $missing.Add([string]$expected.name) }
        }
        if ($missing.Count -eq 0) {
            Write-Host ("断点检查：$($expectedNames.Count) 个附件都已在线上且大小一致；" +
                "包体跳过上传，$shaFileName 与 $ToolboxManifestFileName 每次重传。") -ForegroundColor DarkGray
        }
        else {
            $readyCount = $expectedNames.Count - $missing.Count
            Write-Host ("断点检查：线上已有 $readyCount/$($expectedNames.Count) 个附件，本次补传 " + ($missing -join '、')) -ForegroundColor Cyan
        }
        $index = 0
        foreach ($payload in $payloads) {
            $index++
            $progress = if ($payloads.Count -le 1) { 40 } else { 40 + (24.0 * ($index - 1) / $payloads.Count) }
            [void](Send-AxGiteeAsset -ReleaseId $releaseId -LocalPath $payload.path -AssetName $payload.name `
                    -ExistingAssets $assets -Progress $progress)
            $assets = @(Get-AxGiteeList -Path "releases/$releaseId/attach_files")
            if (Test-AxCancelled) { throw '用户在上传中途取消了操作；已传好的附件下次会跳过。' }
        }
        [void](Send-AxGiteeAsset -ReleaseId $releaseId -LocalPath $shaPath -AssetName $shaFileName `
                -ExistingAssets $assets -Progress 66 -ReplaceAlways)
        $expectedSoFar = [Collections.Generic.List[hashtable]]::new()
        foreach ($payload in $payloads) {
            $expectedSoFar.Add(@{ name = $payload.name; size = [int64]$payload.size })
        }
        $expectedSoFar.Add(@{ name = $shaFileName; size = (Get-Item -LiteralPath $shaPath).Length })
        Assert-AxReleaseAssets -ReleaseId $releaseId -Expected $expectedSoFar.ToArray()
        Set-AxCancellationMode -Mode 'locked' -Message '包已就位，正在提交更新清单；这一瞬间不可停止。'
        $assets = @(Get-AxGiteeList -Path "releases/$releaseId/attach_files")
        [void](Send-AxGiteeAsset -ReleaseId $releaseId -LocalPath $manifestPath -AssetName $ToolboxManifestFileName `
                -ExistingAssets $assets -Progress 92 -ReplaceAlways)
        $expectedAll = [Collections.Generic.List[hashtable]]::new()
        foreach ($payload in $payloads) {
            $expectedAll.Add(@{ name = $payload.name; size = [int64]$payload.size })
        }
        $expectedAll.Add(@{ name = $shaFileName; size = (Get-Item -LiteralPath $shaPath).Length })
        $expectedAll.Add(@{ name = $ToolboxManifestFileName; size = (Get-Item -LiteralPath $manifestPath).Length })
        Assert-AxReleaseAssets -ReleaseId $releaseId -Expected $expectedAll.ToArray()
        if (!$SkipSelfCheck) {
            try {
                Invoke-AxSelfCheck `
                    -Tag $tag `
                    -ManifestFileName $ToolboxManifestFileName `
                    -LocalZipPath $zipPath `
                    -LocalManifestPath $manifestPath
            }
            catch {
                if ($TolerateSelfCheckFailure) {
                    Write-Host ("自检未通过（已按 -TolerateSelfCheckFailure 放行）：" + $_.Exception.Message) -ForegroundColor Yellow
                }
                else {
                    throw
                }
            }
        }
        if (!$SkipPrune) {
            Prune-AxOldReleaseAssets -KeepCount $KeepReleasesWithAssets -CurrentTag $tag `
                -DeleteReleaseRecords:(!$KeepOldReleaseRecords) -ProjectRoot $root -Repository $GiteeRepository `
                -DryRun:$DryRun
        }
        Set-AxCancellationMode -Mode 'stop' -Message '发布已完成。'
        Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message "发布完成：$tag"
        Write-Host ''
        Write-Host '发布完成：' -ForegroundColor Green
        Write-Host "  Release 页：https://gitee.com/$GiteeRepository/releases/tag/$tag"
        if ($parts.Count -gt 0) {
            Write-Host ("  包地址    ：{0} 卷（客户端按卷号顺序拼接；任一卷都在 Release 附件列表里）" -f $parts.Count)
            foreach ($part in $parts) {
                Write-Host "              $(Get-AxAttachmentUrl -ReleaseId $releaseId -FileName ([string]$part.fileName))"
            }
        }
        else {
            Write-Host "  包地址    ：$(Get-AxAttachmentUrl -ReleaseId $releaseId -FileName $zipFileName)"
        }
        Write-Host "  清单地址  ：$(Get-AxAttachmentUrl -ReleaseId $releaseId -FileName $ToolboxManifestFileName)"
        Write-Host "  API 调用  ：$Script:RequestCount 次（限流重试 $Script:RateLimitRetries 次、瞬时故障重试 $Script:TransientRetries 次）"
    }
    catch {
        $exitCode = 1
        $message = [string]$_.Exception.Message
        $redactions = [Collections.Generic.List[string]]::new()
        if (![string]::IsNullOrWhiteSpace($Script:Token)) { $redactions.Add($Script:Token) }
        $redactions.Add($GiteeAccessToken)
        foreach ($name in @('AXTOOLS_GITEE_TOKEN', 'FANTASYTOOLS_GITEE_TOKEN', 'GITEE_TOKEN', 'GITEE_ACCESS_TOKEN')) {
            foreach ($scope in @('Process', 'User', 'Machine')) {
                $value = [Environment]::GetEnvironmentVariable($name, $scope)
                if (![string]::IsNullOrWhiteSpace($value)) { $redactions.Add($value) }
            }
        }
        foreach ($secret in $redactions) {
            if ($secret.Length -ge 8) { $message = $message.Replace($secret, '<token>') }
        }
        Write-AxReleaseProgress -Stage 'failed' -Percent 100 -Message ("发布失败：" + $message)
        [Console]::Error.WriteLine('发布失败：' + $message)
        [Console]::Error.WriteLine($_.ScriptStackTrace)
    }
    return $exitCode
}

$GithubApiBase = 'https://api.github.com/repos'
$GithubUploadBase = 'https://uploads.github.com/repos'
$GithubApiVersion = '2022-11-28'
function Invoke-AxGithubRequest {
    # 统一出口：带 Bearer 鉴权 + API 版本头，5xx/429 退避重试。
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Patch', 'Delete')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body,
        [string]$Operation = '',
        [switch]$AllowNotFound
    )
    $uri = "$GithubApiBase/$GithubRepository/$Path"
    $headers = @{
        Authorization          = "Bearer $Script:Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = $GithubApiVersion
        'User-Agent'           = 'AxTools-ReleasePublish/1.0'
    }
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            $params = @{ Method = $Method; Uri = $uri; Headers = $headers; TimeoutSec = 120 }
            if ($null -ne $Body) {
                $json = $Body | ConvertTo-Json -Compress -Depth 6
                $params['Body'] = [Text.Encoding]::UTF8.GetBytes($json)
                $params['ContentType'] = 'application/json; charset=utf-8'
            }
            return Invoke-RestMethod @params
        }
        catch {
            $status = Get-AxStatusCode -ErrorRecord $_
            if ($status -eq 404 -and $AllowNotFound) { return $null }
            $bodyText = Get-AxErrorBodyText -ErrorRecord $_
            $summary = if ($bodyText.Length -gt 400) { $bodyText.Substring(0, 400) + '…' } else { $bodyText }
            $label = if ([string]::IsNullOrWhiteSpace($Operation)) { $Method } else { $Operation }
            if ($status -in @(0, 429, 500, 502, 503, 504) -and $attempt -lt 4) {
                $statusLabel = if ($status -eq 0) { '无 HTTP 响应' } else { "HTTP $status" }
                Write-AxReleaseProgress -Stage 'retry' -Percent $Script:LastPercent `
                    -Message "GitHub 瞬时故障（$statusLabel），$(5 * $attempt) 秒后重试（$attempt/3）" `
                    -Detail $label
                Start-Sleep -Seconds (5 * $attempt)
                continue
            }
            throw ("GitHub API 调用失败（$label，HTTP $status）：$summary")
        }
    }
}
function Get-AxGithubReleaseByTag {
    param([Parameter(Mandatory)][string]$Tag)
    return Invoke-AxGithubRequest -Method Get -Path "releases/tags/$Tag" -Operation "读取 Release $Tag" -AllowNotFound
}
function Confirm-AxGithubRelease {
    # 幂等确保 Release 存在：存在就 PATCH（让 name/body/prerelease 与本地产物对齐）。
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][AllowEmptyString()][string]$BodyText,
        [Parameter(Mandatory)][bool]$Prerelease
    )
    $payload = @{
        tag_name         = $Tag
        name             = $Title
        body             = $BodyText
        prerelease       = $Prerelease
        draft            = $false
        target_commitish = $GithubBranch
    }
    $existing = Get-AxGithubReleaseByTag -Tag $Tag
    if ($null -eq $existing) {
        Write-Host "创建 GitHub Release：$Tag（prerelease=$Prerelease）" -ForegroundColor Cyan
        return Invoke-AxGithubRequest -Method Post -Path 'releases' -Body $payload -Operation "创建 Release $Tag"
    }
    Write-Host "更新已存在的 GitHub Release：$Tag（prerelease=$Prerelease）" -ForegroundColor Cyan
    return Invoke-AxGithubRequest -Method Patch -Path ("releases/{0}" -f [int]$existing.id) -Body $payload `
        -Operation "更新 Release $Tag"
}
function Get-AxGithubAssets {
    param([Parameter(Mandatory)][int]$ReleaseId)
    return @(Invoke-AxGithubRequest -Method Get -Path ("releases/{0}/assets?per_page=100" -f $ReleaseId) `
            -Operation '列出 Release 附件')
}
function Remove-AxGithubAsset {
    param([Parameter(Mandatory)][int]$AssetId, [Parameter(Mandatory)][string]$Name)
    Write-Host "  删除同名旧附件：$Name" -ForegroundColor Yellow
    Invoke-AxGithubRequest -Method Delete -Path ("releases/assets/{0}" -f $AssetId) -Operation "删除附件 $Name" | Out-Null
}
function Send-AxGithubAsset {
    # 上传一个附件。幂等靠 `digest`（GitHub 自己算的 sha256）：
    param(
        [Parameter(Mandatory)][int]$ReleaseId,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ExistingAssets,
        [Parameter(Mandatory)][double]$Progress
    )
    if (!(Test-Path -LiteralPath $LocalPath -PathType Leaf)) { throw "要上传的文件不存在：$LocalPath" }
    $file = Get-Item -LiteralPath $LocalPath
    $localSha = Get-AxSha256Text -Path $file.FullName
    $existing = @($ExistingAssets | Where-Object { [string]$_.name -eq $AssetName } | Select-Object -First 1)
    if ($existing.Count -gt 0) {
        $remoteSize = [int64]$existing[0].size
        $remoteDigest = [string]$existing[0].digest
        $sameContent = $false
        if ($remoteSize -eq [int64]$file.Length) {
            if (![string]::IsNullOrWhiteSpace($remoteDigest)) {
                $sameContent = $remoteDigest.Equals("sha256:$localSha", [StringComparison]::OrdinalIgnoreCase)
            }
            else {
                Write-Host "  线上附件 $AssetName 没有 digest，只能按大小判定（$remoteSize 字节）。" -ForegroundColor Yellow
                $sameContent = $true
            }
        }
        if ($sameContent) {
            Write-Host "  已存在同名同内容附件，跳过上传：$AssetName（$([Math]::Round($file.Length / 1MB, 2)) MB）" -ForegroundColor DarkGray
            return $false
        }
        Remove-AxGithubAsset -AssetId ([int]$existing[0].id) -Name $AssetName
    }
    $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $curl) { throw '没有找到 curl.exe，无法上传 GitHub Release 附件。' }
    Write-AxReleaseProgress -Stage 'upload' -Percent $Progress -Message "正在上传到 GitHub：$AssetName（$([Math]::Round($file.Length / 1MB, 2)) MB）"
    $encodedName = [Uri]::EscapeDataString($AssetName)
    $uri = "$GithubUploadBase/$GithubRepository/releases/$ReleaseId/assets?name=$encodedName"
    & $curl.Source --http1.1 --fail-with-body --silent --show-error --location `
        --retry 10 --retry-delay 5 --retry-all-errors `
        -H "Authorization: Bearer $Script:Token" `
        -H 'Accept: application/vnd.github+json' `
        -H "X-GitHub-Api-Version: $GithubApiVersion" `
        -H 'Content-Type: application/octet-stream' `
        --data-binary "@$($file.FullName)" `
        $uri
    $exit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exit -ne 0) {
        throw "上传 $AssetName 到 GitHub 失败（curl 退出码 $exit）。常见原因：令牌缺 repo 权限、文件超过 2 GiB、网络中断。"
    }
    Write-Host "  已上传到 GitHub：$AssetName" -ForegroundColor Green
    return $true
}
function Assert-AxGithubAssets {
    # 传完复查：附件名齐全、大小与本地一致，缺一即中止。
    param(
        [Parameter(Mandatory)][int]$ReleaseId,
        [Parameter(Mandatory)][AllowEmptyCollection()][hashtable[]]$Expected
    )
    $assets = Get-AxGithubAssets -ReleaseId $ReleaseId
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($item in $Expected) {
        $match = @($assets | Where-Object { [string]$_.name -eq $item.name } | Select-Object -First 1)
        if ($match.Count -eq 0) {
            $problems.Add("线上缺少附件：$($item.name)")
            continue
        }
        if ([int64]$match[0].size -ne [int64]$item.size) {
            $problems.Add("附件大小不一致：$($item.name) 线上 $([int64]$match[0].size) / 本地 $([int64]$item.size)")
        }
    }
    if ($problems.Count -gt 0) {
        throw "GitHub 附件复查未通过，已中止：`n  - " + ($problems -join "`n  - ")
    }
    Write-Host ("  附件复查通过：$($Expected.Count) 个") -ForegroundColor Green
}
function Invoke-AxToolsGithubPublish {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [string]$Notes = '',
        [string]$StagingDirectory = '',
        [string]$ProjectRoot = (Resolve-Path (Join-Path $axScriptRoot '..\..')).Path,
        [string]$GithubRepository = 'kirito0000001/AxTools',
        [string]$GithubBranch = 'main',
        [string]$GithubAccessToken = '',
        [ValidateSet('win-x64')]
        [string]$Runtime = 'win-x64',
        [switch]$DryRun,
        [switch]$SkipBuild,
        [switch]$SkipSelfCheck,
        [switch]$TolerateSelfCheckFailure
    )
    $exitCode = 0
    try {
        if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.]+)?$') {
            throw "版本号必须形如 1.0.1 或 1.0.1-Beta：$Version"
        }
        $targetVersion = $Version.Trim().TrimStart('v')
        $tag = "v$targetVersion"
        $prerelease = $targetVersion.Contains('-')
        $root = Get-AxNormalizedPath -Path $ProjectRoot
        $staging = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
            Join-Path $root (Join-Path 'Artifacts\ReleaseAssets' "v$targetVersion")
        }
        else { Get-AxNormalizedPath -Path $StagingDirectory }
        $zipFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.zip"
        $shaFileName = "$ToolboxStableKey-v$targetVersion-$Runtime.sha256.txt"
        $zipPath = Join-Path $staging $zipFileName
        $shaPath = Join-Path $staging $shaFileName
        $manifestPath = Join-Path $staging $ToolboxManifestFileName
        Write-Host "== GitHub 发布 $DisplayName $tag（prerelease=$prerelease）==" -ForegroundColor Cyan
        Write-Host "   产物目录：$staging"
        foreach ($path in @($zipPath, $shaPath, $manifestPath)) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "缺少发布物：$path。请先跑 Build-AxToolsUpdatePackage.ps1（或 Gitee 发布脚本）出包。"
            }
        }
        $zipItem = Get-Item -LiteralPath $zipPath
        $zipSha = Get-AxSha256Text -Path $zipPath
        $manifest = Read-AxJsonFile -Path $manifestPath

        # 公告为空 → 用清单里那句自动说明（出包脚本在没给 -Notes 时会写一句）。
        $manifestNotes = [string](Get-AxJsonProperty -InputObject $manifest -Name 'releaseNotes')
        if ([string]::IsNullOrWhiteSpace($Notes) -and ![string]::IsNullOrWhiteSpace($manifestNotes)) {
            $Notes = $manifestNotes
        }
        if ([string]$manifest.toolboxStableKey -ne $ToolboxStableKey) {
            throw "清单里的 toolboxStableKey 不是 $ToolboxStableKey：$($manifest.toolboxStableKey)"
        }
        if ([string]$manifest.version -ne $targetVersion) {
            throw "清单版本 $($manifest.version) 与目标版本 $targetVersion 不一致。"
        }
        if ([string]$manifest.assets[0].sha256 -ne $zipSha) {
            throw '清单里的 sha256 与本地整包实际哈希不一致。'
        }
        if ([int64]$manifest.assets[0].sizeBytes -ne [int64]$zipItem.Length) {
            throw '清单里的 sizeBytes 与本地整包实际大小不一致。'
        }
        $parts = @()
        $partsProperty = $manifest.assets[0].PSObject.Properties['parts']
        if ($null -ne $partsProperty -and $null -ne $partsProperty.Value) { $parts = @($partsProperty.Value) }
        $payloads = [Collections.Generic.List[hashtable]]::new()
        if ($parts.Count -gt 0) {
            foreach ($part in $parts) {
                $partName = [string]$part.fileName
                $partPath = Join-Path $staging $partName
                if (!(Test-Path -LiteralPath $partPath -PathType Leaf)) {
                    throw "清单里列了分卷 $partName，但产物目录里没有它：$partPath"
                }
                $partItem = Get-Item -LiteralPath $partPath
                if ([int64]$partItem.Length -ne [int64]$part.sizeBytes) {
                    throw "分卷 $partName 大小与清单不一致：本地 $($partItem.Length) / 清单 $($part.sizeBytes)。"
                }
                $partSha = Get-AxSha256Text -Path $partPath
                if (![string]::Equals($partSha, [string]$part.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "分卷 $partName 的 SHA256 与清单不一致。"
                }
                $payloads.Add(@{ name = $partName; path = $partPath; size = [int64]$partItem.Length })
            }
        }
        else {
            $payloads.Add(@{ name = $zipFileName; path = $zipPath; size = [int64]$zipItem.Length })
        }
        Write-Host "   版本：$targetVersion（prerelease=$prerelease）"
        Write-Host "   整包：$([Math]::Round($zipItem.Length / 1MB, 2)) MB / $($zipItem.Length) 字节"
        # 增量包：与 Gitee 侧同一份清单、同一份产物，一起传。
        $deltaNode = Get-AxJsonProperty -InputObject $manifest -Name 'delta'
        $deltaName = if ($null -eq $deltaNode) { '' } else { [string]$deltaNode.fileName }
        if (![string]::IsNullOrWhiteSpace($deltaName)) {
            $deltaPath = Join-Path $staging $deltaName
            if (!(Test-Path -LiteralPath $deltaPath -PathType Leaf)) {
                throw "清单里声明了增量包 $deltaName，但产物目录里没有它：$deltaPath。"
            }
            $deltaItem = Get-Item -LiteralPath $deltaPath
            if ([int64]$deltaItem.Length -ne [int64]$deltaNode.sizeBytes) {
                throw "增量包大小与清单不一致：本地 $($deltaItem.Length) / 清单 $($deltaNode.sizeBytes)。"
            }
            $deltaSha = Get-AxSha256Text -Path $deltaPath
            if (![string]::Equals($deltaSha, [string]$deltaNode.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "增量包 SHA256 与清单不一致。"
            }
            $payloads.Add(@{ name = $deltaName; path = $deltaPath; size = [int64]$deltaItem.Length })
            Write-Host ("   增量包：$deltaName（{0:N2} MB，基准 v{1}）" -f ($deltaItem.Length / 1MB), [string]$deltaNode.baseVersion)
        }

        Write-Host "   要上传：$($payloads.Count) 个包体 + sha256.txt + 清单"
        if ($DryRun) {
            Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message '演练完成：本地产物校验通过，未接触 GitHub'
            Write-Host ''
            Write-Host '演练完成（-DryRun）：真实发布会执行 ——' -ForegroundColor Yellow
            Write-Host "  1. 幂等确保 GitHub Release $tag（owner/repo=$GithubRepository，target_commitish=$GithubBranch）"
            foreach ($payload in $payloads) {
                Write-Host ("  2. 传 {0}（{1:N2} MB）" -f $payload.name, ($payload.size / 1MB))
            }
            Write-Host "  3. 传 $shaFileName → 复查 → 传 $ToolboxManifestFileName（最后）"
            Write-Host '  注意：GitHub 侧**不清理任何旧 Release / 旧附件**。' -ForegroundColor DarkGray
            return 0
        }
        $Script:Token = Get-AxToolsGithubToken -Explicit $GithubAccessToken
        Write-AxReleaseProgress -Stage 'release' -Percent 20 -Message "正在幂等确保 GitHub Release：$tag"
        $release = Confirm-AxGithubRelease -Tag $tag -Title "$DisplayName $tag" -BodyText $Notes -Prerelease $prerelease
        $releaseId = [int]$release.id
        if ($releaseId -le 0) { throw "GitHub 没有返回有效的 Release ID：$($release | ConvertTo-Json -Compress)" }
        $index = 0
        foreach ($payload in $payloads) {
            $index++
            $progress = 30 + (50.0 * ($index - 1) / [Math]::Max(1, $payloads.Count))
            $assets = Get-AxGithubAssets -ReleaseId $releaseId
            [void](Send-AxGithubAsset -ReleaseId $releaseId -LocalPath $payload.path -AssetName $payload.name `
                    -ExistingAssets $assets -Progress $progress)
        }
        $assets = Get-AxGithubAssets -ReleaseId $releaseId
        [void](Send-AxGithubAsset -ReleaseId $releaseId -LocalPath $shaPath -AssetName $shaFileName `
                -ExistingAssets $assets -Progress 90)
        $expected = [Collections.Generic.List[hashtable]]::new()
        foreach ($payload in $payloads) { $expected.Add(@{ name = $payload.name; size = [int64]$payload.size }) }
        $expected.Add(@{ name = $shaFileName; size = (Get-Item -LiteralPath $shaPath).Length })
        Assert-AxGithubAssets -ReleaseId $releaseId -Expected $expected.ToArray()
        $assets = Get-AxGithubAssets -ReleaseId $releaseId
        [void](Send-AxGithubAsset -ReleaseId $releaseId -LocalPath $manifestPath -AssetName $ToolboxManifestFileName `
                -ExistingAssets $assets -Progress 94)
        $expected.Add(@{ name = $ToolboxManifestFileName; size = (Get-Item -LiteralPath $manifestPath).Length })
        Assert-AxGithubAssets -ReleaseId $releaseId -Expected $expected.ToArray()
        # GitHub 侧不做收尾自检：客户端只读 Gitee，而上传本身已由 Assert-AxGithubAssets 复查过。
        Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message "GitHub 发布完成：$tag"
        Write-Host ''
        Write-Host 'GitHub 发布完成：' -ForegroundColor Green
        Write-Host "  Release 页：https://github.com/$GithubRepository/releases/tag/$tag"
        Write-Host "  上传文件  ：$($expected.Count) 个（含清单）"
        Write-Host '  旧版本    ：未清理（GitHub 侧设计为永久保留）' -ForegroundColor DarkGray
    }
    catch {
        $exitCode = 1
        $message = [string]$_.Exception.Message
        $redactions = [Collections.Generic.List[string]]::new()
        if (![string]::IsNullOrWhiteSpace($Script:Token)) { $redactions.Add($Script:Token) }
        $redactions.Add($GithubAccessToken)
        foreach ($name in @('AXTOOLS_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN')) {
            foreach ($scope in @('Process', 'User', 'Machine')) {
                $value = [Environment]::GetEnvironmentVariable($name, $scope)
                if (![string]::IsNullOrWhiteSpace($value)) { $redactions.Add($value) }
            }
        }
        foreach ($secret in $redactions) {
            if ($secret.Length -ge 8) { $message = $message.Replace($secret, '<token>') }
        }
        Write-AxReleaseProgress -Stage 'failed' -Percent 100 -Message ("GitHub 发布失败：" + $message)
        [Console]::Error.WriteLine('GitHub 发布失败：' + $message)
        [Console]::Error.WriteLine($_.ScriptStackTrace)
    }
    return $exitCode
}
