[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('stable','beta')][string]$Channel = 'stable',
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('GitHub','Gitee','Both')][string]$Target = 'Both',
    [string]$ReleaseNotes = '',
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$packageScript = Join-Path $ProjectRoot 'Scripts\Release\Package-AxTools.ps1'
$validateScript = Join-Path $ProjectRoot 'Scripts\Release\Test-AxToolsPackage.ps1'
$releaseCommonModule = Join-Path $ProjectRoot 'Scripts\Release\AxToolsReleaseCommon.psm1'
Import-Module $releaseCommonModule -Force
if (!$SkipBuild) {
    & $packageScript `
        -ProjectRoot $ProjectRoot `
        -Version $Version `
        -Channel $Channel `
        -OutputRoot $OutputRoot `
        -ReleaseNotes $ReleaseNotes
}
& $validateScript -ReleaseAssetRoot $OutputRoot -ExpectedVersion $Version
$manifestPath = Join-Path $OutputRoot 'toolbox-update.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$asset = @($manifest.assets)[0]
$zipPath = Join-Path $OutputRoot $asset.fileName
$shaPath = Join-Path $OutputRoot ([IO.Path]::GetFileNameWithoutExtension($asset.fileName) + '.sha256.txt')
$assets = @($zipPath, $shaPath, $manifestPath)
$tag = "v$Version"
$title = if ($Channel -eq 'beta') { "AxTools $Version 测试版" } else { "AxTools $Version 正式版" }
$defaultNotes = @"
## AxTools V$Version

统一管理工具的启动、构建、打包、发布、热更新与开发环境。
"@.Trim()
$notes = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $defaultNotes
} else {
    $ReleaseNotes.Trim()
}

Write-Host "发布摘要：$tag / $Channel / $($asset.fileName) / $($asset.sizeBytes) bytes / $($asset.sha256)"
if ($DryRun) { Write-Host 'DryRun：已完成本地校验，没有执行网络写入。'; $global:LASTEXITCODE = 0; return }

if ($Target -in @('GitHub','Both')) {
    if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw '未找到 GitHub CLI。' }
    $existing = & gh release view $tag --repo 'kirito0000001/AxTools' --json tagName 2>$null
    if ($LASTEXITCODE -ne 0) {
        $args = @('release','create',$tag,'--repo','kirito0000001/AxTools','--title',$title,'--notes',$notes)
        if ($Channel -eq 'beta') { $args += '--prerelease' }
        & gh @args
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release 创建失败：$LASTEXITCODE" }
    }
    else {
        $editArgs = @('release','edit',$tag,'--repo','kirito0000001/AxTools','--title',$title,'--notes',$notes)
        if ($Channel -eq 'beta') { $editArgs += '--prerelease' } else { $editArgs += '--latest' }
        & gh @editArgs
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release 标题或说明更新失败：$LASTEXITCODE" }
    }
    $releaseJson = & gh release view $tag --repo 'kirito0000001/AxTools' --json assets
    if ($LASTEXITCODE -ne 0) { throw 'GitHub Release 附件列表读取失败。' }
    $oldAssets = @((ConvertFrom-Json ($releaseJson -join [Environment]::NewLine)).assets)
    foreach ($oldAsset in $oldAssets) {
        & gh release delete-asset $tag ([string]$oldAsset.name) --repo 'kirito0000001/AxTools' --yes
        if ($LASTEXITCODE -ne 0) { throw "GitHub 旧附件删除失败：$($oldAsset.name)" }
    }
    & gh release upload $tag @assets --repo 'kirito0000001/AxTools'
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release 附件上传失败：$LASTEXITCODE" }

    $uploadedJson = & gh release view $tag --repo 'kirito0000001/AxTools' --json assets
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 上传结果读取失败。' }
    $uploadedAssets = @((ConvertFrom-Json ($uploadedJson -join [Environment]::NewLine)).assets)
    if ($uploadedAssets.Count -ne $assets.Count) { throw 'GitHub 上传后的附件数量不一致。' }
    foreach ($path in $assets) {
        $file = Get-Item -LiteralPath $path
        $remote = @($uploadedAssets | Where-Object name -CEQ $file.Name)
        if ($remote.Count -ne 1 -or [int64]$remote[0].size -ne $file.Length) {
            throw "GitHub 附件核对失败：$($file.Name)"
        }
    }
}

if ($Target -in @('Gitee','Both')) {
    $credential = Get-AxToolsGiteeCredential
    $token = $credential.Token
    Write-Host "Gitee 凭据来源：$($credential.SourceName) / $($credential.Scope)"
    $repoApi = 'https://gitee.com/api/v5/repos/xiaojie578/AxTools'
    try {
        Invoke-RestMethod -Method Get -Uri $repoApi -Body @{ access_token=$token } | Out-Null
    }
    catch {
        $statusCode = if ($_.Exception.Response) {
            [int]$_.Exception.Response.StatusCode
        } else {
            0
        }
        if ($statusCode -ne 404) { throw }
        Write-Host 'Gitee AxTools 仓库不存在，正在创建公开的 README-only 仓库...'
        $created = Invoke-RestMethod `
            -Method Post `
            -Uri 'https://gitee.com/api/v5/user/repos' `
            -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
            -Body @{
                access_token=$token
                name='AxTools'
                description='AxTools 统一工具管理器 Release 镜像'
                private='false'
                auto_init='true'
            }
        if ([string]$created.full_name -ne 'xiaojie578/AxTools') {
            throw 'Gitee AxTools 仓库创建结果无效。'
        }
    }
    $repository = Invoke-RestMethod `
        -Method Patch `
        -Uri $repoApi `
        -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
        -Body @{
            access_token=$token
            name='AxTools'
            description='AxTools 统一工具管理器 Release 镜像'
            private='false'
        }
    if ($repository.private -eq $true -or $repository.public -ne $true) {
        throw 'Gitee AxTools 仓库不是公开仓库，已停止发布。'
    }
    $api = 'https://gitee.com/api/v5/repos/xiaojie578/AxTools/releases'
    $releaseResponse = Invoke-RestMethod -Method Get -Uri $api -Body @{ access_token=$token }
    $releases = @($releaseResponse.GetEnumerator())
    $release = @($releases | Where-Object tag_name -eq $tag | Select-Object -First 1)
    if ($release.Count -eq 0) {
        $release = Invoke-RestMethod `
            -Method Post `
            -Uri $api `
            -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
            -Body @{ access_token=$token; tag_name=$tag; name=$title; body=$notes; prerelease=($Channel -eq 'beta').ToString().ToLowerInvariant(); target_commitish='master' }
    } else {
        $release = $release[0]
        $release = Invoke-RestMethod `
            -Method Patch `
            -Uri "$api/$($release.id)" `
            -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
            -Body @{ access_token=$token; tag_name=$tag; name=$title; body=$notes; prerelease=($Channel -eq 'beta').ToString().ToLowerInvariant(); target_commitish='master' }
    }
    $attachApi = "$api/$($release.id)/attach_files"
    $existingResponse = Invoke-RestMethod -Method Get -Uri $attachApi -Body @{
        access_token=$token
        per_page=100
    }
    $existingFiles = @($existingResponse.GetEnumerator() | Where-Object {
        $null -ne $_ -and $null -ne $_.id
    })
    foreach ($existingFile in $existingFiles) {
        Invoke-RestMethod `
            -Method Delete `
            -Uri "$attachApi/$($existingFile.id)" `
            -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
            -Body @{ access_token=$token } | Out-Null
    }
    foreach ($path in $assets) {
        $name = [IO.Path]::GetFileName($path)
        & curl.exe --fail-with-body --silent --show-error --request POST --form "access_token=$token" --form "file=@$path;filename=$name" $attachApi | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Gitee 附件上传失败：$name" }
    }
    $uploadedResponse = Invoke-RestMethod -Method Get -Uri $attachApi -Body @{
        access_token=$token
        per_page=100
    }
    $uploadedFiles = @($uploadedResponse.GetEnumerator() | Where-Object {
        $null -ne $_ -and ![string]::IsNullOrWhiteSpace([string]$_.name)
    })
    foreach ($path in $assets) {
        $file = Get-Item -LiteralPath $path
        $remote = @($uploadedFiles | Where-Object name -CEQ $file.Name)
        if ($remote.Count -ne 1 -or [int64]$remote[0].size -ne $file.Length) {
            throw "Gitee 附件核对失败：$($file.Name)"
        }
    }
}
Write-Host "发布完成：$tag"
$global:LASTEXITCODE = 0
