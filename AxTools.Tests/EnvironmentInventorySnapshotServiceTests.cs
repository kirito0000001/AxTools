using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class EnvironmentInventorySnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxTools.EnvironmentSnapshot",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApplyAsync_MarksNewAndVersionChangesAndPersistsAtomically()
    {
        var service = new EnvironmentInventorySnapshotService(new AtomicJsonFileService());
        var first = await service.ApplyAsync(
            _root,
            [Item("dotnet", "8.0.1")],
            CancellationToken.None);

        Assert.True(Assert.Single(first.Items).IsNew);
        Assert.Contains("首次发现", first.ChangeSummary);

        var second = await service.ApplyAsync(
            _root,
            [Item("dotnet", "8.0.2"), Item("node", "24.1")],
            CancellationToken.None);

        Assert.Contains(second.Items, item =>
            item.Key == "dotnet" &&
            !item.IsNew &&
            item.ChangeSummary.Contains("8.0.1 -> 8.0.2"));
        Assert.Contains(second.Items, item => item.Key == "node" && item.IsNew);
        Assert.True(File.Exists(Path.Combine(_root, "EnvironmentInventory.snapshot.json")));
        Assert.False(File.Exists(Path.Combine(_root, "EnvironmentInventory.snapshot.json.tmp")));
    }

    private static ToolchainStatusItem Item(string key, string version) => new(
        key,
        key,
        @"C:\Tools\" + key,
        true,
        "ok",
        CurrentVersion: version);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
