# 完整发布编排：凭据预检 → Gitee（0-50%）→ GitHub（50-100%），同进程顺序调用。
#
# 与 ZD 工具箱（CrossingVoidZDTool\Scripts\Publish-ZDToolboxRelease.ps1）同构：
# 出包只做一次（Gitee 段负责），两条通道传的是**同一批附件**；任一段失败都可以原样重跑，
# 已传成功的附件会核对内容后跳过。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Notes = '',
    [string]$StagingDirectory = '',
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$DryRun,
    [switch]$ForceRebuild,
    # 跳过出包：产物目录里必须已有三件产物（「单独上传」这条动作走它）。
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipVersionWrite,
    [int64]$PartSizeBytes = 0,
    [switch]$ExcludeAiRuntime,
    [switch]$NoDelta,

    [switch]$SkipGitee,
    [switch]$SkipGithub,
    [string]$GiteeRepository = 'xiaojie578/AxTools',
    [string]$GiteeBranch = 'master',
    [string]$GiteeAccessToken = '',
    [string]$GithubRepository = 'kirito0000001/AxTools',
    [string]$GithubBranch = 'main',
    [string]$GithubAccessToken = '',
    [int]$KeepReleasesWithAssets = 3,
    [switch]$SkipPrune,
    [switch]$KeepOldReleaseRecords,
    [switch]$SkipSelfCheck,
    [switch]$TolerateSelfCheckFailure
)
. (Join-Path $PSScriptRoot 'AxToolsPublish.ps1')

