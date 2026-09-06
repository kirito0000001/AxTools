using AxTools.Core.Services;
using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_CreatesDefaultSettingsWithSevenToolEntries()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);

        var settings = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(projectRoot, settings.ProjectRootPath);
        Assert.Equal("AxTools", settings.LastPageTag);
        Assert.Equal(7, settings.ManagedTools.Count);
        Assert.Contains("GalExcleTools", settings.ManagedTools.Keys);
        Assert.Contains("CrossingVoidZDTool", settings.ManagedTools.Keys);
        Assert.Contains("FantasyProject-PC", settings.ManagedTools.Keys);
        Assert.True(File.Exists(Path.Combine(projectRoot, "AxTools.settings.json")));
        Assert.All(settings.ManagedTools, item => Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", item.Key),
            item.Value.OutputRoot));
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyOutputsIntoWorkspaceWithoutChangingReleaseExecutables()
    {
        var bootstrap = Path.Combine(_root, "AppData-Migration", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Unified", "Ax工具箱项目");
        Directory.CreateDirectory(projectRoot);
        var releaseExecutable = Path.Combine(_root, "LegacyRelease", "Tool.exe");
        var json = $$"""
        {
          "schemaVersion": 5,
          "projectRootPath": "{{projectRoot.Replace("\\", "\\\\")}}",
          "managedTools": {
            "AxTools": { "outputRoot": "D:\\UnrealMap\\AxTools\\Artifacts" },
            "FantasyTools": { "outputRoot": "D:\\UnrealMap\\FantasyTools\\ReleaseAssets" },
            "CrossingVoidinitiator-PC": {
              "outputRoot": "D:\\UnrealMap\\CrossingVoidinitiator-PC\\ReleaseAssets",
              "releaseExecutable": "{{releaseExecutable.Replace("\\", "\\\\")}}"
            }
          },
          "crossingVoidPackage": {
            "outputDirectory": "D:\\LegacyChunks"
          }
        }
        """;
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "AxTools.settings.json"),
            json);
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);

        var settings = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(9, settings.SchemaVersion);
        Assert.All(settings.ManagedTools, item => Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", item.Key),
            item.Value.OutputRoot));
        Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", "CrossingVoidinitiator-PC"),
            settings.CrossingVoidPackage.OutputDirectory);
        Assert.Equal(
            releaseExecutable,
            settings.ManagedTools["CrossingVoidinitiator-PC"].ReleaseExecutable);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "Artifacts")));
    }

    [Fact]
    public async Task LoadAsync_AddsFantasyProjectPcToOldSettingsWithoutChangingExistingPaths()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        Directory.CreateDirectory(projectRoot);
        var oldFantasyPath = Path.Combine(_root, "Existing FantasyTools");
        var oldCrossingPath = Path.Combine(_root, "Existing Crossing");
        var json = $$"""
        {
          "schemaVersion": 2,
          "projectRootPath": "{{projectRoot.Replace("\\", "\\\\")}}",
          "managedTools": {
            "AxTools": { "sourceRoot": "D:\\UnrealMap\\AxTools" },
            "FantasyTools": { "sourceRoot": "{{oldFantasyPath.Replace("\\", "\\\\")}}" },
            "CrossingVoidinitiator-PC": { "sourceRoot": "{{oldCrossingPath.Replace("\\", "\\\\")}}" }
          }
        }
        """;
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "AxTools.settings.json"),
            json);
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);

        var settings = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(7, settings.ManagedTools.Count);
        Assert.Equal(oldFantasyPath, settings.ManagedTools["FantasyTools"].SourceRoot);
        Assert.Equal(oldCrossingPath, settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot);
        Assert.Equal(string.Empty, settings.ManagedTools["FantasyProject-PC"].SourceRoot);
        Assert.Equal(string.Empty, settings.ManagedTools["GalExcleTools"].SourceRoot);
        Assert.Equal(string.Empty, settings.ManagedTools["CrossingVoidZDTool"].SourceRoot);
    }

    [Fact]
    public async Task LoadAsync_BacksUpCorruptSettingsAndReturnsDefaults()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "AxTools.settings.json"),
            "{broken");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);

        var settings = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(projectRoot, settings.ProjectRootPath);
        Assert.Single(Directory.GetFiles(projectRoot, "AxTools.settings.corrupt-*.json"));
    }

    [Fact]
    public async Task LoadAsync_UsesAndPersistsUpdateDefaults()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);

        var settings = await service.LoadAsync(CancellationToken.None);
        Assert.Equal(UpdateSource.GitHub, settings.UpdateSource);
        Assert.Equal(UpdateChannel.Stable, settings.UpdateChannel);
        Assert.True(settings.UpdateAutoCheckEnabled);
        Assert.True(settings.UpdateCheckOnStartup);
        Assert.Equal(120, settings.UpdateConnectionTimeoutSeconds);

        settings.UpdateSource = UpdateSource.Gitee;
        settings.UpdateChannel = UpdateChannel.Beta;
        settings.UpdateConnectionTimeoutSeconds = 300;
        await service.SaveAsync(settings, CancellationToken.None);
        var restored = await service.LoadAsync(CancellationToken.None);
        Assert.Equal(UpdateSource.Gitee, restored.UpdateSource);
        Assert.Equal(UpdateChannel.Beta, restored.UpdateChannel);
        Assert.Equal(300, restored.UpdateConnectionTimeoutSeconds);
    }

    [Fact]
    public async Task SaveAsync_NormalizesManagedOutputsBeforeWritingJson()
    {
        var bootstrap = Path.Combine(_root, "AppData-SavePolicy", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "SavePolicy", "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = new AppSettings { ProjectRootPath = projectRoot };
        settings.ManagedTools["FantasyTools"].OutputRoot = @"D:\ExternalReleaseAssets";
        settings.CrossingVoidPackage.OutputDirectory = @"D:\ExternalChunks";

        await service.SaveAsync(settings, CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(projectRoot, "AxTools.settings.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", "FantasyTools"),
            root.GetProperty("managedTools")
                .GetProperty("FantasyTools")
                .GetProperty("outputRoot")
                .GetString());
        Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", "CrossingVoidinitiator-PC"),
            root.GetProperty("crossingVoidPackage")
                .GetProperty("outputDirectory")
                .GetString());
    }

    [Fact]
    public async Task SaveAsync_PersistsCrossingVoidPackageSettingsAndMigratesSchema()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);

        settings.CrossingVoidPackage.GameDirectory = @"D:\Packages\CrossingVoid";
        settings.CrossingVoidPackage.OutputDirectory = @"D:\Packages\GitChunks";
        settings.CrossingVoidPackage.BandizipExecutable = @"C:\Program Files\Bandizip\bz.exe";
        settings.CrossingVoidPackage.GameVersion = "V0.5.14";
        await service.SaveAsync(settings, CancellationToken.None);

        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(9, restored.SchemaVersion);
        Assert.Equal(@"D:\Packages\CrossingVoid", restored.CrossingVoidPackage.GameDirectory);
        Assert.Equal(
            Path.Combine(projectRoot, "Artifacts", "CrossingVoidinitiator-PC"),
            restored.CrossingVoidPackage.OutputDirectory);
        Assert.Equal(@"C:\Program Files\Bandizip\bz.exe", restored.CrossingVoidPackage.BandizipExecutable);
        Assert.Equal("V0.5.14", restored.CrossingVoidPackage.GameVersion);
    }

    [Fact]
    public async Task SaveAsync_PersistsSeparateGamePackageRootsForPcAndAndroid()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        settings.ManagedTools["CrossingVoidinitiator-PC"].GamePackageRoot = @"D:\DabaoV\Client\Windows";
        settings.ManagedTools["CrossingVoidinitiator-Android"].GamePackageRoot = @"D:\DabaoV\Client\Android";

        await service.SaveAsync(settings, CancellationToken.None);
        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(
            @"D:\DabaoV\Client\Windows",
            restored.ManagedTools["CrossingVoidinitiator-PC"].GamePackageRoot);
        Assert.Equal(
            @"D:\DabaoV\Client\Android",
            restored.ManagedTools["CrossingVoidinitiator-Android"].GamePackageRoot);
    }

    [Fact]
    public async Task LoadAsync_ClearsConflictingGamePublishingStateForPcAndAndroid()
    {
        var bootstrap = Path.Combine(_root, "AppData", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        var pc = settings.ManagedTools["CrossingVoidinitiator-PC"];
        var android = settings.ManagedTools["CrossingVoidinitiator-Android"];
        pc.GamePublishVersion = "0.5.12";
        pc.GamePublishChannel = "stable";
        android.GamePublishVersion = "0.5.12.1";
        android.GamePublishChannel = "beta";

        await service.SaveAsync(settings, CancellationToken.None);
        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(string.Empty, restored.ManagedTools["CrossingVoidinitiator-PC"].GamePublishVersion);
        Assert.Equal("stable", restored.ManagedTools["CrossingVoidinitiator-PC"].GamePublishChannel);
        Assert.Equal(string.Empty, restored.ManagedTools["CrossingVoidinitiator-Android"].GamePublishVersion);
        Assert.Equal("stable", restored.ManagedTools["CrossingVoidinitiator-Android"].GamePublishChannel);
    }

    [Fact]
    public async Task LoadAsync_PreservesMatchingGamePublishingStateForPcAndAndroid()
    {
        var bootstrap = Path.Combine(_root, "AppData2", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目2");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        var pc = settings.ManagedTools["CrossingVoidinitiator-PC"];
        var android = settings.ManagedTools["CrossingVoidinitiator-Android"];
        pc.GamePublishVersion = "0.5.14";
        android.GamePublishVersion = "0.5.14";
        pc.GamePublishChannel = "beta";
        android.GamePublishChannel = "beta";
        pc.GamePublishReleaseNotes = "双端资源更新";
        android.GamePublishReleaseNotes = "双端资源更新";
        settings.AxToolsReleaseNotes = "统一发布流程";

        await service.SaveAsync(settings, CancellationToken.None);
        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal("0.5.14", restored.ManagedTools["CrossingVoidinitiator-PC"].GamePublishVersion);
        Assert.Equal("0.5.14", restored.ManagedTools["CrossingVoidinitiator-Android"].GamePublishVersion);
        Assert.Equal("beta", restored.ManagedTools["CrossingVoidinitiator-PC"].GamePublishChannel);
        Assert.Equal("beta", restored.ManagedTools["CrossingVoidinitiator-Android"].GamePublishChannel);
        Assert.Equal("双端资源更新", restored.ManagedTools["CrossingVoidinitiator-PC"].GamePublishReleaseNotes);
        Assert.Equal("双端资源更新", restored.ManagedTools["CrossingVoidinitiator-Android"].GamePublishReleaseNotes);
        Assert.Equal("统一发布流程", restored.AxToolsReleaseNotes);
    }

    [Fact]
    public async Task SaveAsync_PreservesLauncherPublishSettingsForEveryTool()
    {
        var bootstrap = Path.Combine(_root, "AppData-Versions", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目-Versions");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        var index = 0;
        foreach (var tool in settings.ManagedTools.OrderBy(item => item.Key))
        {
            tool.Value.PublishVersion = $"1.{index++}.0";
            tool.Value.PublishChannel = index % 2 == 0 ? "stable" : "beta";
            tool.Value.PublishReleaseNotes = $"{tool.Key} 更新说明";
        }

        await service.SaveAsync(settings, CancellationToken.None);
        var restored = await service.LoadAsync(CancellationToken.None);

        foreach (var tool in settings.ManagedTools)
        {
            Assert.Equal(tool.Value.PublishVersion, restored.ManagedTools[tool.Key].PublishVersion);
            Assert.Equal(tool.Value.PublishChannel, restored.ManagedTools[tool.Key].PublishChannel);
            Assert.Equal(tool.Value.PublishReleaseNotes, restored.ManagedTools[tool.Key].PublishReleaseNotes);
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyAxToolsReleaseNotesToManagedToolSettings()
    {
        var bootstrap = Path.Combine(_root, "AppData-AxNotes", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目-AxNotes");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        settings.AxToolsReleaseNotes = "旧版 AxTools 更新说明";
        await service.SaveAsync(settings, CancellationToken.None);

        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(
            "旧版 AxTools 更新说明",
            restored.ManagedTools["AxTools"].PublishReleaseNotes);
    }

    [Fact]
    public async Task LoadAsync_PrefersPcProjectConfiguredLauncherVersionOverCachedInput()
    {
        var bootstrap = Path.Combine(_root, "AppData-PcVersion", "AxTools", "bootstrap.json");
        var projectRoot = Path.Combine(_root, "Ax工具箱项目-PcVersion");
        var sourceRoot = Path.Combine(_root, "CrossingVoidinitiator-PC");
        var service = new AppSettingsService(
            new AtomicJsonFileService(),
            bootstrap,
            () => projectRoot);
        var settings = await service.LoadAsync(CancellationToken.None);
        var pc = settings.ManagedTools["CrossingVoidinitiator-PC"];
        pc.SourceRoot = sourceRoot;
        pc.PublishVersion = "1.0.1";
        await service.SaveAsync(settings, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Saved", "Launcher"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Saved", "Launcher", "developer-version.json"),
            "{\"version\":\"1.1.0\"}");

        var restored = await service.LoadAsync(CancellationToken.None);

        Assert.Equal("1.1.0", restored.ManagedTools["CrossingVoidinitiator-PC"].PublishVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
