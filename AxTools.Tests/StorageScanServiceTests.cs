using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class StorageScanServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.StorageScan",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanAsync_IncludesUnifiedWorkspacePublishingOutputs()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_testRoot, "Unified", "Ax工具箱项目")
        };
        settings.ManagedTools["AxTools"].SourceRoot = Path.Combine(_testRoot, "AxTools");
        settings.ManagedTools["FantasyTools"].SourceRoot = Path.Combine(_testRoot, "FantasyTools");
        settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot =
            Path.Combine(_testRoot, "CrossingVoidinitiator-PC");
        WorkspaceArtifactPathPolicy.Apply(settings.ProjectRootPath, settings.ManagedTools);

        foreach (var key in new[] { "AxTools", "FantasyTools", "CrossingVoidinitiator-PC" })
        {
            CreateSizedFile(
                Path.Combine(settings.ManagedTools[key].OutputRoot, "release.bin"),
                21);
        }
        CreateSizedFile(
            Path.Combine(settings.ManagedTools["AxTools"].OutputRoot, ".work", "temp.bin"),
            8);

        var result = await new StorageScanService(CreateCatalog()).ScanAsync(
            settings,
            progress: null,
            CancellationToken.None);

        foreach (var key in new[] { "AxTools", "FantasyTools", "CrossingVoidinitiator-PC" })
        {
            Assert.Contains(result.Items, item =>
                item.ToolStableKey == key &&
                item.Path == Path.GetFullPath(settings.ManagedTools[key].OutputRoot) &&
                item.Category == StorageCategory.ReleaseArtifact);
        }
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "AxTools" &&
            item.Path == Path.GetFullPath(Path.Combine(
                settings.ManagedTools["AxTools"].OutputRoot,
                ".work")) &&
            item.Category == StorageCategory.SafeTemporary);
    }

    [Fact]
    public async Task ScanAsync_ReturnsOnlyFixedAllowlistWithExpectedCategories()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_testRoot, "Ax工具箱项目")
        };
        var axRoot = Path.Combine(_testRoot, "AxTools");
        var fantasyRoot = Path.Combine(_testRoot, "FantasyTools");
        var galExcleRoot = Path.Combine(_testRoot, "GalExcleTools");
        var galExcleOutput = Path.Combine(_testRoot, "DabaoV");
        var zdRoot = Path.Combine(_testRoot, "CrossingVoidZDTool");
        var zdOutput = Path.Combine(_testRoot, "ZD-DabaoV");
        var fantasyProjectPcRoot = Path.Combine(_testRoot, "FantasyProject-PC");
        var fantasyProjectPcOutput = Path.Combine(_testRoot, "FantasyProject-PC Output");
        var crossingRoot = Path.Combine(_testRoot, "CrossingVoidinitiator-PC");
        var crossingOutput = Path.Combine(settings.ProjectRootPath, "Artifacts", "CrossingVoidinitiator-PC");
        var crossingAndroidRoot = Path.Combine(_testRoot, "CrossingVoidinitiator-Android");
        settings.ManagedTools["AxTools"].SourceRoot = axRoot;
        settings.ManagedTools["FantasyTools"].SourceRoot = fantasyRoot;
        settings.ManagedTools["GalExcleTools"].SourceRoot = galExcleRoot;
        settings.ManagedTools["GalExcleTools"].OutputRoot = galExcleOutput;
        settings.ManagedTools["CrossingVoidZDTool"].SourceRoot = zdRoot;
        settings.ManagedTools["CrossingVoidZDTool"].OutputRoot = zdOutput;
        settings.ManagedTools["FantasyProject-PC"].SourceRoot = fantasyProjectPcRoot;
        settings.ManagedTools["FantasyProject-PC"].OutputRoot = fantasyProjectPcOutput;
        settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot = crossingRoot;
        settings.ManagedTools["CrossingVoidinitiator-PC"].OutputRoot = crossingOutput;
        settings.ManagedTools["CrossingVoidinitiator-Android"].SourceRoot = crossingAndroidRoot;
        settings.Toolchains.AndroidSdkRoot = Path.Combine(_testRoot, "Android", "Sdk");
        CreateSizedFile(Path.Combine(axRoot, "bin", "Debug", "app.dll"), 10);
        CreateSizedFile(Path.Combine(axRoot, "Content", "never-delete.uasset"), 20);
        CreateSizedFile(Path.Combine(fantasyRoot, "obj", "cache.bin"), 30);
        CreateSizedFile(Path.Combine(galExcleRoot, "bin", "Debug", "app.dll"), 31);
        CreateSizedFile(Path.Combine(galExcleRoot, "obj", "project.assets.json"), 32);
        CreateSizedFile(Path.Combine(galExcleRoot, "AppPackages", "old.msix"), 33);
        CreateSizedFile(Path.Combine(galExcleRoot, "Assets", "keep.png"), 34);
        CreateSizedFile(Path.Combine(galExcleRoot, "Docs", "keep.md"), 35);
        CreateSizedFile(Path.Combine(galExcleOutput, ".package-work", "publish", "temp.dll"), 36);
        CreateSizedFile(Path.Combine(galExcleOutput, "TFAC剧情箱-轮椅版V2.1.0", "release.exe"), 37);
        CreateSizedFile(Path.Combine(zdRoot, "obj", "project.assets.json"), 11);
        CreateSizedFile(Path.Combine(zdRoot, "bin", "verify", "verified.dll"), 12);
        CreateSizedFile(Path.Combine(zdRoot, "Tests", "CrossingVoidZDTool.RegressionTests", "obj", "test.assets.json"), 13);
        CreateSizedFile(Path.Combine(zdRoot, "Tests", "CrossingVoidZDTool.RegressionTests", "bin", "test.dll"), 14);
        CreateSizedFile(Path.Combine(zdRoot, "Assets", "keep.png"), 15);
        CreateSizedFile(Path.Combine(zdOutput, ".axtools-staging", "run", "package.tmp"), 16);
        CreateSizedFile(Path.Combine(_testRoot, "Temp", "CrossingVoidZDTool-Pakout", "work.tmp"), 17);
        CreateSizedFile(Path.Combine(crossingRoot, "src-tauri", "target", "cache.bin"), 40);
        CreateSizedFile(Path.Combine(crossingRoot, "node_modules", "package", "index.js"), 50);
        CreateSizedFile(Path.Combine(crossingOutput, "release.zip"), 60);
        CreateSizedFile(Path.Combine(crossingAndroidRoot, "node_modules", "package", "index.js"), 61);
        CreateSizedFile(Path.Combine(crossingAndroidRoot, "dist", "index.js"), 62);
        CreateSizedFile(Path.Combine(crossingAndroidRoot, "android", "build", "cache.bin"), 63);
        CreateSizedFile(Path.Combine(crossingAndroidRoot, "android", "app", "build", "app.apk"), 64);
        CreateSizedFile(Path.Combine(settings.Toolchains.AndroidSdkRoot, "ndk", "29.0.0", "ndk.exe"), 65);
        CreateSizedFile(Path.Combine(settings.Toolchains.AndroidSdkRoot, "system-images", "android-36", "image.bin"), 66);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "dist", "index.js"), 70);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "dist-launcher-update", "latest.json"), 80);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "src-tauri", "target", "cache.bin"), 90);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "node_modules", "package", "index.js"), 100);
        CreateSizedFile(Path.Combine(fantasyProjectPcOutput, "launcher-setup.exe"), 110);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "Saved", "game-cache.bin"), 120);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "OnSet", "game.bin"), 130);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "public", "asset.bin"), 140);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, "src-tauri", "private", "updater.key"), 150);
        CreateSizedFile(Path.Combine(fantasyProjectPcRoot, ".git", "objects", "object"), 160);

        var result = await new StorageScanService(CreateCatalog()).ScanAsync(
            settings,
            progress: null,
            CancellationToken.None);

        Assert.DoesNotContain(result.Items, item =>
            item.Path.Contains("Content", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item =>
            item.Path.EndsWith("bin", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.RebuildableCache &&
            item.SizeBytes == 10);
        Assert.Contains(result.Items, item =>
            item.Path.EndsWith("node_modules", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.HighCostCache &&
            item.SizeBytes == 50);
        Assert.Contains(result.Items, item =>
            item.Path == Path.GetFullPath(crossingOutput) &&
            item.Category == StorageCategory.ReleaseArtifact &&
            item.SizeBytes == 60);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            item.Path == Path.GetFullPath(Path.Combine(galExcleRoot, "bin")) &&
            item.Category == StorageCategory.RebuildableCache &&
            item.SizeBytes == 31);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            item.Path == Path.GetFullPath(Path.Combine(galExcleRoot, "obj")) &&
            item.Category == StorageCategory.RebuildableCache &&
            item.SizeBytes == 32);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            item.Path == Path.GetFullPath(Path.Combine(galExcleRoot, "AppPackages")) &&
            item.Category == StorageCategory.ReleaseArtifact &&
            item.SizeBytes == 33);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            item.Path == Path.GetFullPath(Path.Combine(galExcleOutput, ".package-work")) &&
            item.Category == StorageCategory.SafeTemporary &&
            item.SizeBytes == 36);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            item.Path == Path.GetFullPath(galExcleOutput) &&
            item.Category == StorageCategory.ReleaseArtifact &&
            item.SizeBytes == 73);
        Assert.DoesNotContain(result.Items, item =>
            item.ToolStableKey == "GalExcleTools" &&
            (item.Path.Contains("Assets", StringComparison.OrdinalIgnoreCase) ||
             item.Path.Contains("Docs", StringComparison.OrdinalIgnoreCase) ||
             item.Path == Path.GetFullPath(galExcleRoot)));
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path == Path.GetFullPath(Path.Combine(zdRoot, "obj")) &&
            item.Category == StorageCategory.RebuildableCache && item.SizeBytes == 11);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path == Path.GetFullPath(Path.Combine(zdRoot, "bin", "verify")) &&
            item.Category == StorageCategory.SafeTemporary && item.SizeBytes == 12);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path == Path.GetFullPath(Path.Combine(zdRoot, "bin")) &&
            item.Category == StorageCategory.HighCostCache);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path.EndsWith(Path.Combine("RegressionTests", "bin"), StringComparison.OrdinalIgnoreCase) &&
            item.SizeBytes == 14);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path == Path.GetFullPath(Path.Combine(zdOutput, ".axtools-staging")) &&
            item.Category == StorageCategory.SafeTemporary && item.SizeBytes == 16);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path == Path.GetFullPath(zdOutput) && !item.CanClean);
        Assert.DoesNotContain(result.Items, item =>
            item.ToolStableKey == "CrossingVoidZDTool" &&
            item.Path.Contains("Assets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            item.Path.EndsWith("dist", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.RebuildableCache &&
            item.SizeBytes == 70);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            item.Path.EndsWith("dist-launcher-update", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.RebuildableCache &&
            item.SizeBytes == 80);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            item.Path.EndsWith("target", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.HighCostCache &&
            item.SizeBytes == 90);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            item.Path.EndsWith("node_modules", StringComparison.OrdinalIgnoreCase) &&
            item.Category == StorageCategory.HighCostCache &&
            item.SizeBytes == 100);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            item.Path == Path.GetFullPath(fantasyProjectPcOutput) &&
            item.Category == StorageCategory.ReleaseArtifact &&
            item.SizeBytes == 110);
        Assert.DoesNotContain(result.Items, item =>
            item.ToolStableKey == "FantasyProject-PC" &&
            (item.Path.Contains("Saved", StringComparison.OrdinalIgnoreCase) ||
             item.Path.Contains("OnSet", StringComparison.OrdinalIgnoreCase) ||
             item.Path.Contains("public", StringComparison.OrdinalIgnoreCase) ||
             item.Path.Contains("private", StringComparison.OrdinalIgnoreCase) ||
             item.Path.Contains(".git", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "CrossingVoidinitiator-Android" &&
            item.Path.EndsWith("node_modules", StringComparison.OrdinalIgnoreCase) &&
            item.CanClean);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "AndroidSdk" &&
            item.Path.Contains("ndk", StringComparison.OrdinalIgnoreCase) &&
            !item.CanClean);
        Assert.Contains(result.Items, item =>
            item.ToolStableKey == "AndroidSdk" &&
            item.Path.Contains("system-images", StringComparison.OrdinalIgnoreCase) &&
            !item.CanClean);
    }

    [Fact]
    public async Task ScanAsync_ReportsGrowthComparedWithPreviousSnapshot()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_testRoot, "Ax工具箱项目")
        };
        var sourceRoot = Path.Combine(_testRoot, "AxToolsGrowth");
        settings.ManagedTools["AxTools"].SourceRoot = sourceRoot;
        var file = Path.Combine(sourceRoot, "obj", "cache.bin");
        CreateSizedFile(file, 10);
        var scanner = new StorageScanService(CreateCatalog());

        var first = await scanner.ScanAsync(settings, null, CancellationToken.None);
        var firstItem = Assert.Single(first.Items, item =>
            string.Equals(item.Path, Path.GetDirectoryName(file), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, firstItem.GrowthBytes);

        CreateSizedFile(file, 25);
        var second = await scanner.ScanAsync(settings, null, CancellationToken.None);
        var item = Assert.Single(second.Items, candidate =>
            string.Equals(candidate.Path, Path.GetDirectoryName(file), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, item.PreviousSizeBytes);
        Assert.Equal(15, item.GrowthBytes);
        Assert.True(File.Exists(Path.Combine(settings.ProjectRootPath, "StorageInventory.snapshot.json")));
    }

    private StorageCatalogService CreateCatalog() => new(
        new SharedCacheRoots(
            Path.Combine(_testRoot, "Profile"),
            Path.Combine(_testRoot, "LocalAppData"),
            Path.Combine(_testRoot, "Temp")),
        new UnrealProjectRoots(
            Path.Combine(_testRoot, "Unreal", "CrossingVoid"),
            Path.Combine(_testRoot, "Unreal", "FantasyProject")));

    private static void CreateSizedFile(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
