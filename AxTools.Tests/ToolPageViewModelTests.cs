using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class ToolPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ToolPageViewModelTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FantasyTools_GroupsActionsAndExposesPublishSection()
    {
        var paths = CreateFantasyProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Contains(viewModel.DevelopmentActions, item => item.Action == ManagedToolAction.Build);
        Assert.Contains(viewModel.ReleaseActions, item => item.Action == ManagedToolAction.RunRelease);
        Assert.Contains(viewModel.PublishActions, item => item.Action == ManagedToolAction.PublishDryRun);
        Assert.True(viewModel.HasPublishActions);
        Assert.True(viewModel.IsExecutionAvailable);
    }

    [Fact]
    public void GalExcleTools_GroupsEightActionsAndRequiresAllProjectMarkers()
    {
        var paths = CreateGalExcleProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.GalExcleTools),
            paths,
            new GalExcleToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Equal(5, viewModel.DevelopmentActions.Count);
        Assert.Single(viewModel.ReleaseActions);
        Assert.Equal(2, viewModel.PublishActions.Count);
        Assert.Equal(8, viewModel.AllActions.Count);
        Assert.True(viewModel.HasPublishActions);
        Assert.True(viewModel.IsExecutionAvailable);

        File.Delete(Path.Combine(paths.SourceRoot, "Scripts", "Package-App.ps1"));
        var invalidViewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.GalExcleTools),
            paths,
            new GalExcleToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.False(invalidViewModel.IsExecutionAvailable);
        Assert.Contains("Package-App.ps1", invalidViewModel.ExecutionAvailabilityText);
        Assert.All(invalidViewModel.AllActions, action => Assert.False(action.IsEnabled));
    }

    [Fact]
    public void CrossingVoidZdTool_EnablesReplaceOnlyWhenPendingPackageIsValid()
    {
        var paths = CreateCrossingVoidZdProject();
        paths.PublishVersion = "1.0.0";
        var pendingIsValid = false;
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidZDTool),
            paths,
            new CrossingVoidZDToolAdapter(Path.Combine(_root, "Scripts")),
            pendingPackageAvailable: () => pendingIsValid);
        var replace = viewModel.AllActions.Single(item => item.Action == ManagedToolAction.ReplaceRelease);

        Assert.False(replace.IsEnabled);
        Assert.All(
            viewModel.AllActions.Where(item => item.Action != ManagedToolAction.ReplaceRelease),
            item => Assert.True(item.IsEnabled));

        pendingIsValid = true;
        var stage = viewModel.AllActions.Single(item => item.Action == ManagedToolAction.ValidateAndStagePackage);
        viewModel.BeginAction(stage);
        viewModel.CompleteAction(SucceededResult());
        Assert.True(replace.IsEnabled);
    }

    [Fact]
    public void AxTools_ShowsPublishSectionAndStorageRemainsUnavailable()
    {
        var paths = CreateAxToolsProject();
        paths.PublishVersion = "1.1.0";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.AxTools),
            paths,
            new AxToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Equal(7, viewModel.PublishActions.Count);
        Assert.Contains(viewModel.PublishActions, item =>
            item.Action == ManagedToolAction.PackageStable);
        Assert.Contains(viewModel.PublishActions, item =>
            item.Action == ManagedToolAction.Publish);
        Assert.True(viewModel.HasPublishActions);
        Assert.False(viewModel.IsStorageAvailable);
        Assert.Contains("第四阶段", viewModel.StorageAvailabilityText);
    }

    [Fact]
    public void DownloadCommands_AreIntegratedIntoDevelopmentAndReleaseActionRows()
    {
        var paths = new ManagedToolPaths();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Equal(ManagedToolAction.DownloadSource, viewModel.DevelopmentPanelActions[0].Action);
        Assert.Contains(viewModel.DevelopmentPanelActions, action =>
            action.Action == ManagedToolAction.BuildAndRun);
        Assert.Equal(ManagedToolAction.DownloadRelease, viewModel.ReleasePanelActions[0].Action);
        Assert.Equal(ManagedToolAction.GetDownloadLink, viewModel.ReleasePanelActions[1].Action);
        Assert.Equal(
            "https://gitee.com/xiaojie578/FantasyTools/releases",
            viewModel.GiteeReleasesUrl);
        Assert.Contains(viewModel.ReleasePanelActions, action =>
            action.Action == ManagedToolAction.RunRelease);
        Assert.True(viewModel.DevelopmentPanelActions[0].IsEnabled);
        Assert.True(viewModel.ReleasePanelActions[0].IsEnabled);
    }

    [Fact]
    public void DevelopmentAndReleaseVersionText_ReadConfiguredExecutables()
    {
        var executable = typeof(ToolPageViewModelTests).Assembly.Location;
        var paths = CreateFantasyProject();
        paths.DevelopmentExecutable = executable;
        paths.ReleaseExecutable = executable;
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Contains("开发版版本：", viewModel.DevelopmentVersionText);
        Assert.DoesNotContain("未配置", viewModel.DevelopmentVersionText);
        Assert.Contains("正式版版本：", viewModel.ReleaseVersionText);
        Assert.DoesNotContain("未配置", viewModel.ReleaseVersionText);
    }

    [Fact]
    public void AxToolsPublishingRequest_UsesSharedVersionChannelAndReleaseDescription()
    {
        var paths = CreateAxToolsProject();
        paths.PublishVersion = "1.1.0";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.AxTools),
            paths,
            new AxToolsAdapter(Path.Combine(_root, "Scripts")))
        {
            PublishVersion = "1.2.0-beta.1",
            PublishChannel = "beta",
            LauncherReleaseDescription = "AxTools 页面发布说明"
        };

        var request = viewModel.CreateRequest(ManagedToolAction.PublishDryRun);

        Assert.Equal("1.2.0-beta.1", request.Version);
        Assert.Equal("beta", request.Channel);
        Assert.Equal("AxTools 页面发布说明", request.ReleaseNotes);
        Assert.Equal("beta", paths.PublishChannel);
        Assert.Equal("AxTools 页面发布说明", paths.PublishReleaseNotes);
    }

    [Fact]
    public void CreateRequest_RequiresVersionOnlyForVersionedActions()
    {
        var paths = CreateFantasyProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            viewModel.CreateRequest(ManagedToolAction.PackageStable));
        Assert.Contains("版本号", exception.Message);

        viewModel.PublishVersion = "2.2.0";
        viewModel.PublishChannel = "beta";
        var request = viewModel.CreateRequest(ManagedToolAction.PublishDryRun);

        Assert.Equal("2.2.0", request.Version);
        Assert.Equal("beta", request.Channel);
    }

    [Fact]
    public void PublishVersion_InitializesFromAndWritesManagedToolSettings()
    {
        var paths = CreateFantasyProject();
        paths.PublishVersion = "2.2.0";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Equal("2.2.0", viewModel.PublishVersion);
        Assert.Contains("2.2.0", viewModel.LauncherVersionStateText);

        viewModel.PublishVersion = "2.3.0";

        Assert.Equal("2.3.0", paths.PublishVersion);
        Assert.Contains("2.3.0", viewModel.LauncherVersionStateText);
    }

    [Fact]
    public void CreateTask_UsesMatchingAdapterAndCurrentPaths()
    {
        var paths = CreateFantasyProject();
        paths.OutputRoot = Path.Combine(_root, "Release Assets");
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));
        viewModel.PublishVersion = "2.2.0";
        viewModel.PublishChannel = "beta";
        var request = viewModel.CreateRequest(ManagedToolAction.PublishDryRun);

        var task = viewModel.CreateTask(request);
        Assert.Contains(paths.OutputRoot, task.Arguments);
        Assert.Contains("PublishDryRun", task.Arguments);
        Assert.Contains("2.2.0", task.Arguments);
        Assert.Contains("beta", task.Arguments);
    }

    [Fact]
    public void CrossingVoidPc_SeparatesLauncherAndGamePublishingActions()
    {
        var paths = CreateCrossingProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")));

        Assert.DoesNotContain(
            viewModel.LauncherPublishActions,
            item => item.Action is ManagedToolAction.BuildGameChunks or
                ManagedToolAction.UploadGameChunks or
                ManagedToolAction.PublishGamePackage);
        Assert.Equal(
            [
                ManagedToolAction.BuildGameChunks,
                ManagedToolAction.UploadGameChunks,
                ManagedToolAction.PublishGamePackage
            ],
            viewModel.GamePublishActions.Select(item => item.Action));
        Assert.True(viewModel.HasGamePackagePublishing);
    }

    [Fact]
    public void NonCrossingTool_HasNoGamePublishingSection()
    {
        var paths = CreateFantasyProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.FantasyTools),
            paths,
            new FantasyToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Empty(viewModel.GamePublishActions);
        Assert.False(viewModel.HasGamePackagePublishing);
        Assert.Equal(viewModel.PublishActions, viewModel.LauncherPublishActions);
    }

    [Fact]
    public void CrossingVoidPc_CreateRequestUsesIndependentLauncherAndGameState()
    {
        var paths = CreateCrossingProject();
        paths.GamePackageRoot = Path.Combine(_root, "WindowsGamePackage");
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")))
        {
            PublishVersion = "1.0.14",
            PublishChannel = "stable",
            GamePublishVersion = "0.5.12",
            GamePublishChannel = "beta",
            GamePublishReleaseNotes = "修复资源下载"
        };

        var launcher = viewModel.CreateRequest(ManagedToolAction.Publish);
        var game = viewModel.CreateRequest(ManagedToolAction.PublishGamePackage);

        Assert.Equal("1.0.14", launcher.Version);
        Assert.Equal("stable", launcher.Channel);
        Assert.Equal("0.5.12", game.Version);
        Assert.Equal("beta", game.Channel);
        Assert.Equal("修复资源下载", game.ReleaseNotes);
        Assert.Equal("修复资源下载", viewModel.GameReleaseDescription);
    }

    [Fact]
    public void CrossingVoidPc_RefreshesLauncherAndGameAvailabilityIndependently()
    {
        var paths = CreateCrossingProject();
        var state = new CrossingVoidGamePublishState(paths, new ManagedToolPaths());
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")),
            sharedGamePublishState: state)
        {
            PublishVersion = "1.0.14"
        };

        Assert.All(viewModel.LauncherPublishActions, action => Assert.True(action.IsEnabled));
        Assert.All(viewModel.GamePublishActions, action => Assert.False(action.IsEnabled));

        viewModel.GamePackageRoot = Path.Combine(_root, "WindowsGamePackage");
        Assert.All(viewModel.GamePublishActions, action => Assert.False(action.IsEnabled));

        viewModel.GamePublishVersion = "0.5.12";
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.BuildGameChunks).IsEnabled);
        Assert.False(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.UploadGameChunks).IsEnabled);
        Assert.False(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGamePackage).IsEnabled);

        viewModel.PublishVersion = string.Empty;
        Assert.All(
            viewModel.LauncherPublishActions.Where(action => action.Definition.RequiresVersion),
            action => Assert.False(action.IsEnabled));
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.BuildGameChunks).IsEnabled);
    }

    [Fact]
    public void CrossingVoidPcAndAndroid_ShareNextGameVersionAndChannel()
    {
        var pcPaths = CreateCrossingProject();
        var androidPaths = CreateCrossingAndroidProject();
        var state = new CrossingVoidGamePublishState(pcPaths, androidPaths);
        var pc = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            pcPaths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")),
            sharedGamePublishState: state);
        var android = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidAndroid),
            androidPaths,
            new CrossingVoidAndroidAdapter(
                Path.Combine(_root, "Scripts"),
                new ToolchainSettings()),
            sharedGamePublishState: state);

        pc.GamePublishVersion = "0.5.14";
        android.GamePublishChannel = "beta";
        pc.GamePublishReleaseNotes = "双端同步更新";

        Assert.Equal("0.5.14", android.GamePublishVersion);
        Assert.Equal("0.5.14", pcPaths.GamePublishVersion);
        Assert.Equal("0.5.14", androidPaths.GamePublishVersion);
        Assert.Equal("beta", pc.GamePublishChannel);
        Assert.Equal("beta", pcPaths.GamePublishChannel);
        Assert.Equal("beta", androidPaths.GamePublishChannel);
        Assert.Equal("双端同步更新", android.GamePublishReleaseNotes);
        Assert.Equal("双端同步更新", pcPaths.GamePublishReleaseNotes);
        Assert.Equal("双端同步更新", androidPaths.GamePublishReleaseNotes);
    }

    [Fact]
    public void CrossingVoidGame_MisalignedRemoteVersionsBlockUploadButAllowChunkBuild()
    {
        var paths = CreateCrossingProject();
        paths.GamePackageRoot = Path.Combine(_root, "WindowsGamePackage");
        var state = new CrossingVoidGamePublishState(paths, new ManagedToolPaths());
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")),
            sharedGamePublishState: state);
        viewModel.GamePublishVersion = "0.5.14";

        state.ApplyPublishedVersions(new CrossingVoidGamePublishedVersionResult(
            "V0.5.12",
            "V0.5.13",
            false,
            null,
            "线上双端版本不一致"));

        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.BuildGameChunks).IsEnabled);
        Assert.False(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.UploadGameChunks).IsEnabled);
        Assert.False(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGamePackage).IsEnabled);
        Assert.Contains("PC V0.5.12", viewModel.GamePublishedVersionText);
        Assert.Contains("Android V0.5.13", viewModel.GamePublishedVersionText);

        state.ApplyPublishedVersions(new CrossingVoidGamePublishedVersionResult(
            "V0.5.13",
            "V0.5.13",
            true,
            "V0.5.13",
            "双端线上版本一致"));

        Assert.All(viewModel.GamePublishActions, action => Assert.True(action.IsEnabled));
    }

    [Fact]
    public void BeginAndComplete_DisablesActionsAndStoresReadableResult()
    {
        var paths = CreateAxToolsProject();
        paths.PublishVersion = "1.1.0";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.AxTools),
            paths,
            new AxToolsAdapter(Path.Combine(_root, "Scripts")));
        var action = viewModel.DevelopmentActions.First();

        viewModel.BeginAction(action);

        Assert.True(viewModel.IsRunning);
        Assert.All(viewModel.AllActions, item => Assert.False(item.IsEnabled));

        var now = DateTimeOffset.Now;
        viewModel.CompleteAction(new AxTaskResult(
            "test",
            AxTaskStatus.Succeeded,
            0,
            now,
            now.AddSeconds(1),
            "任务已成功完成。",
            Array.Empty<string>(),
            Array.Empty<AxTaskEvent>()));

        Assert.False(viewModel.IsRunning);
        Assert.Contains("成功", viewModel.LastResultText);
        Assert.All(viewModel.AllActions, item => Assert.True(item.IsEnabled));
    }

    [Theory]
    [InlineData(AxTaskStatus.Succeeded, 0, "成功")]
    [InlineData(AxTaskStatus.Failed, 17, "失败")]
    [InlineData(AxTaskStatus.Stopped, 130, "已停止")]
    public void CompleteAction_ReenablesActionsForEveryTerminalStatus(
        AxTaskStatus status,
        int exitCode,
        string expectedText)
    {
        var paths = CreateAxToolsProject();
        paths.PublishVersion = "1.1.0";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.AxTools),
            paths,
            new AxToolsAdapter(Path.Combine(_root, "Scripts")));
        var action = viewModel.DevelopmentActions.First();
        var now = DateTimeOffset.Now;

        viewModel.BeginAction(action);
        viewModel.CompleteAction(new AxTaskResult(
            "test",
            status,
            exitCode,
            now,
            now.AddSeconds(1),
            "测试结果",
            Array.Empty<string>(),
            Array.Empty<AxTaskEvent>()));

        Assert.False(viewModel.IsRunning);
        Assert.Contains(expectedText, viewModel.LastResultText);
        Assert.All(viewModel.AllActions, item => Assert.True(item.IsEnabled));
    }

    [Fact]
    public void OutputRoot_RefreshesCrossingActionAvailabilityWhenPathBecomesForbidden()
    {
        var paths = CreateCrossingProject();
        paths.OutputRoot = Path.Combine(_root, "CrossingOutput");
        paths.GamePackageRoot = Path.Combine(_root, "CrossingGamePackage");
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")));
        viewModel.PublishVersion = "1.0.14";
        viewModel.GamePublishVersion = "0.5.12";

        Assert.True(viewModel.IsExecutionAvailable);
        Assert.All(viewModel.AllActions, item => Assert.True(item.IsEnabled));

        viewModel.OutputRoot = @"D:\UnrealMap\CrossingVoid";

        Assert.False(viewModel.IsExecutionAvailable);
        Assert.Contains("拒绝访问虚幻项目目录", viewModel.ExecutionAvailabilityText);
        Assert.All(viewModel.AllActions, item => Assert.False(item.IsEnabled));

        viewModel.OutputRoot = Path.Combine(_root, "SafeCrossingOutput");

        Assert.True(viewModel.IsExecutionAvailable);
        Assert.All(viewModel.AllActions, item => Assert.True(item.IsEnabled));
    }

    [Fact]
    public void ApplicationViewModel_CreatesEachToolWithItsMatchingAdapter()
    {
        var settings = new AppSettings
        {
            ProjectRootPath = Path.Combine(_root, "AxToolsProject")
        };
        var settingsService = new AppSettingsService(
            new AtomicJsonFileService(),
            Path.Combine(_root, "AppData", "AxTools", "bootstrap.json"),
            () => settings.ProjectRootPath);

        var viewModel = new ApplicationViewModel(
            settings,
            settingsService,
            scriptsRoot: Path.Combine(_root, "Scripts"));

        Assert.All(viewModel.Tools, tool => Assert.NotEmpty(tool.AllActions));
        Assert.All(
            viewModel.Tools,
            tool => Assert.All(
                tool.AllActions,
                action => Assert.Contains(
                    action.Definition,
                    ManagedToolAdapterCatalog
                        .Create(Path.Combine(_root, "Scripts"))
                        .Single(adapter => adapter.Key == tool.Descriptor.Key)
                        .Actions)));
    }

    private ManagedToolPaths CreateAxToolsProject()
    {
        var path = Path.Combine(_root, "AxTools");
        CreateFile(Path.Combine(path, "AxTools.csproj"));
        CreateFile(Path.Combine(path, "AxTools.sln"));
        return new ManagedToolPaths { SourceRoot = path };
    }

    private ManagedToolPaths CreateFantasyProject()
    {
        var path = Path.Combine(_root, "FantasyTools");
        CreateFile(Path.Combine(path, "FantasyTools.csproj"));
        CreateFile(Path.Combine(path, "Scripts", "打包工具箱.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "发布新版本.ps1"));
        return new ManagedToolPaths { SourceRoot = path };
    }

    private ManagedToolPaths CreateGalExcleProject()
    {
        var path = Path.Combine(_root, "GalExcleTools");
        CreateFile(Path.Combine(path, "GalExcleTools.csproj"));
        CreateFile(Path.Combine(path, "GalExcleTools.sln"));
        CreateFile(Path.Combine(path, "Scripts", "Test-SourceHealth.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "Package-App.ps1"));
        return new ManagedToolPaths { SourceRoot = path };
    }

    private ManagedToolPaths CreateCrossingProject()
    {
        var path = Path.Combine(_root, "CrossingVoidinitiator-PC");
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, "src-tauri", "Cargo.toml"));
        CreateFile(Path.Combine(path, "Scripts", "Build-LauncherUpdaterPackage.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "Publish-LauncherGiteePackage.ps1"));
        return new ManagedToolPaths { SourceRoot = path };
    }

    private ManagedToolPaths CreateCrossingAndroidProject()
    {
        var path = Path.Combine(_root, "CrossingVoidinitiator-Android");
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, "capacitor.config.ts"));
        CreateFile(Path.Combine(path, "android", "settings.gradle"));
        CreateFile(Path.Combine(path, "Scripts", "Publish-AndroidLauncher.ps1"));
        return new ManagedToolPaths { SourceRoot = path };
    }

    private ManagedToolPaths CreateCrossingVoidZdProject()
    {
        var path = Path.Combine(_root, "CrossingVoidZDTool");
        CreateFile(Path.Combine(path, "CrossingVoidZDTool.csproj"));
        CreateFile(Path.Combine(path, "Pakout.ps1"));
        CreateFile(Path.Combine(path, "Tests", "CrossingVoidZDTool.RegressionTests", "CrossingVoidZDTool.RegressionTests.csproj"));
        return new ManagedToolPaths
        {
            SourceRoot = path,
            OutputRoot = Path.Combine(_root, "DabaoV")
        };
    }

    private static AxTaskResult SucceededResult()
    {
        var now = DateTimeOffset.Now;
        return new AxTaskResult(
            "test",
            AxTaskStatus.Succeeded,
            0,
            now,
            now.AddSeconds(1),
            "成功",
            Array.Empty<string>(),
            Array.Empty<AxTaskEvent>());
    }

    private static ManagedToolDescriptor GetDescriptor(ManagedToolKey key) =>
        ManagedToolCatalog.All.Single(item => item.Key == key);

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    [Fact]
    public void DeveloperRelease_DefaultDescriptionIsVisibleAndTracksVersion()
    {
        var viewModel = new DeveloperReleaseViewModel(new AppSettings())
        {
            TargetVersion = "1.2.3"
        };

        Assert.Contains("## AxTools V1.2.3", viewModel.ReleaseDescription);
        viewModel.ReleaseDescription = "自定义正文";
        Assert.Equal("自定义正文", viewModel.ReleaseDescription);
    }

    [Fact]
    public void DeveloperRelease_UsesSameManagedAxToolsPublishSettingsAsToolPage()
    {
        var settings = new AppSettings();
        var paths = settings.ManagedTools["AxTools"];
        paths.PublishVersion = "1.3.0";
        paths.PublishChannel = "beta";
        paths.PublishReleaseNotes = "共享发布说明";
        var viewModel = new DeveloperReleaseViewModel(settings);

        Assert.Equal("1.3.0", viewModel.TargetVersion);
        Assert.Equal("beta", viewModel.Channel);
        Assert.Equal("共享发布说明", viewModel.ReleaseDescription);

        viewModel.TargetVersion = "1.3.1";
        viewModel.Channel = "stable";
        viewModel.ReleaseDescription = "修改后的说明";

        Assert.Equal("1.3.1", paths.PublishVersion);
        Assert.Equal("stable", paths.PublishChannel);
        Assert.Equal("修改后的说明", paths.PublishReleaseNotes);
    }

    [Fact]
    public void DeveloperRelease_RecognizesSharedFantasyToolsGiteeToken()
    {
        var original = Environment.GetEnvironmentVariable(
            "FANTASYTOOLS_GITEE_TOKEN",
            EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                "FANTASYTOOLS_GITEE_TOKEN",
                "test-fantasy-token",
                EnvironmentVariableTarget.Process);
            var viewModel = new DeveloperReleaseViewModel(new AppSettings());

            Assert.True(viewModel.HasGiteeToken);
            Assert.Contains("幻杀工具箱", viewModel.GiteeTokenStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "FANTASYTOOLS_GITEE_TOKEN",
                original,
                EnvironmentVariableTarget.Process);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
