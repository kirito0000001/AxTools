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
    public void GalExcleTools_GroupsActionsAndRequiresAllProjectMarkers()
    {
        var paths = CreateGalExcleProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.GalExcleTools),
            paths,
            new GalExcleToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.Equal(5, viewModel.DevelopmentActions.Count);
        Assert.Single(viewModel.ReleaseActions);
        Assert.Equal(5, viewModel.PublishActions.Count);
        Assert.Equal(11, viewModel.AllActions.Count);
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

    /// <summary>
    /// 热更新发布链（`Docs\热更新-发布侧需求.md`）必须落到「打包与发布」面板的启动器区：
    /// 三个动作都归 In Publish 段、都由 LauncherPublishActions 承载（不是游戏包那一套），
    /// 且**不需要线上版本门禁**（线上现在是空的，走门禁会永远拦死）——所以它们不能被
    /// IsVersionGuardedPublishingAction 认领，也得能在没填版本号时保持可用。
    /// </summary>
    [Fact]
    public async Task CrossingVoidZdTool_ExposesHotUpdatePublishActionsInLauncherSection()
    {
        var paths = CreateCrossingVoidZdProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidZDTool),
            paths,
            new CrossingVoidZDToolAdapter(Path.Combine(_root, "Scripts")),
            pendingPackageAvailable: () => true);

        Assert.True(viewModel.HasPublishActions);
        Assert.True(viewModel.HasLauncherPublishActions);
        Assert.False(viewModel.HasGamePackagePublishing);

        var publishActionSet = viewModel.LauncherPublishActions.Select(item => item.Action).ToArray();
        Assert.Contains(ManagedToolAction.BuildUpdatePackage, publishActionSet);
        Assert.Contains(ManagedToolAction.PublishDryRun, publishActionSet);
        Assert.Contains(ManagedToolAction.Publish, publishActionSet);

        // 老的「临时打包并验证 / 替换正式版」不能被挤掉。
        Assert.Contains(ManagedToolAction.ValidateAndStagePackage, publishActionSet);
        Assert.Contains(ManagedToolAction.ReplaceRelease, publishActionSet);

        // 没填版本号也要能点：版本是 AX 设置里的值，由脚本按 -Version 收，不参与这里的门禁。
        var hotUpdateActions = viewModel.LauncherPublishActions
            .Where(item => item.Action is
                ManagedToolAction.BuildUpdatePackage or
                ManagedToolAction.PublishDryRun or
                ManagedToolAction.Publish)
            .ToArray();
        Assert.Equal(3, hotUpdateActions.Length);
        Assert.All(hotUpdateActions, item => Assert.True(item.IsEnabled, $"{item.DisplayName} 应当是可用状态"));

        // 真实发布要二次确认，演练与出包不需要。
        Assert.True(viewModel.LauncherPublishActions.Single(item => item.Action == ManagedToolAction.Publish).RequiresConfirmation);
        Assert.False(viewModel.LauncherPublishActions.Single(item => item.Action == ManagedToolAction.BuildUpdatePackage).RequiresConfirmation);

        // 版本门禁：ZD 只发 Gitee，而线上仓库目前一个 Release 都没有 —— 走线上门禁会永远拦死。
        // 所以这三个动作**不进** IsVersionGuardedPublishingAction；版本号由 AX 设置经 -Version 传给脚本。
        // 注意：这里不调 ValidatePublishingVersionAsync —— 它会去读线上版本（走网络），
        // 测试不该依赖网络也不该依赖限流状态。
        var gate = await viewModel.ValidatePublishingVersionAsync(ManagedToolAction.BuildUpdatePackage, CancellationToken.None);
        Assert.True(gate.IsAllowed);
        Assert.Contains("不需要发布版本门禁", gate.Message);
    }

    /// <summary>
    /// 剧情填表（TFAC剧情箱）也接了同一套热更新发布链：三个动作要落在「打包与发布」面板的启动器区，
    /// 而且老的「x64 正式打包 / 校验打包产物」不能被挤掉。
    /// </summary>
    [Fact]
    public void GalExcleTools_ExposesHotUpdatePublishActionsInLauncherSection()
    {
        var paths = CreateGalExcleProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.GalExcleTools),
            paths,
            new GalExcleToolsAdapter(Path.Combine(_root, "Scripts")));

        Assert.True(viewModel.HasPublishActions);
        Assert.True(viewModel.HasLauncherPublishActions);
        Assert.False(viewModel.HasGamePackagePublishing);

        var publishActionSet = viewModel.LauncherPublishActions.Select(item => item.Action).ToArray();
        Assert.Contains(ManagedToolAction.BuildUpdatePackage, publishActionSet);
        Assert.Contains(ManagedToolAction.PublishDryRun, publishActionSet);
        Assert.Contains(ManagedToolAction.Publish, publishActionSet);
        Assert.Contains(ManagedToolAction.PackageX64, publishActionSet);
        Assert.Contains(ManagedToolAction.ValidatePackage, publishActionSet);

        // 真实发布要二次确认，演练与出包不需要。
        Assert.True(viewModel.LauncherPublishActions.Single(item => item.Action == ManagedToolAction.Publish).RequiresConfirmation);
        Assert.False(viewModel.LauncherPublishActions.Single(item => item.Action == ManagedToolAction.BuildUpdatePackage).RequiresConfirmation);
        Assert.False(viewModel.LauncherPublishActions.Single(item => item.Action == ManagedToolAction.PublishDryRun).RequiresConfirmation);
    }

    /// <summary>
    /// 发布链的三个动作要派发到**另一个**适配器脚本：老的
    /// `Invoke-CrossingVoidZDToolAction.ps1` 的 ValidateSet 里没有这些值，转过去会直接哑掉。
    /// 同时确认版本号确实通过 -Version 传下去了（这是 ZD 出包唯一拿到版本的途径）。
    /// </summary>
    [Fact]
    public void CrossingVoidZdTool_RoutesPublishPipelineToItsOwnWrapperWithVersion()
    {
        var paths = CreateCrossingVoidZdProject();
        paths.PublishVersion = "1.0.2";
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidZDTool),
            paths,
            new CrossingVoidZDToolAdapter(Path.Combine(_root, "Scripts")),
            pendingPackageAvailable: () => true);

        var publishTask = viewModel.CreateTask(viewModel.CreateRequest(ManagedToolAction.Publish));
        Assert.EndsWith("Invoke-CrossingVoidZDToolPublish.ps1", publishTask.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.2", publishTask.Arguments);

        var legacyTask = viewModel.CreateTask(viewModel.CreateRequest(ManagedToolAction.CheckEnvironment));
        Assert.EndsWith("Invoke-CrossingVoidZDToolAction.ps1", legacyTask.ScriptPath, StringComparison.OrdinalIgnoreCase);
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
    public void CrossingVoidGame_OwnsGamePublishingSection()
    {
        var paths = CreateCrossingGamePackage();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidGame),
            paths,
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")));

        // 日常入口：「只检查」+「发布到服务器」+「发布到 GitHub」；维护折叠区已随 git 传输退场。
        Assert.Equal(
            [
                ManagedToolAction.EditGameReleaseNotes,
                ManagedToolAction.CheckGamePackage,
                ManagedToolAction.PublishGameVersion,
                ManagedToolAction.PublishGamePatch,
                ManagedToolAction.PublishGameVersionToGitHub
            ],
            viewModel.GamePublishActions.Select(item => item.Action));
        Assert.Empty(viewModel.GameMaintenanceActions);
        Assert.False(viewModel.HasGameMaintenanceActions);
        Assert.True(viewModel.HasGamePackagePublishing);
        Assert.Empty(viewModel.LauncherPublishActions);
    }

    [Fact]
    public void CrossingVoidPcLauncher_HasNoGamePublishingSection()
    {
        var paths = CreateCrossingProject();
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            paths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")));

        Assert.Empty(viewModel.GamePublishActions);
        Assert.Empty(viewModel.GameMaintenanceActions);
        Assert.False(viewModel.HasGamePackagePublishing);
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
        Assert.Equal(
            viewModel.PublishActions,
            viewModel.LauncherPublishActions.Where(
                item => item.Action != ManagedToolAction.EditLauncherReleaseNotes));
    }

    [Fact]
    public void CrossingVoidLauncherAndGameTool_UseIndependentPublishState()
    {
        var launcherPaths = CreateCrossingProject();
        var launcher = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            launcherPaths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")))
        {
            PublishVersion = "1.0.14",
            PublishChannel = "stable"
        };
        var launcherRequest = launcher.CreateRequest(ManagedToolAction.Publish);

        var game = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidGame),
            CreateCrossingGamePackage(),
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")))
        {
            GamePublishVersion = "0.5.13",
            GamePublishChannel = "beta",
            GamePublishReleaseNotes = "修复资源下载"
        };
        var gameRequest = game.CreateRequest(ManagedToolAction.PublishGameVersion);

        Assert.Equal("1.0.14", launcherRequest.Version);
        Assert.Equal("stable", launcherRequest.Channel);
        Assert.Equal("0.5.13", gameRequest.Version);
        Assert.Equal("beta", gameRequest.Channel);
        Assert.Equal("修复资源下载", gameRequest.ReleaseNotes);
        Assert.Equal("修复资源下载", game.GameReleaseDescription);
    }

    [Fact]
    public void CrossingVoidGame_EnablesActionsOnlyAfterPackageDirectoryAndVersion()
    {
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidGame),
            new ManagedToolPaths(),
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")));

        // 没选游戏包目录：连"检查环境"都不给点，因为发布内核拿不到产物。
        Assert.False(viewModel.IsExecutionAvailable);
        Assert.All(
            viewModel.GamePublishActions.Where(action => action.Action != ManagedToolAction.EditGameReleaseNotes),
            action => Assert.False(action.IsEnabled));
        Assert.All(viewModel.GameMaintenanceActions, action => Assert.False(action.IsEnabled));

        var packageRoot = Path.Combine(_root, "CrossingVoidGamePackage");
        Directory.CreateDirectory(packageRoot);
        viewModel.GamePackageRoot = packageRoot;
        Assert.True(viewModel.IsExecutionAvailable);
        Assert.All(
            viewModel.GamePublishActions.Where(action => action.Definition.RequiresVersion),
            action => Assert.False(action.IsEnabled));

        viewModel.GamePublishVersion = "0.5.13";
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.CheckGamePackage).IsEnabled);
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGameVersion).IsEnabled);
        Assert.Empty(viewModel.GameMaintenanceActions);
    }

    [Fact]
    public void CrossingVoidPcAndAndroid_ShareNextGameVersionAndReleaseNotes()
    {
        var pcPaths = CreateCrossingProject();
        var androidPaths = CreateCrossingAndroidProject();
        var state = new CrossingVoidGamePublishState(pcPaths, androidPaths);
        var pc = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            pcPaths,
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")),
            gamePublishState: state);
        var android = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidAndroid),
            androidPaths,
            new CrossingVoidAndroidAdapter(
                Path.Combine(_root, "Scripts"),
                new ToolchainSettings()),
            gamePublishState: state);

        pc.GamePublishVersion = "0.5.14";
        android.GamePublishChannel = "beta";
        pc.GamePublishReleaseNotes = "双端同步更新";

        Assert.Equal("0.5.14", android.GamePublishVersion);
        Assert.Equal("0.5.14", pcPaths.GamePublishVersion);
        Assert.Equal("0.5.14", androidPaths.GamePublishVersion);
        // 渠道暂时取消：就算写进 beta，也一律按 stable 发（等有测试服了再加回来）。
        Assert.Equal("stable", pc.GamePublishChannel);
        Assert.Equal("stable", pcPaths.GamePublishChannel);
        Assert.Equal("stable", androidPaths.GamePublishChannel);
        Assert.Equal("双端同步更新", android.GamePublishReleaseNotes);
        Assert.Equal("双端同步更新", pcPaths.GamePublishReleaseNotes);
        Assert.Equal("双端同步更新", androidPaths.GamePublishReleaseNotes);
    }

    [Fact]
    public void TfacFragmentTab_CanSwitchBetweenZeroAndFantasyPc()
    {
        var zeroPaths = new ManagedToolPaths
        {
            GamePublishVersion = "0.6.0",
            GamePackageRoot = @"F:\DaBaoV\Crossingvoid\Windows",
            GamePackageRootAndroid = @"F:\DaBaoV\Crossingvoid\Android"
        };
        var fantasyPaths = new ManagedToolPaths
        {
            GamePublishVersion = "0.1.0",
            GamePackageRoot = @"F:\DaBaoV\Fantasy\Windows",
            GamePackageRootAndroid = @"F:\DaBaoV\Fantasy\Android"
        };
        var options = new[]
        {
            new FragmentGameOption(GameProfileCatalog.CrossingVoid, zeroPaths),
            new FragmentGameOption(GameProfileCatalog.Fantasy, fantasyPaths)
        };
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidPc),
            new ManagedToolPaths(),
            new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts")),
            gamePublishPaths: zeroPaths,
            fragmentGameOptions: options);

        Assert.True(viewModel.HasFragmentGeneration);
        Assert.True(viewModel.HasFragmentGameSelection);
        Assert.Same(options[0], viewModel.SelectedFragmentGame);
        Assert.Equal("crossingvoid", viewModel.FragmentProductSegment);
        Assert.Equal("零境V0.6PC", viewModel.FragmentArchiveNamePrefix);
        Assert.Equal(@"F:\DaBaoV\Crossingvoid\Windows", viewModel.FragmentGameDirectory);
        Assert.Equal("零境碎片生成", viewModel.FragmentGenerationActionText);

        // 碎片源目录可以单独选，优先于游戏包发布目录；选择结果落到该游戏自己的 paths。
        var customSource = @"F:\DaBaoV\Crossingvoid\WindowsServer";
        viewModel.FragmentGameDirectory = customSource;
        Assert.Equal(customSource, viewModel.FragmentGameDirectory);
        Assert.Equal(customSource, zeroPaths.FragmentSourceDirectory);

        viewModel.SelectedFragmentGame = options[1];

        Assert.Same(options[1], viewModel.SelectedFragmentGame);
        Assert.Equal("fantasyproject", viewModel.FragmentProductSegment);
        Assert.Equal("幻杀V0.1PC", viewModel.FragmentArchiveNamePrefix);
        Assert.Equal(@"F:\DaBaoV\Fantasy\Windows", viewModel.FragmentGameDirectory);
        Assert.Equal("幻杀碎片生成", viewModel.FragmentGenerationActionText);
    }

    [Fact]
    public void CrossingVoidGame_GatesPublishByCurrentPlatformOnly()
    {
        var paths = CreateCrossingGamePackage();
        var state = new CrossingVoidGamePublishState(paths, new ManagedToolPaths());
        var viewModel = new ToolPageViewModel(
            GetDescriptor(ManagedToolKey.CrossingVoidGame),
            paths,
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")),
            gamePublishState: state);
        viewModel.GamePublishVersion = "0.5.14";

        // 安卓落后 PC 一个版本：PC 侧照发，不该被另一个平台拖住。
        state.ApplyPublishedVersions(new CrossingVoidGamePublishedVersionResult(
            "0.5.14",
            "0.5.13",
            false,
            null,
            "下载站版本：PC 0.5.14；Android 0.5.13。"));

        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.CheckGamePackage).IsEnabled);
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGameVersion).IsEnabled);
        Assert.Contains("PC 0.5.14", viewModel.GamePublishedVersionText);
        Assert.Contains("Android 0.5.13", viewModel.GamePublishedVersionText);

        // 目标版本低于这个平台的线上版本：按钮先灰掉（倒序发布）。
        viewModel.GamePublishVersion = "0.5.1";
        Assert.False(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGameVersion).IsEnabled);

        // 切到安卓：它自己的线上版本是 0.5.13，同一个目标版本照样能发（各平台独立）。
        var androidRoot = Path.Combine(_root, "CrossingVoidGamePackageAndroid");
        Directory.CreateDirectory(androidRoot);
        paths.GamePackageRootAndroid = androidRoot;
        viewModel.GamePublishPlatform = "Android";
        viewModel.GamePublishVersion = "0.5.14";
        Assert.True(viewModel.GamePublishActions.Single(
            action => action.Action == ManagedToolAction.PublishGameVersion).IsEnabled);
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

    /// <summary>零境游戏本体工具的"项目"就是打包产物目录，测试里给一个真实存在的空目录。</summary>
    private ManagedToolPaths CreateCrossingGamePackage()
    {
        var path = Path.Combine(_root, "CrossingVoidGamePackage");
        Directory.CreateDirectory(path);
        return new ManagedToolPaths { GamePackageRoot = path };
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
