using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class StorageCleanupServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.StorageCleanup",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupAsync_DeletesAllowlistedDirectoryAndReportsReleasedBytes()
    {
        var settings = CreateSettings();
        var binPath = Path.Combine(
            settings.ManagedTools["AxTools"].SourceRoot,
            "bin");
        Directory.CreateDirectory(binPath);
        File.WriteAllBytes(Path.Combine(binPath, "cache.bin"), new byte[128]);
        var catalog = CreateCatalog();
        var scan = await new StorageScanService(catalog).ScanAsync(
            settings,
            progress: null,
            CancellationToken.None);
        var item = Assert.Single(scan.Items, value =>
            string.Equals(value.Path, binPath, StringComparison.OrdinalIgnoreCase));

        var result = await new StorageCleanupService(catalog).CleanupAsync(
            settings,
            new[] { item },
            currentExecutablePath: Path.Combine(_testRoot, "running", "AxTools.exe"),
            progress: null,
            CancellationToken.None);

        Assert.False(Directory.Exists(binPath));
        Assert.Equal(128, result.ReleasedBytes);
        Assert.Contains(binPath, result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task CleanupAsync_RejectsForgedSourceDirectoryBeforeDeletingAnything()
    {
        var settings = CreateSettings();
        var sourcePath = Path.Combine(
            settings.ManagedTools["AxTools"].SourceRoot,
            "Content");
        Directory.CreateDirectory(sourcePath);
        var sourceFile = Path.Combine(sourcePath, "keep.uasset");
        File.WriteAllText(sourceFile, "keep");
        var forgedItem = new StorageScanItem(
            "AxTools",
            "伪造缓存",
            sourcePath,
            StorageCategory.RebuildableCache,
            "伪造",
            new FileInfo(sourceFile).Length,
            1,
            File.GetLastWriteTime(sourceFile),
            DateTimeOffset.Now);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new StorageCleanupService(CreateCatalog()).CleanupAsync(
                settings,
                new[] { forgedItem },
                currentExecutablePath: Path.Combine(_testRoot, "running", "AxTools.exe"),
                progress: null,
                CancellationToken.None));

        Assert.Contains("不在固定白名单", exception.Message);
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task CleanupAsync_RejectsDirectoryContainingCurrentExecutable()
    {
        var settings = CreateSettings();
        var binPath = Path.Combine(
            settings.ManagedTools["AxTools"].SourceRoot,
            "bin");
        var executablePath = Path.Combine(binPath, "x64", "Release", "AxTools.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, "running");
        var catalog = CreateCatalog();
        var scan = await new StorageScanService(catalog).ScanAsync(
            settings,
            progress: null,
            CancellationToken.None);
        var item = Assert.Single(scan.Items, value =>
            string.Equals(value.Path, binPath, StringComparison.OrdinalIgnoreCase));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new StorageCleanupService(catalog).CleanupAsync(
                settings,
                new[] { item },
                executablePath,
                progress: null,
                CancellationToken.None));

        Assert.Contains("当前正在运行", exception.Message);
        Assert.True(File.Exists(executablePath));
    }

    [Fact]
    public async Task CleanupAsync_RejectsChangedDirectorySnapshot()
    {
        var settings = CreateSettings();
        var binPath = Path.Combine(
            settings.ManagedTools["AxTools"].SourceRoot,
            "bin");
        Directory.CreateDirectory(binPath);
        File.WriteAllText(Path.Combine(binPath, "before.bin"), "before");
        var catalog = CreateCatalog();
        var scan = await new StorageScanService(catalog).ScanAsync(
            settings,
            progress: null,
            CancellationToken.None);
        var item = Assert.Single(scan.Items, value =>
            string.Equals(value.Path, binPath, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(binPath, "after.bin"), "new content");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new StorageCleanupService(catalog).CleanupAsync(
                settings,
                new[] { item },
                currentExecutablePath: Path.Combine(_testRoot, "running", "AxTools.exe"),
                progress: null,
                CancellationToken.None));

        Assert.Contains("扫描结果已过期", exception.Message);
        Assert.True(File.Exists(Path.Combine(binPath, "before.bin")));
        Assert.True(File.Exists(Path.Combine(binPath, "after.bin")));
    }

    [Fact]
    public async Task CleanupAsync_AllowsExactUnrealIntermediateButNeverProjectRoot()
    {
        var settings = CreateSettings();
        var unrealRoot = Path.Combine(_testRoot, "CrossingVoid");
        var intermediate = Path.Combine(unrealRoot, "Intermediate");
        Directory.CreateDirectory(intermediate);
        File.WriteAllBytes(Path.Combine(intermediate, "cache.bin"), new byte[64]);
        var catalog = new StorageCatalogService(
            new SharedCacheRoots(
                Path.Combine(_testRoot, "Profile"),
                Path.Combine(_testRoot, "LocalAppData"),
                Path.Combine(_testRoot, "Temp")),
            new UnrealProjectRoots(unrealRoot, Path.Combine(_testRoot, "FantasyProject")));
        var scan = await new StorageScanService(catalog).ScanAsync(
            settings,
            null,
            CancellationToken.None);
        var item = Assert.Single(scan.Items, value =>
            string.Equals(value.Path, intermediate, StringComparison.OrdinalIgnoreCase));

        var result = await new StorageCleanupService(catalog).CleanupAsync(
            settings,
            [item],
            Path.Combine(_testRoot, "running", "AxTools.exe"),
            null,
            CancellationToken.None);

        Assert.False(Directory.Exists(intermediate));
        Assert.Equal(64, result.ReleasedBytes);
        Assert.True(Directory.Exists(unrealRoot));
    }

    private AppSettings CreateSettings()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_testRoot, "Ax工具箱项目")
        };
        settings.ManagedTools["AxTools"].SourceRoot = Path.Combine(_testRoot, "AxTools");
        settings.ManagedTools["FantasyTools"].SourceRoot = Path.Combine(_testRoot, "FantasyTools");
        settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot =
            Path.Combine(_testRoot, "CrossingVoidinitiator-PC");
        return settings;
    }

    private StorageCatalogService CreateCatalog() => new(
        new SharedCacheRoots(
            Path.Combine(_testRoot, "Profile"),
            Path.Combine(_testRoot, "LocalAppData"),
            Path.Combine(_testRoot, "Temp")),
        new UnrealProjectRoots(
            Path.Combine(_testRoot, "CrossingVoid"),
            Path.Combine(_testRoot, "FantasyProject")));

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
