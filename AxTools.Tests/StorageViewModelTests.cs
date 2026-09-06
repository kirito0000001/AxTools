using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class StorageViewModelTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.StorageViewModel",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanAsync_PopulatesUncheckedItemsAndSelectionEnablesCleanup()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_testRoot, "Ax工具箱项目")
        };
        var sourceRoot = Path.Combine(_testRoot, "AxTools");
        settings.ManagedTools["AxTools"].SourceRoot = sourceRoot;
        Directory.CreateDirectory(Path.Combine(sourceRoot, "obj"));
        File.WriteAllText(Path.Combine(sourceRoot, "obj", "cache.bin"), "cache");
        var catalog = new StorageCatalogService(
            new SharedCacheRoots(
                Path.Combine(_testRoot, "Profile"),
                Path.Combine(_testRoot, "LocalAppData"),
                Path.Combine(_testRoot, "Temp")),
            new UnrealProjectRoots(
                Path.Combine(_testRoot, "Unreal", "CrossingVoid"),
                Path.Combine(_testRoot, "Unreal", "FantasyProject")));
        var viewModel = new StorageViewModel(
            settings,
            new StorageScanService(catalog),
            new StorageCleanupService(catalog));

        await viewModel.ScanAsync(progress: null, CancellationToken.None);

        var item = Assert.Single(viewModel.Items);
        Assert.False(item.IsSelected);
        Assert.False(viewModel.CanCleanSelected);
        item.IsSelected = true;
        Assert.True(viewModel.CanCleanSelected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
