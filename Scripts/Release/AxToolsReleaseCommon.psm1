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

Export-ModuleMember -Function 'Get-AxToolsGiteeCredential'