$exitCode = 0
try {
    if ($SkipGitee -and $SkipGithub) { throw '两条通道都被 -SkipGitee / -SkipGithub 跳过了，没有可做的事。' }
    $root = Get-AxNormalizedPath -Path $ProjectRoot
    $targetVersion = $Version.Trim().TrimStart('v')
    $staging = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
        Join-Path $root (Join-Path 'Artifacts\ReleaseAssets' "v$targetVersion")
    }
    else { Get-AxNormalizedPath -Path $StagingDirectory }
    Write-Host "== 完整发布 AxTools v$targetVersion ==" -ForegroundColor Cyan
    Write-Host "   工程根  ：$root"
    Write-Host "   产物目录：$staging"
    Write-Host "   通道    ：$(if ($SkipGitee) { 'GitHub' } elseif ($SkipGithub) { 'Gitee' } else { 'Gitee + GitHub' })"
    $giteeToken = ''
    $githubToken = ''
    if (!$DryRun) {
        if (!$SkipGitee) {
            $credential = Get-AxToolsGiteeCredential
            $giteeToken = Get-AxToolsGiteeToken -Explicit $GiteeAccessToken
            Write-Host "Gitee 令牌 ：$(if ([string]::IsNullOrWhiteSpace($GiteeAccessToken)) { "来自环境变量 $($credential.SourceName)（$($credential.Scope)）" } else { '来自 -GiteeAccessToken' })"
        }
        if (!$SkipGithub) {
            $github = Get-AxToolsGithubCredential -Explicit $GithubAccessToken
            if ($null -eq $github) {
                throw ('缺少 GitHub 令牌：请设置 AXTOOLS_GITHUB_TOKEN（或 GITHUB_TOKEN / GH_TOKEN）、' +
                    '先执行 `gh auth login`，或传 -GithubAccessToken；也可以加 -SkipGithub 只发 Gitee。')
            }
            $githubToken = $github.Token
            Write-Host "GitHub 令牌：已就绪（$($github.SourceName)）"
        }
    }
    # 只发 GitHub 时没人出包，这里补一次，免得白跑一趟才报"缺少发布物"。
    if ($SkipGitee -and !$SkipBuild) {
        $buildScript = Join-Path $axScriptRoot 'Build-AxToolsUpdatePackage.ps1'
        Write-Host ''
        Write-Host '===== 出包（只发 GitHub，Gitee 段被跳过）=====' -ForegroundColor Cyan
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
        & $buildScript @buildArguments | Out-Null
        $buildExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        if ($buildExit -ne 0) { throw "出包失败，退出码 $buildExit。" }
    }
    if (!$SkipGitee) {
        Set-AxProgressRange -From 0 -To $(if ($SkipGithub) { 100 } else { 50 })
        Write-Host ''
        Write-Host '===== Gitee 通道 =====' -ForegroundColor Cyan
        $code = Invoke-AxToolsGiteePublish -Version $targetVersion -ProjectRoot $root -StagingDirectory $staging `
            -Runtime $Runtime -Notes $Notes -DryRun:$DryRun `
            -GiteeRepository $GiteeRepository -GiteeBranch $GiteeBranch -GiteeAccessToken $GiteeAccessToken `
            -KeepReleasesWithAssets $KeepReleasesWithAssets -SkipPrune:$SkipPrune `
            -KeepOldReleaseRecords:$KeepOldReleaseRecords -ForceRebuild:$ForceRebuild `
            -SkipBuild:$SkipBuild -SkipTests:$SkipTests -SkipVersionWrite:$SkipVersionWrite -NoDelta:$NoDelta `
            -PartSizeBytes $PartSizeBytes -ExcludeAiRuntime:$ExcludeAiRuntime `
            -SkipSelfCheck:$SkipSelfCheck -TolerateSelfCheckFailure:$TolerateSelfCheckFailure
        if ($code -ne 0) { throw "Gitee 通道失败（退出码 $code）。已传成功的附件不会重传，修好上面那个原因后原样重跑即可。" }
    }
    if (!$SkipGithub) {
        Set-AxProgressRange -From $(if ($SkipGitee) { 0 } else { 50 }) -To 100
        Write-Host ''
        Write-Host '===== GitHub 通道 =====' -ForegroundColor Cyan
        $code = Invoke-AxToolsGithubPublish -Version $targetVersion -ProjectRoot $root -StagingDirectory $staging `
            -Runtime $Runtime -DryRun:$DryRun -GithubRepository $GithubRepository -GithubBranch $GithubBranch `
            -GithubAccessToken $githubToken -SkipSelfCheck:$SkipSelfCheck `
            -TolerateSelfCheckFailure:$TolerateSelfCheckFailure
        if ($code -ne 0) { throw "GitHub 通道失败（退出码 $code）。已传成功的附件不会重传，修好上面那个原因后原样重跑即可。" }
    }
    Set-AxProgressRange -From 0 -To 100
    Write-AxReleaseProgress -Stage 'completed' -Percent 100 -Message "完整发布完成：v$targetVersion"
    Write-Host ''
    Write-Host "完整发布完成：v$targetVersion" -ForegroundColor Green
    if (!$SkipGitee) {
        Write-Host ("  Gitee ：https://gitee.com/$GiteeRepository/releases/tag/v$targetVersion" +
            "（附件只保留最近 $KeepReleasesWithAssets 个 Release）")
    }
    if (!$SkipGithub) {
        Write-Host "  GitHub：https://github.com/$GithubRepository/releases/tag/v$targetVersion（旧版本永久保留）"
    }
}
catch {
    $exitCode = 1
    $message = [string]$_.Exception.Message
    $redactions = [Collections.Generic.List[string]]::new()
    $redactions.Add($GiteeAccessToken)
    $redactions.Add($GithubAccessToken)
    if (![string]::IsNullOrWhiteSpace($giteeToken)) { $redactions.Add($giteeToken) }
    if (![string]::IsNullOrWhiteSpace($githubToken)) { $redactions.Add($githubToken) }
    foreach ($name in @('AXTOOLS_GITEE_TOKEN', 'FANTASYTOOLS_GITEE_TOKEN', 'GITEE_TOKEN', 'GITEE_ACCESS_TOKEN',
            'AXTOOLS_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN')) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if (![string]::IsNullOrWhiteSpace($value)) { $redactions.Add($value) }
        }
    }
    foreach ($secret in $redactions) {
        if ($secret.Length -ge 8) { $message = $message.Replace($secret, '<token>') }
    }
    $payload = [ordered]@{ type = 'progress'; stage = 'failed'; percent = 100; message = ('完整发布失败：' + $message) }
    [Console]::Out.WriteLine('::axtools ' + ($payload | ConvertTo-Json -Compress))
    [Console]::Error.WriteLine('完整发布失败：' + $message)
    [Console]::Error.WriteLine($_.ScriptStackTrace)
}
exit $exitCode
