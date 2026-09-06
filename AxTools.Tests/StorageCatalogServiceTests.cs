using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class StorageCatalogServiceTests
{
    [Fact]
    public void CreateCandidates_IncludesSharedEcosystemCachesWithExplicitImpact()
    {
        var roots = new SharedCacheRoots(
            UserProfile: @"D:\Profiles\Test",
            LocalAppData: @"D:\Profiles\Test\AppData\Local",
            Temp: @"D:\Temp");

        var candidates = new StorageCatalogService(roots).CreateCandidates(new AppSettings());

        Assert.Contains(candidates, item =>
            item.DisplayName == "NuGet 全局包缓存" &&
            item.Category == StorageCategory.HighCostCache &&
            item.CanClean);
        Assert.Contains(candidates, item => item.DisplayName == "NuGet HTTP 缓存");
        Assert.Contains(candidates, item => item.DisplayName == "NuGet 临时缓存");
        Assert.Contains(candidates, item => item.DisplayName == "npm 下载缓存");
        Assert.Contains(candidates, item => item.DisplayName == "Cargo Registry 缓存");
        Assert.Contains(candidates, item => item.DisplayName == "Cargo Git 缓存");
        Assert.Contains(candidates, item => item.DisplayName == "Gradle 依赖缓存");
        Assert.Contains(candidates, item => item.DisplayName == "Gradle Wrapper 缓存");
        Assert.All(
            candidates.Where(item => item.ToolStableKey == "SharedCache"),
            item => Assert.Contains("其他项目", item.Impact));
    }

    [Fact]
    public void CreateCandidates_IncludesOnlyKnownUnrealDerivedDirectories()
    {
        var candidates = new StorageCatalogService(
            new SharedCacheRoots(
                UserProfile: @"D:\Profiles\Test",
                LocalAppData: @"D:\Profiles\Test\AppData\Local",
                Temp: @"D:\Temp"),
            new UnrealProjectRoots(
                @"D:\UnrealMap\CrossingVoid",
                @"D:\UnrealMap\FantasyProject")).CreateCandidates(new AppSettings());

        Assert.Contains(candidates, item =>
            item.ToolStableKey == "UnrealGlobal" &&
            item.DisplayName == "Unreal 全局派生数据缓存");
        Assert.Contains(candidates, item =>
            item.ToolStableKey == "CrossingVoid" &&
            item.Path.EndsWith(Path.Combine("CrossingVoid", "Intermediate")));
        Assert.Contains(candidates, item =>
            item.ToolStableKey == "FantasyProject" &&
            item.Path.EndsWith(Path.Combine("FantasyProject", "Saved", "Cooked")));
        Assert.Contains(candidates, item =>
            item.ToolStableKey == "CrossingVoid" &&
            item.Path.EndsWith(Path.Combine("CrossingVoid", "Binaries")) &&
            item.Category == StorageCategory.HighCostCache);
        Assert.DoesNotContain(candidates, item =>
            item.Path.EndsWith(Path.Combine("CrossingVoid", "Content")) ||
            item.Path.EndsWith(Path.Combine("CrossingVoid", "Config")) ||
            item.Path.EndsWith(Path.Combine("CrossingVoid", "Source")) ||
            item.Path.Contains(Path.Combine("CrossingVoid", "Plugins")) ||
            item.Path.EndsWith(".uproject") ||
            item.Path.Contains(Path.DirectorySeparatorChar + ".git"));
    }

    [Fact]
    public void CreateCandidates_ModelsLauncherSpecificSafeAndReadOnlyStorage()
    {
        var root = @"D:\Launchers";
        var settings = new AppSettings();
        settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot = Path.Combine(root, "PC");
        settings.ManagedTools["CrossingVoidinitiator-Android"].SourceRoot = Path.Combine(root, "Android");
        var candidates = new StorageCatalogService(new SharedCacheRoots(
            UserProfile: @"D:\Profiles\Test",
            LocalAppData: @"D:\Profiles\Test\AppData\Local",
            Temp: @"D:\Temp")).CreateCandidates(settings);

        Assert.Contains(candidates, item =>
            item.Path.EndsWith(Path.Combine("PC", "Logs")) &&
            item.Category == StorageCategory.SafeTemporary && item.CanClean);
        Assert.Contains(candidates, item =>
            item.Path.EndsWith(Path.Combine("PC", "Saved", "OssDryRun")) && item.CanClean);
        Assert.Contains(candidates, item =>
            item.Path.EndsWith(Path.Combine("PC", "dist-launcher-update-debug")) && item.CanClean);
        Assert.Contains(candidates, item =>
            item.Path.EndsWith(Path.Combine("PC", "Saved", "GamePackages")) && !item.CanClean);
        Assert.Contains(candidates, item =>
            item.Path.EndsWith(Path.Combine("Android", "android", ".gradle")) && item.CanClean);
    }
}
