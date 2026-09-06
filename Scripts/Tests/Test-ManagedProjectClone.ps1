[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cloneScript = Join-Path $repoRoot 'Scripts\Git\Clone-ManagedProject.ps1'
if (!(Test-Path -LiteralPath $cloneScript -PathType Leaf)) {
    throw "缺少项目下载脚本：$cloneScript"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'AxTools-ManagedClone-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $testRoot 'source'
    $remote = Join-Path $testRoot 'remote.git'
    $target = Join-Path $testRoot 'download\DemoTool'
    New-Item -ItemType Directory -Path $source,(Split-Path -Parent $target) -Force | Out-Null
    & git -C $source init -b main | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'DemoTool.csproj') -Value '<Project />' -Encoding UTF8
    & git -C $source add DemoTool.csproj
    & git -C $source -c user.name=AxToolsTest -c user.email=axtools@example.invalid commit -m 'initial' | Out-Null
    & git clone --bare $source $remote | Out-Null

    $lines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File $cloneScript `
        -RepositoryUrl $remote `
        -DestinationPath $target `
        -ExpectedDirectoryName 'DemoTool' `
        -ExpectedMarkersJson '["DemoTool.csproj"]' 2>&1 |
        ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "项目下载脚本失败：$($lines -join [Environment]::NewLine)"
    }
    if (!(Test-Path -LiteralPath (Join-Path $target '.git') -PathType Container) -or
        !(Test-Path -LiteralPath (Join-Path $target 'DemoTool.csproj') -PathType Leaf)) {
        throw '下载结果缺少 Git 元数据或项目特征。'
    }
    $origin = (& git -C $target remote get-url origin).Trim()
    if (![string]::Equals(
        [IO.Path]::GetFullPath($origin),
        [IO.Path]::GetFullPath($remote),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "origin 不匹配：$origin"
    }
    if (!($lines | Where-Object { $_ -match '^::axtools .*"type":"result".*"status":"success"' })) {
        throw '项目下载脚本缺少成功 result。'
    }

    Write-Output 'PASS: 固定项目下载脚本契约通过。'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
exit 0
