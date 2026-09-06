using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class StorageProcessGuardService
{
    private readonly Func<IReadOnlyList<ProcessSnapshot>> _snapshotProvider;

    public StorageProcessGuardService()
        : this(ProcessConflictDetector.CaptureRunningProcesses)
    {
    }

    public StorageProcessGuardService(Func<IReadOnlyList<ProcessSnapshot>> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    public IReadOnlyList<StorageProcessConflict> FindConflicts(
        IReadOnlyList<StorageScanItem> selectedItems)
    {
        var ecosystems = selectedItems
            .SelectMany(GetEcosystems)
            .DistinctBy(item => item.Name)
            .ToArray();
        return _snapshotProvider()
            .SelectMany(process => ecosystems
                .Where(ecosystem => ecosystem.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                .Select(ecosystem => new StorageProcessConflict(
                    process,
                    ecosystem.Name,
                    ecosystem.Reason)))
            .DistinctBy(conflict => conflict.Process.ProcessId)
            .ToArray();
    }

    private static IEnumerable<EcosystemRule> GetEcosystems(StorageScanItem item)
    {
        var text = $"{item.ToolStableKey} {item.DisplayName} {item.Path}";
        if (text.Contains("Unreal", StringComparison.OrdinalIgnoreCase) ||
            item.ToolStableKey is "CrossingVoid" or "FantasyProject")
        {
            yield return new EcosystemRule(
                "Unreal Engine",
                ["UnrealEditor", "ShaderCompileWorker", "UnrealBuildTool"],
                "Unreal 正在使用派生数据或项目构建目录");
        }

        if (text.Contains("NuGet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(".NET", StringComparison.OrdinalIgnoreCase))
        {
            yield return new EcosystemRule(
                ".NET / NuGet",
                ["dotnet", "MSBuild", "devenv"],
                ".NET 还原或构建可能正在读写缓存");
        }

        if (text.Contains("npm", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("node_modules", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("前端", StringComparison.OrdinalIgnoreCase))
        {
            yield return new EcosystemRule("Node / npm", ["node", "npm"], "Node 构建可能正在读写缓存");
        }

        if (text.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Rust", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("src-tauri", StringComparison.OrdinalIgnoreCase))
        {
            yield return new EcosystemRule("Rust / Cargo", ["cargo", "rustc"], "Rust 构建可能正在读写缓存");
        }

        if (text.Contains("Gradle", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            yield return new EcosystemRule("Android / Gradle", ["java", "gradle", "adb"], "Android 构建或设备任务可能正在运行");
        }
    }

    private sealed record EcosystemRule(
        string Name,
        IReadOnlyList<string> ProcessNames,
        string Reason);
}
