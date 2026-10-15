$ErrorActionPreference = 'Stop'

function Get-AxToolsGiteeCredential {
    foreach ($name in @(
        'AXTOOLS_GITEE_TOKEN',
        'FANTASYTOOLS_GITEE_TOKEN',
        'GITEE_TOKEN',
        'GITEE_ACCESS_TOKEN')) {
        foreach ($scope in @('Process','User','Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if (![string]::IsNullOrWhiteSpace($value)) {
                return [pscustomobject]@{
                    Token = $value.Trim()
                    SourceName = $name
                    Scope = $scope
                }
            }
        }
    }

    throw '未找到可用的 Gitee 令牌。请设置 AXTOOLS_GITEE_TOKEN、FANTASYTOOLS_GITEE_TOKEN 或 GITEE_TOKEN。'
}

function Get-AxToolsGiteeToken {
    # 发布链只需要令牌本身；显式参数优先于环境变量（与 ZD 侧同一套顺序）。
    param([string]$Explicit)

    if (![string]::IsNullOrWhiteSpace($Explicit)) { return $Explicit.Trim() }
    return (Get-AxToolsGiteeCredential).Token
}

function Get-AxToolsGithubCredential {
    # 顺序：显式参数 > 环境变量（Process→User→Machine）> `gh auth token`。
    # 找不到时返回 $null —— 由调用方决定"缺 GitHub 令牌"这句提示怎么写。
    param([string]$Explicit)

    if (![string]::IsNullOrWhiteSpace($Explicit)) {
        return [pscustomobject]@{ Token = $Explicit.Trim(); SourceName = '-GithubAccessToken' }
    }
    foreach ($name in @('AXTOOLS_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN')) {
        foreach ($scope in @('Process','User','Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if (![string]::IsNullOrWhiteSpace($value)) {
                return [pscustomobject]@{ Token = $value.Trim(); SourceName = "$name@$scope" }
            }
        }
    }
    $gh = Get-Command 'gh.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $gh) {
        $token = (& $gh.Source auth token 2>$null)
        if (![string]::IsNullOrWhiteSpace($token)) {
            return [pscustomobject]@{ Token = $token.Trim(); SourceName = 'gh auth token' }
        }
    }
    return $null
}

function Get-AxToolsGithubToken {
    param([string]$Explicit)

    $credential = Get-AxToolsGithubCredential -Explicit $Explicit
    if ($null -eq $credential) {
        throw ('没有找到 GitHub 令牌。请设置 AXTOOLS_GITHUB_TOKEN / GITHUB_TOKEN / GH_TOKEN，' +
            '或先 `gh auth login`；也可以直接传 -GithubAccessToken。')
    }
    return $credential.Token
}

Export-ModuleMember -Function @(
    'Get-AxToolsGiteeCredential',
    'Get-AxToolsGiteeToken',
    'Get-AxToolsGithubCredential',
    'Get-AxToolsGithubToken')
