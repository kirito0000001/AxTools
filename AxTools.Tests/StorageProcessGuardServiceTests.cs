using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class StorageProcessGuardServiceTests
{
    [Fact]
    public void FindConflicts_MatchesOnlyProcessesRelevantToSelectedCaches()
    {
        var snapshots = new[]
        {
            new ProcessSnapshot(10, "dotnet", @"C:\Program Files\dotnet\dotnet.exe"),
            new ProcessSnapshot(11, "node", @"C:\Program Files\nodejs\node.exe"),
            new ProcessSnapshot(12, "UnrealEditor", @"D:\UE\UnrealEditor.exe")
        };
        var guard = new StorageProcessGuardService(() => snapshots);

        var conflicts = guard.FindConflicts([
            Item("SharedCache", "NuGet 全局包缓存"),
            Item("CrossingVoid", "CrossingVoid Intermediate")
        ]);

        Assert.Contains(conflicts, item => item.Process.ProcessId == 10 && item.Ecosystem == ".NET / NuGet");
        Assert.Contains(conflicts, item => item.Process.ProcessId == 12 && item.Ecosystem == "Unreal Engine");
        Assert.DoesNotContain(conflicts, item => item.Process.ProcessId == 11);
    }

    private static StorageScanItem Item(string tool, string displayName) => new(
        tool,
        displayName,
        @"D:\Cache\" + displayName,
        StorageCategory.RebuildableCache,
        "impact",
        100,
        1,
        DateTimeOffset.Now,
        DateTimeOffset.Now);
}
