using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class ApplicationViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_CreatesToolsInCatalogOrderAndFallsBackToAxToolsPage()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "Ax工具箱项目"),
            LastPageTag = "Unknown"
        };
        var service = CreateSettingsService(settings.ProjectRootPath);

        var viewModel = new ApplicationViewModel(settings, service);

        Assert.Collection(
            viewModel.Tools,
            tool => Assert.Equal("AxTools", tool.NavigationTag),
            tool => Assert.Equal("FantasyTools", tool.NavigationTag),
            tool => Assert.Equal("GalExcleTools", tool.NavigationTag),
            tool => Assert.Equal("CrossingVoidZDTool", tool.NavigationTag),
            tool => Assert.Equal("FantasyProjectPc", tool.NavigationTag),
            tool => Assert.Equal("CrossingVoidPc", tool.NavigationTag),
            tool => Assert.Equal("CrossingVoidAndroid", tool.NavigationTag));
        Assert.Equal("AxTools", viewModel.CurrentPageTag);
    }

    [Fact]
    public void CurrentPageTag_UpdatesPersistedSettingsState()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "Ax工具箱项目")
        };
        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath));

        viewModel.CurrentPageTag = "FantasyTools";

        Assert.Equal("FantasyTools", settings.LastPageTag);
    }

    [Fact]
    public void Constructor_MigratesRemovedCrossingVoidGamePageToPcLauncher()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "Ax工具箱项目"),
            LastPageTag = "CrossingVoidGame"
        };

        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath));

        Assert.Equal("CrossingVoidPc", viewModel.CurrentPageTag);
        Assert.Equal("CrossingVoidPc", settings.LastPageTag);
    }

    [Fact]
    public void LogService_UsesSettingsPolicyAndSingleServiceOwnedCollection()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "Ax工具箱项目"),
            LogEnabled = true,
            LogWarnings = false
        };
        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath));

        Assert.Same(viewModel.LogService.Entries, viewModel.Logs);
        viewModel.LogService.Write(LogKind.Info, "已保存设置。");
        viewModel.LogService.Write(LogKind.Warning, "这条提示已关闭。");
        settings.LogEnabled = false;
        viewModel.LogService.Write(LogKind.Error, "总开关关闭后不收集。");

        var entry = Assert.Single(viewModel.Logs);
        Assert.Equal("已保存设置。", entry.Message);
    }

    [Fact]
    public void Constructor_ShowsAutomaticPathStatusOnlyForAxTools()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "Ax工具箱项目")
        };
        const string detectionStatus = "AxTools 路径已自动检测并保存。";

        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath),
            axToolsPathDetectionStatus: detectionStatus);

        var axTools = viewModel.Tools.Single(tool =>
            tool.Descriptor.Key == ManagedToolKey.AxTools);
        Assert.True(axTools.HasPathDetectionStatus);
        Assert.Equal(detectionStatus, axTools.PathDetectionStatus);
        Assert.All(
            viewModel.Tools.Where(tool => tool.Descriptor.Key != ManagedToolKey.AxTools),
            tool =>
            {
                Assert.False(tool.HasPathDetectionStatus);
                Assert.Equal(string.Empty, tool.PathDetectionStatus);
            });
    }

    [Fact]
    public void Constructor_DisablesAllToolActionsWhenTaskRunnerIsUnavailable()
    {
        var settings = CreateSettingsWithValidManagedTools();

        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath));

        Assert.All(viewModel.Tools, tool =>
        {
            Assert.False(tool.IsExecutionAvailable);
            Assert.Contains("任务运行器不可用", tool.ExecutionAvailabilityText);
            Assert.All(tool.AllActions, action => Assert.False(action.IsEnabled));
        });
    }

    [Fact]
    public void Constructor_EnablesValidNonVersionActionsWhenTaskRunnerIsAvailable()
    {
        var settings = CreateSettingsWithValidManagedTools();
        var taskRunner = new AxTaskRunner(new NeverCalledProcessHost(), new AxTaskEventParser());

        var viewModel = new ApplicationViewModel(
            settings,
            CreateSettingsService(settings.ProjectRootPath),
            taskRunner: taskRunner);

        Assert.All(viewModel.Tools, tool =>
        {
            Assert.True(tool.IsExecutionAvailable);
            Assert.All(
                tool.AllActions.Where(action =>
                    !action.Definition.RequiresVersion &&
                    action.Action != ManagedToolAction.ReplaceRelease),
                action => Assert.True(action.IsEnabled));
            if (tool.Descriptor.Key == ManagedToolKey.CrossingVoidZDTool)
            {
                Assert.False(tool.AllActions.Single(action =>
                    action.Action == ManagedToolAction.ReplaceRelease).IsEnabled);
            }
        });
    }

    private AppSettings CreateSettingsWithValidManagedTools()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "AxToolsProject")
        };
        var axToolsRoot = Path.Combine(_root, "AxTools");
        CreateFile(Path.Combine(axToolsRoot, "AxTools.csproj"));
        CreateFile(Path.Combine(axToolsRoot, "AxTools.sln"));
        settings.ManagedTools["AxTools"].SourceRoot = axToolsRoot;

        var fantasyRoot = Path.Combine(_root, "FantasyTools");
        CreateFile(Path.Combine(fantasyRoot, "FantasyTools.csproj"));
        CreateFile(Path.Combine(fantasyRoot, "Scripts", "打包工具箱.ps1"));
        CreateFile(Path.Combine(fantasyRoot, "Scripts", "发布新版本.ps1"));
        settings.ManagedTools["FantasyTools"].SourceRoot = fantasyRoot;

        var galExcleRoot = Path.Combine(_root, "GalExcleTools");
        CreateFile(Path.Combine(galExcleRoot, "GalExcleTools.csproj"));
        CreateFile(Path.Combine(galExcleRoot, "GalExcleTools.sln"));
        CreateFile(Path.Combine(galExcleRoot, "Scripts", "Test-SourceHealth.ps1"));
        CreateFile(Path.Combine(galExcleRoot, "Scripts", "Package-App.ps1"));
        settings.ManagedTools["GalExcleTools"].SourceRoot = galExcleRoot;

        var zdRoot = Path.Combine(_root, "CrossingVoidZDTool");
        CreateFile(Path.Combine(zdRoot, "CrossingVoidZDTool.csproj"));
        CreateFile(Path.Combine(zdRoot, "Pakout.ps1"));
        CreateFile(Path.Combine(
            zdRoot,
            "Tests",
            "CrossingVoidZDTool.RegressionTests",
            "CrossingVoidZDTool.RegressionTests.csproj"));
        settings.ManagedTools["CrossingVoidZDTool"].SourceRoot = zdRoot;
        settings.ManagedTools["CrossingVoidZDTool"].OutputRoot = Path.Combine(_root, "DabaoV");

        var fantasyProjectPcRoot = Path.Combine(_root, "FantasyProject-PC");
        CreateFile(Path.Combine(fantasyProjectPcRoot, "package.json"));
        CreateFile(Path.Combine(fantasyProjectPcRoot, "launcher.config.json"));
        CreateFile(Path.Combine(fantasyProjectPcRoot, "src-tauri", "Cargo.toml"));
        CreateFile(Path.Combine(
            fantasyProjectPcRoot,
            "Scripts",
            "Build-LauncherUpdaterPackage.ps1"));
        CreateFile(Path.Combine(
            fantasyProjectPcRoot,
            "Scripts",
            "Publish-LauncherGiteePackage.ps1"));
        settings.ManagedTools["FantasyProject-PC"].SourceRoot = fantasyProjectPcRoot;

        var crossingRoot = Path.Combine(_root, "CrossingVoidinitiator-PC");
        CreateFile(Path.Combine(crossingRoot, "package.json"));
        CreateFile(Path.Combine(crossingRoot, "src-tauri", "Cargo.toml"));
        CreateFile(Path.Combine(crossingRoot, "Scripts", "Build-LauncherUpdaterPackage.ps1"));
        CreateFile(Path.Combine(crossingRoot, "Scripts", "Publish-LauncherGiteePackage.ps1"));
        settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot = crossingRoot;
        settings.ManagedTools["CrossingVoidinitiator-PC"].GamePackageRoot = Path.Combine(_root, "WindowsGamePackage");

        var crossingAndroidRoot = Path.Combine(_root, "CrossingVoidinitiator-Android");
        CreateFile(Path.Combine(crossingAndroidRoot, "package.json"));
        CreateFile(Path.Combine(crossingAndroidRoot, "android", "gradlew.bat"));
        CreateFile(Path.Combine(crossingAndroidRoot, "android", "app", "build.gradle"));
        CreateFile(Path.Combine(crossingAndroidRoot, "android", "gradle", "wrapper", "gradle-wrapper.properties"));
        CreateFile(Path.Combine(crossingAndroidRoot, "Scripts", "Publish-AndroidLauncher.ps1"));
        settings.ManagedTools["CrossingVoidinitiator-Android"].SourceRoot = crossingAndroidRoot;
        settings.ManagedTools["CrossingVoidinitiator-Android"].GamePackageRoot = Path.Combine(_root, "AndroidGamePackage");
        return settings;
    }

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private AppSettingsService CreateSettingsService(string projectRoot) =>
        new(
            new AtomicJsonFileService(),
            Path.Combine(_root, "AppData", "AxTools", "bootstrap.json"),
            () => projectRoot);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NeverCalledProcessHost : IAxProcessHost
    {
        public Task<AxProcessResult> RunAsync(
            AxTaskDefinition definition,
            Func<AxTaskOutputStream, string, ValueTask> onLine,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("测试不应启动进程。");
    }
}
