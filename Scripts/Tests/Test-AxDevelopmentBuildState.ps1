[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $scriptsRoot 'Common\AxAdapterCommon.psm1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AxTools-DevelopmentBuildState-' + [Guid]::NewGuid().ToString('N'))
$originalStateRoot = $env:AXTOOLS_BUILD_STATE_ROOT

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (!$Condition) { throw $Message }
}

function Assert-False {
    param([bool]$Condition,[string]$Message)
    if ($Condition) { throw $Message }
}

try {
    $stateRoot = Join-Path $testRoot 'state'
    $dotNetRoot = Join-Path $testRoot 'dotnet'
    $dotNetOutput = Join-Path $dotNetRoot 'bin\x64\Debug'
    New-Item -ItemType Directory -Path $stateRoot,$dotNetOutput,(Join-Path $dotNetRoot 'obj'),(Join-Path $dotNetRoot 'Tests') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Tool.csproj') -Value '<Project />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Main.cs') -Value 'class MainClass { }' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'obj\generated.cs') -Value 'generated-1' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Tests\test-source.cs') -Value 'test-1' -Encoding UTF8
    $dotNetExe = Join-Path $dotNetOutput 'Tool.exe'
    $dotNetPri = Join-Path $dotNetOutput 'Tool.pri'
    Set-Content -LiteralPath $dotNetExe -Value 'exe-v1' -Encoding UTF8
    Set-Content -LiteralPath $dotNetPri -Value 'pri-v1' -Encoding UTF8

    $env:AXTOOLS_BUILD_STATE_ROOT = $stateRoot
    Import-Module $modulePath -Force
    $moduleSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $modulePath
    Assert-True ($moduleSource -match '\.EnumerateDirectories\(') '源码指纹必须在进入子目录前进行目录剪枝。'
    Assert-True ($moduleSource -match "'Tests'") '源码指纹必须排除 Tests 目录。'
    Assert-False ($moduleSource -match 'Get-ChildItem\s+-LiteralPath\s+\$root\s+-File\s+-Recurse') '源码指纹不得先递归整个项目再过滤。'
    $restoreCacheSource = [regex]::Match(
        $moduleSource,
        'function Test-AxDotNetRestoreCache(?<body>[\s\S]*?)function Invoke-AxDotNetIncrementalBuild').Groups['body'].Value
    Assert-True ($restoreCacheSource -match '\.EnumerateDirectories\(') '依赖缓存检查必须在进入子目录前进行目录剪枝。'
    Assert-False ($restoreCacheSource -match 'Get-ChildItem[\s\S]*-Recurse') '依赖缓存检查不得先递归整个项目再过滤。'
    Assert-True ($moduleSource -match "依赖检查 · 正在扫描项目缓存") '统一 .NET 构建器必须明确报告依赖缓存扫描阶段。'
    Assert-True ($moduleSource -match "依赖检查 · 缓存有效") '统一 .NET 构建器必须明确报告依赖缓存命中。'
    Assert-True ($moduleSource -match "依赖恢复 · 正在更新 NuGet 缓存") '统一 .NET 构建器必须明确报告 NuGet 恢复阶段。'
    Assert-True ($moduleSource -match "增量编译 · 正在构建") '统一 .NET 构建器必须明确报告实际编译阶段。'
    Assert-True ($moduleSource -match "增量编译 · 构建成功") '统一 .NET 构建器必须明确报告 MSBuild 已成功结束。'
    Assert-True ($moduleSource -match '-Detail\s+"项目目录：\$ProjectRoot"') '依赖检查进度必须显示实际项目目录。'
    Assert-True ($moduleSource -match '-Detail\s+"配置：\$Configuration') '编译进度必须显示构建配置。'

    $dotNetParameters = @{
        ToolKey = 'TestDotNet'
        ProjectRoot = $dotNetRoot
        Profile = 'DotNet'
        Configuration = 'DebugX64'
        ExecutablePath = $dotNetExe
        RequiredResourcePaths = @($dotNetPri)
    }

    Assert-False (Test-AxDevelopmentBuildCurrent @dotNetParameters) '首次检查不能命中构建状态。'
    Write-AxDevelopmentBuildState @dotNetParameters
    Assert-True (Test-AxDevelopmentBuildCurrent @dotNetParameters) '保存成功构建状态后应命中。'

    Set-Content -LiteralPath (Join-Path $dotNetRoot 'obj\generated.cs') -Value 'generated-2' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetOutput 'ignored.cache') -Value 'cache' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Tests\test-source.cs') -Value 'test-2' -Encoding UTF8
    Assert-True (Test-AxDevelopmentBuildCurrent @dotNetParameters) 'Tests、obj 和 bin 变化不应触发重新构建。'

    $packageRoot = Join-Path $testRoot 'packages'
    $restoreAssetsPath = Join-Path $dotNetRoot 'obj\project.assets.json'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    [ordered]@{
        packageFolders = [ordered]@{ ($packageRoot + [IO.Path]::DirectorySeparatorChar) = @{} }
        libraries = [ordered]@{}
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $restoreAssetsPath -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Tests\Ignored.csproj') -Value '<Project />' -Encoding UTF8
    Assert-True (Test-AxDotNetRestoreCache -ProjectRoot $dotNetRoot) '依赖缓存检查不应扫描 Tests、bin 或 obj 中的项目文件。'

    $packagePath = Join-Path $packageRoot 'contoso.utility\1.0.0'
    New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
    $projectPath = Join-Path $dotNetRoot 'Tool.csproj'
    Set-Content -LiteralPath $projectPath -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <Version>2.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Contoso.Utility" Version="1.0.0" />
  </ItemGroup>
</Project>
'@ -Encoding UTF8
    [ordered]@{
        packageFolders = [ordered]@{ ($packageRoot + [IO.Path]::DirectorySeparatorChar) = @{} }
        libraries = [ordered]@{
            'Contoso.Utility/1.0.0' = [ordered]@{ type='package'; path='contoso.utility/1.0.0' }
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $restoreAssetsPath -Encoding UTF8
    $restoreSpecPath = Join-Path $dotNetRoot 'obj\Tool.csproj.nuget.dgspec.json'
    [ordered]@{
        projects = [ordered]@{
            $projectPath = [ordered]@{
                restore = [ordered]@{
                    originalTargetFrameworks = @('net8.0')
                    frameworks = [ordered]@{
                        'net8.0' = [ordered]@{ projectReferences = [ordered]@{} }
                    }
                }
                frameworks = [ordered]@{
                    'net8.0' = [ordered]@{
                        targetAlias = 'net8.0'
                        dependencies = [ordered]@{
                            'Contoso.Utility' = [ordered]@{ target='Package'; version='[1.0.0, )' }
                        }
                    }
                }
                runtimes = [ordered]@{ 'win-x64' = [ordered]@{} }
            }
        }
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $restoreSpecPath -Encoding UTF8
    $snapshotTime = [DateTime]::UtcNow.AddMinutes(-5)
    [IO.File]::SetLastWriteTimeUtc($restoreAssetsPath, $snapshotTime)
    [IO.File]::SetLastWriteTimeUtc($restoreSpecPath, $snapshotTime)
    [IO.File]::SetLastWriteTimeUtc($projectPath, [DateTime]::UtcNow)
    Assert-True (Test-AxDotNetRestoreCache -ProjectRoot $dotNetRoot) `
        '项目仅修改版本或内容配置、NuGet 依赖未变化时不应执行 restore。'

    (Get-Content -Raw -Encoding UTF8 -LiteralPath $projectPath).Replace(
        'PackageReference Include="Contoso.Utility" Version="1.0.0"',
        'PackageReference Include="Contoso.Utility" Version="2.0.0"') |
        Set-Content -LiteralPath $projectPath -Encoding UTF8
    Assert-False (Test-AxDotNetRestoreCache -ProjectRoot $dotNetRoot) `
        'PackageReference 版本变化后必须执行 restore。'

    Set-Content -LiteralPath (Join-Path $dotNetRoot 'Main.cs') -Value 'class MainClass { int Value; }' -Encoding UTF8
    Assert-False (Test-AxDevelopmentBuildCurrent @dotNetParameters) 'C# 源码变化后必须重新构建。'
    Write-AxDevelopmentBuildState @dotNetParameters

    Set-Content -LiteralPath $dotNetExe -Value 'exe-v2-with-new-content' -Encoding UTF8
    Assert-False (Test-AxDevelopmentBuildCurrent @dotNetParameters) '开发 EXE 被替换后必须重新构建。'
    Write-AxDevelopmentBuildState @dotNetParameters

    Remove-Item -LiteralPath $dotNetPri -Force
    Assert-False (Test-AxDevelopmentBuildCurrent @dotNetParameters) '必要 PRI 资源缺失后必须重新构建。'
    Set-Content -LiteralPath $dotNetPri -Value 'pri-v2' -Encoding UTF8
    Write-AxDevelopmentBuildState @dotNetParameters

    $dotNetStatePath = Get-AxDevelopmentBuildStatePath -ToolKey 'TestDotNet'
    Set-Content -LiteralPath $dotNetStatePath -Value '{invalid json' -Encoding UTF8
    Assert-False (Test-AxDevelopmentBuildCurrent @dotNetParameters) '损坏状态必须安全回退到重新构建。'
    Write-AxDevelopmentBuildState @dotNetParameters
    Clear-AxDevelopmentBuildState -ToolKey 'TestDotNet'
    Assert-False (Test-Path -LiteralPath $dotNetStatePath) '清除状态后 JSON 仍然存在。'

    $tauriRoot = Join-Path $testRoot 'tauri'
    $tauriTarget = Join-Path $tauriRoot 'src-tauri\target\debug'
    New-Item -ItemType Directory -Path $tauriTarget,(Join-Path $tauriRoot 'src'),(Join-Path $tauriRoot 'node_modules\pkg') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $tauriRoot 'package.json') -Value '{}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'package-lock.json') -Value '{}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'src\App.vue') -Value '<template />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'src-tauri\Cargo.toml') -Value '[package]' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'src-tauri\main.rs') -Value 'fn main() {}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'node_modules\pkg\index.js') -Value 'cached-1' -Encoding UTF8
    $tauriExe = Join-Path $tauriTarget 'launcher.exe'
    Set-Content -LiteralPath $tauriExe -Value 'tauri-exe' -Encoding UTF8
    $tauriParameters = @{
        ToolKey = 'TestTauri'
        ProjectRoot = $tauriRoot
        Profile = 'Tauri'
        Configuration = 'DebugX64'
        ExecutablePath = $tauriExe
    }

    Write-AxDevelopmentBuildState @tauriParameters
    Set-Content -LiteralPath (Join-Path $tauriTarget 'incremental.bin') -Value 'target-cache' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tauriRoot 'node_modules\pkg\index.js') -Value 'cached-2' -Encoding UTF8
    Assert-True (Test-AxDevelopmentBuildCurrent @tauriParameters) 'target 和 node_modules 变化不应触发重新构建。'

    Set-Content -LiteralPath (Join-Path $tauriRoot 'src\App.vue') -Value '<template><main /></template>' -Encoding UTF8
    Assert-False (Test-AxDevelopmentBuildCurrent @tauriParameters) 'Vue 源码变化后必须重新构建。'
}
finally {
    Remove-Module AxAdapterCommon -ErrorAction SilentlyContinue
    $env:AXTOOLS_BUILD_STATE_ROOT = $originalStateRoot
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'PASS: 开发构建状态与源码指纹合同通过。'
exit 0
