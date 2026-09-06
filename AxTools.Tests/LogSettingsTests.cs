using System.Text.Json;
using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class LogSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxTools.LogSettings",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppSettings_UsesSpecifiedLogDefaults()
    {
        var settings = new AppSettings();

        Assert.False(settings.LogEnabled);
        Assert.False(settings.LogSaveToFileEnabled);
        Assert.True(settings.LogUserOperations);
        Assert.True(settings.LogWarnings);
        Assert.True(settings.LogErrors);
    }

    [Fact]
    public async Task ShouldWriteLog_AppliesMasterAndCategoryFilters()
    {
        var settings = new AppSettings
        {
            LogEnabled = true,
            LogUserOperations = false,
            LogWarnings = false,
            LogErrors = true
        };
        var viewModel = CreateViewModel(settings);

        Assert.True(viewModel.ShouldWriteLog(LogKind.Info));
        Assert.False(viewModel.ShouldWriteLog(LogKind.User));
        Assert.False(viewModel.ShouldWriteLog(LogKind.Warning));
        Assert.True(viewModel.ShouldWriteLog(LogKind.Error));

        viewModel.LogEnabled = false;

        Assert.False(viewModel.ShouldWriteLog(LogKind.Info));
        Assert.False(viewModel.ShouldWriteLog(LogKind.Error));
        await viewModel.PendingSettingsSave;
    }

    [Fact]
    public async Task LogSettingChange_SavesImmediatelyAndRaisesNotification()
    {
        var settings = new AppSettings { ProjectRootPath = Path.Combine(_root, "project") };
        var viewModel = CreateViewModel(settings);
        var notifications = 0;
        viewModel.LogSettingsChanged += (_, _) => notifications++;

        viewModel.LogEnabled = true;
        viewModel.LogWarnings = false;
        await viewModel.PendingLogSettingsSave;

        var json = await File.ReadAllTextAsync(
            SettingsPathProvider.GetSettingsPath(settings.ProjectRootPath));
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("logEnabled").GetBoolean());
        Assert.False(document.RootElement.GetProperty("logWarnings").GetBoolean());
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task UndoLastLogSettingAsync_RestoresPreviousValueAndPersistsIt()
    {
        var settings = new AppSettings { ProjectRootPath = Path.Combine(_root, "project") };
        var viewModel = CreateViewModel(settings);
        viewModel.LogSaveToFileEnabled = true;
        await viewModel.PendingLogSettingsSave;

        Assert.True(viewModel.CanUndoLogSetting);
        await viewModel.UndoLastLogSettingAsync();

        Assert.False(viewModel.LogSaveToFileEnabled);
        Assert.False(viewModel.CanUndoLogSetting);
        var json = await File.ReadAllTextAsync(
            SettingsPathProvider.GetSettingsPath(settings.ProjectRootPath));
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("logSaveToFileEnabled").GetBoolean());
    }

    [Fact]
    public async Task NonLogSettings_SaveImmediatelyAcrossSettingsSections()
    {
        var settings = new AppSettings { ProjectRootPath = Path.Combine(_root, "project") };
        var paths = settings.ManagedTools["AxTools"];
        var descriptor = ManagedToolCatalog.All.Single(item => item.Key == ManagedToolKey.AxTools);
        var tool = new ToolPageViewModel(
            descriptor,
            paths,
            new AxToolsAdapter(Path.Combine(_root, "Scripts")),
            isRunnerAvailable: false);
        var viewModel = CreateViewModel(settings, [tool]);

        viewModel.ThemeMode = ThemeMode.Dark;
        viewModel.ShowWorkspacePath = true;
        viewModel.Update.Source = UpdateSource.Gitee;
        viewModel.Environment.Settings.NodePath = @"D:\Environment\node.exe";
        tool.SourceRoot = @"D:\UnrealMap\AxTools-New";
        await viewModel.PendingSettingsSave;

        var json = await File.ReadAllTextAsync(
            SettingsPathProvider.GetSettingsPath(settings.ProjectRootPath));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("dark", root.GetProperty("themeMode").GetString());
        Assert.True(root.GetProperty("showWorkspacePath").GetBoolean());
        Assert.Equal("gitee", root.GetProperty("updateSource").GetString());
        Assert.Equal(@"D:\Environment\node.exe", root.GetProperty("toolchains").GetProperty("nodePath").GetString());
        Assert.Equal(@"D:\UnrealMap\AxTools-New", root.GetProperty("managedTools").GetProperty("AxTools").GetProperty("sourceRoot").GetString());
    }

    private SettingsViewModel CreateViewModel(
        AppSettings settings,
        IReadOnlyList<ToolPageViewModel>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ProjectRootPath))
        {
            settings.ProjectRootPath = Path.Combine(_root, "project");
        }

        return new SettingsViewModel(
            settings,
            new AppSettingsService(
                new AtomicJsonFileService(),
                Path.Combine(_root, "bootstrap.json"),
                () => settings.ProjectRootPath),
            tools ?? [],
            new RunnerDiagnosticsViewModel(null, "测试"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
