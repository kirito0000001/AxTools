using AxTools.Core.Adapters;
using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedToolAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsAdapterTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Catalog_ReturnsSevenAdaptersInManagedToolOrder()
    {
        var catalog = ManagedToolAdapterCatalog.Create(Path.Combine(_root, "Scripts"));

        Assert.Collection(
            catalog,
            adapter => Assert.Equal(ManagedToolKey.AxTools, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.FantasyTools, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.GalExcleTools, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.CrossingVoidZDTool, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.FantasyProjectPc, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.CrossingVoidPc, adapter.Key),
            adapter => Assert.Equal(ManagedToolKey.CrossingVoidAndroid, adapter.Key));
        Assert.All(catalog, adapter =>
            Assert.Equal(
                adapter.Actions.Count,
                adapter.Actions.Select(action => action.Action).Distinct().Count()));
    }

    [Fact]
    public void DevelopmentLaunchActions_AlwaysBuildCurrentSource()
    {
        var adapters = ManagedToolAdapterCatalog.Create(Path.Combine(_root, "Scripts"));

        foreach (var key in new[]
        {
            ManagedToolKey.AxTools,
            ManagedToolKey.FantasyTools,
            ManagedToolKey.GalExcleTools
            ,ManagedToolKey.CrossingVoidZDTool
        })
        {
            var adapter = adapters.Single(item => item.Key == key);
            Assert.DoesNotContain(
                adapter.Actions,
                item => item.Action == ManagedToolAction.RunDevelopment);
            Assert.Contains(
                adapter.Actions,
                item => item.Action == ManagedToolAction.BuildAndRun &&
                    item.DisplayName == "编译并启动");
        }

        foreach (var key in new[]
        {
            ManagedToolKey.FantasyProjectPc,
            ManagedToolKey.CrossingVoidPc
        })
        {
            var adapter = adapters.Single(item => item.Key == key);
            Assert.DoesNotContain(
                adapter.Actions,
                item => item.Action == ManagedToolAction.RunExistingDevelopment);
            Assert.Contains(
                adapter.Actions,
                item => item.Action == ManagedToolAction.RunDevelopment);
        }

        var android = adapters.Single(item => item.Key == ManagedToolKey.CrossingVoidAndroid);
        Assert.Contains(
            android.Actions,
            item => item.Action == ManagedToolAction.BuildAndroidDebug &&
                item.DisplayName == "构建 Debug APK");

        Assert.All(
            adapters,
            adapter => Assert.Contains(
                adapter.Actions,
                item => item.Action == ManagedToolAction.RunRelease));
    }

    [Fact]
    public void DesktopAdapters_ExposeForceBuildAndRunButAndroidDoesNot()
    {
        var adapters = ManagedToolAdapterCatalog.Create(Path.Combine(_root, "Scripts"));
        var desktopKeys = new[]
        {
            ManagedToolKey.AxTools,
            ManagedToolKey.FantasyTools,
            ManagedToolKey.GalExcleTools,
            ManagedToolKey.CrossingVoidZDTool,
            ManagedToolKey.FantasyProjectPc,
            ManagedToolKey.CrossingVoidPc
        };

        foreach (var key in desktopKeys)
        {
            var action = Assert.Single(
                adapters.Single(item => item.Key == key).Actions,
                item => item.Action == ManagedToolAction.ForceBuildAndRun);
            Assert.Equal("强制重新编译并启动", action.DisplayName);
            Assert.Equal(ManagedToolActionSection.Development, action.Section);
            Assert.True(action.IsHeavy);
        }

        Assert.DoesNotContain(
            adapters.Single(item => item.Key == ManagedToolKey.CrossingVoidAndroid).Actions,
            item => item.Action == ManagedToolAction.ForceBuildAndRun);
    }

    [Fact]
    public void CrossingVoidZdTool_ExposesFixedSafeActionsAndStructuredTask()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var adapter = new CrossingVoidZDToolAdapter(scriptsRoot);

        Assert.Equal(
        [
            ManagedToolAction.CheckEnvironment,
            ManagedToolAction.BuildAndRun,
            ManagedToolAction.ForceBuildAndRun,
            ManagedToolAction.Test,
            ManagedToolAction.CheckUnrealSyncEnvironment,
            ManagedToolAction.InspectArtifacts,
            ManagedToolAction.OpenDevelopmentOutput,
            ManagedToolAction.OpenWorkspace,
            ManagedToolAction.RunRelease,
            ManagedToolAction.OpenReleaseDirectory,
            ManagedToolAction.ValidateAndStagePackage,
            ManagedToolAction.ReplaceRelease
        ],
        adapter.Actions.Select(action => action.Action));
        Assert.True(adapter.Actions.Single(action =>
            action.Action == ManagedToolAction.ReplaceRelease).RequiresConfirmation);
        Assert.DoesNotContain(adapter.Actions, action => action.Action is
            ManagedToolAction.Upload or ManagedToolAction.Publish);

        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "CrossingVoidZDTool"),
            DevelopmentExecutable = Path.Combine(_root, "Release", "零境交错：ZD工具箱.exe"),
            ReleaseExecutable = Path.Combine(_root, "Formal", "零境交错：ZD工具箱.exe"),
            OutputRoot = Path.Combine(_root, "DabaoV")
        };
        var task = adapter.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.ValidateAndStagePackage),
            paths);

        Assert.EndsWith(
            Path.Combine("Scripts", "Adapters", "CrossingVoidZDTool", "Invoke-CrossingVoidZDToolAction.ps1"),
            task.ScriptPath);
        Assert.Contains(paths.SourceRoot, task.Arguments);
        Assert.Contains(paths.DevelopmentExecutable, task.Arguments);
        Assert.Contains(paths.ReleaseExecutable, task.Arguments);
        Assert.Contains(paths.OutputRoot, task.Arguments);
        Assert.True(task.IsHeavy);
    }

    [Fact]
    public void CrossingVoidAndroid_InjectsConfiguredJdkAndAndroidSdkOnlyIntoItsTask()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var toolchains = new ToolchainSettings
        {
            JavaHome = Path.Combine(_root, "jdk-23"),
            AndroidSdkRoot = Path.Combine(_root, "Android", "Sdk")
        };
        var adapter = new CrossingVoidAndroidAdapter(scriptsRoot, toolchains);

        var task = adapter.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.BuildAndroidDebug),
            new ManagedToolPaths { SourceRoot = Path.Combine(_root, "AndroidLauncher") });

        Assert.NotNull(task.EnvironmentVariables);
        Assert.Equal(toolchains.JavaHome, task.EnvironmentVariables!["JAVA_HOME"]);
        Assert.Equal(toolchains.AndroidSdkRoot, task.EnvironmentVariables["ANDROID_HOME"]);
        Assert.Equal(toolchains.AndroidSdkRoot, task.EnvironmentVariables["ANDROID_SDK_ROOT"]);
        Assert.Contains(Path.Combine(toolchains.JavaHome, "bin"), task.EnvironmentVariables["PATH"]);
        Assert.Contains(Path.Combine(toolchains.AndroidSdkRoot, "platform-tools"), task.EnvironmentVariables["PATH"]);
    }

    [Fact]
    public void CrossingVoidAndroid_PrefersDedicatedJdk21OverGeneralJdk()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var toolchains = new ToolchainSettings
        {
            JavaHome = Path.Combine(_root, "jdk-23"),
            AndroidJavaHome = Path.Combine(_root, "jdk-21"),
            AndroidSdkRoot = Path.Combine(_root, "Android", "Sdk")
        };
        var adapter = new CrossingVoidAndroidAdapter(scriptsRoot, toolchains);

        var task = adapter.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.BuildAndroidAndInstall),
            new ManagedToolPaths { SourceRoot = Path.Combine(_root, "AndroidLauncher") });

        Assert.Equal(toolchains.AndroidJavaHome, task.EnvironmentVariables!["JAVA_HOME"]);
        Assert.Contains(Path.Combine(toolchains.AndroidJavaHome, "bin"), task.EnvironmentVariables["PATH"]);
        Assert.DoesNotContain(Path.Combine(toolchains.JavaHome, "bin"), task.EnvironmentVariables["PATH"]);
    }

    [Fact]
    public void CrossingVoidAndroid_ExposesDevelopmentReleaseAndPublishActions()
    {
        var adapter = new CrossingVoidAndroidAdapter(
            Path.Combine(_root, "Scripts"),
            new ToolchainSettings());

        Assert.Equal(
            [
                ManagedToolAction.CheckEnvironment,
                ManagedToolAction.TestFrontend,
                ManagedToolAction.BuildFrontend,
                ManagedToolAction.BuildAndroidDebug,
                ManagedToolAction.BuildAndroidAndInstall,
                ManagedToolAction.BuildAndroidRelease,
                ManagedToolAction.ListAndroidDevices,
                ManagedToolAction.StopAndroidApp,
                ManagedToolAction.RunRelease,
                ManagedToolAction.PublishDryRun,
                ManagedToolAction.Publish,
                ManagedToolAction.BuildGameChunks,
                ManagedToolAction.UploadGameChunks,
                ManagedToolAction.PublishGamePackage
            ],
            adapter.Actions.Select(action => action.Action));
        Assert.True(adapter.Actions.Single(action => action.Action == ManagedToolAction.Publish).RequiresConfirmation);
    }

    [Fact]
    public void CrossingVoidLaunchers_ExposeVersionedPublishingWithChineseNames()
    {
        var adapters = ManagedToolAdapterCatalog.Create(Path.Combine(_root, "Scripts"));
        var pc = adapters.Single(adapter => adapter.Key == ManagedToolKey.CrossingVoidPc);
        var android = adapters.Single(adapter => adapter.Key == ManagedToolKey.CrossingVoidAndroid);

        var pcTask = pc.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.RunDevelopment),
            new ManagedToolPaths { SourceRoot = Path.Combine(_root, "CrossingVoidinitiator-PC") });
        Assert.StartsWith("零境启动器 PC", pcTask.Title);
        Assert.DoesNotContain(pc.Actions, action => action.Action == ManagedToolAction.SetVersion);
        Assert.All(
            pc.Actions.Where(action => action.Action is ManagedToolAction.PublishDryRun or ManagedToolAction.Publish),
            action => Assert.True(action.RequiresVersion));
        Assert.All(
            android.Actions.Where(action => action.Action is ManagedToolAction.PublishDryRun or ManagedToolAction.Publish),
            action => Assert.True(action.RequiresVersion));
    }

    [Fact]
    public void CrossingVoidLaunchers_ExposePlatformLockedGamePublishingActions()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var pc = new CrossingVoidPcAdapter(scriptsRoot);
        var android = new CrossingVoidAndroidAdapter(scriptsRoot, new ToolchainSettings());
        var gameActions = new[]
        {
            ManagedToolAction.BuildGameChunks,
            ManagedToolAction.UploadGameChunks,
            ManagedToolAction.PublishGamePackage
        };

        Assert.Equal(
            ["制作 PC 游戏分片", "上传 PC 游戏", "制作并上传 PC 游戏"],
            pc.Actions.Where(action => gameActions.Contains(action.Action)).Select(action => action.DisplayName));
        Assert.Equal(
            ["制作 Android 游戏分片", "上传 Android 游戏", "制作并上传 Android 游戏"],
            android.Actions.Where(action => gameActions.Contains(action.Action)).Select(action => action.DisplayName));
        Assert.All(
            pc.Actions.Concat(android.Actions).Where(action => gameActions.Contains(action.Action)),
            action => Assert.True(action.RequiresVersion));
        Assert.True(pc.Actions.Single(action => action.Action == ManagedToolAction.UploadGameChunks).RequiresConfirmation);
        Assert.True(android.Actions.Single(action => action.Action == ManagedToolAction.PublishGamePackage).RequiresConfirmation);

        var pcPaths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "CrossingVoidinitiator-PC"),
            GamePackageRoot = Path.Combine(_root, "WindowsPackage")
        };
        var androidPaths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "CrossingVoidinitiator-Android"),
            GamePackageRoot = Path.Combine(_root, "AndroidPackage")
        };
        var pcTask = pc.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.BuildGameChunks, "0.5.12", "stable", "PC 更新"),
            pcPaths);
        var androidTask = android.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.BuildGameChunks, "0.5.12", "stable", "Android 更新"),
            androidPaths);

        AssertArgumentPair(pcTask.Arguments, "-Platform", "Windows");
        AssertArgumentPair(pcTask.Arguments, "-GamePackageRoot", pcPaths.GamePackageRoot);
        AssertArgumentPair(androidTask.Arguments, "-Platform", "Android");
        AssertArgumentPair(androidTask.Arguments, "-GamePackageRoot", androidPaths.GamePackageRoot);
        AssertArgumentPair(pcTask.Arguments, "-ReleaseNotes", "PC 更新");
        AssertArgumentPair(androidTask.Arguments, "-ReleaseNotes", "Android 更新");
        Assert.DoesNotContain("Android", pcTask.Arguments);
        Assert.DoesNotContain("Windows", androidTask.Arguments);
    }

    [Fact]
    public void GalExcleTools_ExposesSevenStandardActionsAndStructuredTask()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var adapter = new GalExcleToolsAdapter(scriptsRoot);
        Assert.Equal(
            [
                ManagedToolAction.CheckEnvironment,
                ManagedToolAction.BuildAndRun,
                ManagedToolAction.ForceBuildAndRun,
                ManagedToolAction.Build,
                ManagedToolAction.SourceHealthCheck,
                ManagedToolAction.RunRelease,
                ManagedToolAction.PackageX64,
                ManagedToolAction.ValidatePackage
            ],
            adapter.Actions.Select(action => action.Action));
        Assert.Equal(5, adapter.Actions.Count(action => action.Section == ManagedToolActionSection.Development));
        Assert.Single(adapter.Actions, action => action.Section == ManagedToolActionSection.Release);
        Assert.Equal(2, adapter.Actions.Count(action => action.Section == ManagedToolActionSection.Publish));

        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "GalExcleTools"),
            DevelopmentExecutable = Path.Combine(_root, "Debug", "TFAC剧情箱-轮椅版.exe"),
            ReleaseExecutable = Path.Combine(_root, "Release", "TFAC剧情箱-轮椅版.exe"),
            OutputRoot = Path.Combine(_root, "DabaoV")
        };
        var task = adapter.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.PackageX64, string.Empty, "stable"),
            paths);

        Assert.EndsWith(
            Path.Combine("Scripts", "Adapters", "GalExcleTools", "Invoke-GalExcleToolsAction.ps1"),
            task.ScriptPath);
        Assert.Contains(paths.SourceRoot, task.Arguments);
        Assert.Contains(paths.OutputRoot, task.Arguments);
        Assert.True(task.IsHeavy);
    }

    [Theory]
    [InlineData(ManagedToolKey.AxTools, ManagedToolAction.Build)]
    [InlineData(ManagedToolKey.AxTools, ManagedToolAction.Test)]
    [InlineData(ManagedToolKey.FantasyTools, ManagedToolAction.PackageStable)]
    [InlineData(ManagedToolKey.FantasyTools, ManagedToolAction.PublishDryRun)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.CheckEnvironment)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.RunDevelopment)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.BuildFrontend)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.TestFrontend)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.TestRust)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.RunRelease)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.BuildLauncherPackage)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.ValidatePackage)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.UploadDryRun)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.Upload)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.PublishDryRun)]
    [InlineData(ManagedToolKey.FantasyProjectPc, ManagedToolAction.Publish)]
    [InlineData(ManagedToolKey.CrossingVoidPc, ManagedToolAction.TestFrontend)]
    [InlineData(ManagedToolKey.CrossingVoidPc, ManagedToolAction.TestRust)]
    [InlineData(ManagedToolKey.CrossingVoidPc, ManagedToolAction.BuildLauncherPackage)]
    public void Actions_ExposeExpectedCapabilities(
        ManagedToolKey key,
        ManagedToolAction expectedAction)
    {
        var adapter = ManagedToolAdapterCatalog
            .Create(Path.Combine(_root, "Scripts"))
            .Single(item => item.Key == key);

        Assert.Contains(adapter.Actions, action => action.Action == expectedAction);
    }

    [Fact]
    public void AxTools_ExposesPackagingValidationAndPublishingActions()
    {
        var adapter = new AxToolsAdapter(Path.Combine(_root, "Scripts"));

        Assert.Equal(
        [
            ManagedToolAction.CheckEnvironment,
            ManagedToolAction.BuildAndRun,
            ManagedToolAction.ForceBuildAndRun,
            ManagedToolAction.Build,
            ManagedToolAction.Test,
            ManagedToolAction.RunRelease,
            ManagedToolAction.PackageStable,
            ManagedToolAction.PackageBeta,
            ManagedToolAction.ValidatePackage,
            ManagedToolAction.UploadDryRun,
            ManagedToolAction.Upload,
            ManagedToolAction.PublishDryRun,
            ManagedToolAction.Publish
        ],
        adapter.Actions.Select(action => action.Action));
        Assert.True(adapter.Actions.Single(action =>
            action.Action == ManagedToolAction.Upload).RequiresConfirmation);
        Assert.True(adapter.Actions.Single(action =>
            action.Action == ManagedToolAction.Publish).RequiresConfirmation);
        Assert.False(adapter.Actions.Single(action =>
            action.Action == ManagedToolAction.UploadDryRun).RequiresConfirmation);
        Assert.False(adapter.Actions.Single(action =>
            action.Action == ManagedToolAction.PublishDryRun).RequiresConfirmation);
    }

    [Fact]
    public void AxToolsPublishingTask_PassesVersionChannelOutputAndReleaseNotes()
    {
        var adapter = new AxToolsAdapter(Path.Combine(_root, "Scripts"));
        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "AxTools"),
            DevelopmentExecutable = Path.Combine(_root, "Debug", "AxTools.exe"),
            ReleaseExecutable = Path.Combine(_root, "Release", "AxTools.exe"),
            OutputRoot = Path.Combine(_root, "Artifacts", "AxTools")
        };

        var task = adapter.CreateTask(
            new ManagedToolActionRequest(
                ManagedToolAction.PublishDryRun,
                "1.2.0-beta.1",
                "beta",
                "修复 AxTools 发布流程"),
            paths);

        AssertArgumentPair(task.Arguments, "-Version", "1.2.0-beta.1");
        AssertArgumentPair(task.Arguments, "-Channel", "beta");
        AssertArgumentPair(task.Arguments, "-OutputRoot", paths.OutputRoot);
        AssertArgumentPair(task.Arguments, "-ReleaseNotes", "修复 AxTools 发布流程");
        Assert.Contains("-DryRun", task.Arguments);
    }

    [Fact]
    public void RealPublishingRequiresConfirmationButDryRunDoesNot()
    {
        var publishingActions = ManagedToolAdapterCatalog
            .Create(Path.Combine(_root, "Scripts"))
            .SelectMany(adapter => adapter.Actions)
            .Where(action => action.Action is
                ManagedToolAction.Upload or
                ManagedToolAction.UploadDryRun or
                ManagedToolAction.Publish or
                ManagedToolAction.PublishDryRun)
            .ToArray();

        Assert.NotEmpty(publishingActions);
        Assert.All(
            publishingActions.Where(action => action.Action is
                ManagedToolAction.Upload or ManagedToolAction.Publish),
            action => Assert.True(action.RequiresConfirmation));
        Assert.All(
            publishingActions.Where(action => action.Action is
                ManagedToolAction.UploadDryRun or ManagedToolAction.PublishDryRun),
            action => Assert.False(action.RequiresConfirmation));
    }

    [Fact]
    public void CreateTask_UsesFixedWrapperAndStructuredArgumentBoundaries()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var adapter = new FantasyToolsAdapter(scriptsRoot);
        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "Fantasy Tools"),
            DevelopmentExecutable = Path.Combine(_root, "Dev Build", "FantasyTools.exe"),
            ReleaseExecutable = Path.Combine(_root, "Release Build", "FantasyTools.exe"),
            OutputRoot = Path.Combine(_root, "Release Assets")
        };

        var task = adapter.CreateTask(
            new ManagedToolActionRequest(
                ManagedToolAction.PublishDryRun,
                Version: "2.2.0 Beta",
                Channel: "beta"),
            paths);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                scriptsRoot,
                "Adapters",
                "FantasyTools",
                "Invoke-FantasyToolsAction.ps1")),
            task.ScriptPath);
        Assert.True(task.IsHeavy);
        Assert.Equal("-Action", task.Arguments[0]);
        Assert.Equal("PublishDryRun", task.Arguments[1]);
        Assert.Equal("-ProjectRoot", task.Arguments[2]);
        Assert.Equal(paths.SourceRoot, task.Arguments[3]);
        Assert.Contains("2.2.0 Beta", task.Arguments);
        Assert.Contains("beta", task.Arguments);
    }

    [Fact]
    public void Validate_RequiresProjectMarkers()
    {
        var projectRoot = Path.Combine(_root, "FantasyTools");
        Directory.CreateDirectory(projectRoot);
        var adapter = new FantasyToolsAdapter(Path.Combine(_root, "Scripts"));

        var invalid = adapter.Validate(new ManagedToolPaths { SourceRoot = projectRoot });

        Assert.False(invalid.IsValid);
        Assert.Contains("FantasyTools.csproj", invalid.Message);

        File.WriteAllText(Path.Combine(projectRoot, "FantasyTools.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Scripts"));
        File.WriteAllText(
            Path.Combine(projectRoot, "Scripts", "打包工具箱.ps1"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(projectRoot, "Scripts", "发布新版本.ps1"),
            string.Empty);

        Assert.True(adapter.Validate(new ManagedToolPaths { SourceRoot = projectRoot }).IsValid);
    }

    [Fact]
    public void CrossingAdapter_RejectsUnrealProject()
    {
        var adapter = new CrossingVoidPcAdapter(Path.Combine(_root, "Scripts"));
        var result = adapter.Validate(new ManagedToolPaths
        {
            SourceRoot = @"D:\UnrealMap\CrossingVoid"
        });

        Assert.False(result.IsValid);
        Assert.Contains("虚幻", result.Message);
    }

    private static void AssertArgumentPair(
        IReadOnlyList<string> arguments,
        string name,
        string value)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"缺少参数 {name}。");
        Assert.Equal(value, arguments[index + 1]);
    }

    [Fact]
    public void FantasyProjectPcAdapter_RequiresItsFiveLauncherMarkers()
    {
        var projectRoot = Path.Combine(_root, "FantasyProject-PC");
        Directory.CreateDirectory(projectRoot);
        var adapter = new FantasyProjectPcAdapter(Path.Combine(_root, "Scripts"));

        var invalid = adapter.Validate(new ManagedToolPaths { SourceRoot = projectRoot });

        Assert.False(invalid.IsValid);
        Assert.Equal(5, invalid.MissingPaths.Count);
        Assert.Contains("launcher.config.json", invalid.MissingPaths);

        foreach (var marker in new[]
        {
            "package.json",
            "launcher.config.json",
            Path.Combine("src-tauri", "Cargo.toml"),
            Path.Combine("Scripts", "Build-LauncherUpdaterPackage.ps1"),
            Path.Combine("Scripts", "Publish-LauncherGiteePackage.ps1")
        })
        {
            var path = Path.Combine(projectRoot, marker);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        Assert.True(adapter.Validate(new ManagedToolPaths { SourceRoot = projectRoot }).IsValid);
    }

    [Fact]
    public void FantasyProjectPcAdapter_CreateTaskUsesDedicatedWrapperAndPathArguments()
    {
        var scriptsRoot = Path.Combine(_root, "Scripts");
        var adapter = new FantasyProjectPcAdapter(scriptsRoot);
        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "FantasyProject-PC"),
            DevelopmentExecutable = Path.Combine(_root, "Debug", "fantasyproject-pc-launcher.exe"),
            ReleaseExecutable = Path.Combine(_root, "Release", "fantasyproject-pc-launcher.exe"),
            OutputRoot = Path.Combine(_root, "Launcher Output")
        };

        var task = adapter.CreateTask(
            new ManagedToolActionRequest(ManagedToolAction.PublishDryRun, "1.2.3", "beta"),
            paths);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                scriptsRoot,
                "Adapters",
                "FantasyProjectPc",
                "Invoke-FantasyProjectPcAction.ps1")),
            task.ScriptPath);
        Assert.Contains(paths.SourceRoot, task.Arguments);
        Assert.Contains(paths.DevelopmentExecutable, task.Arguments);
        Assert.Contains(paths.ReleaseExecutable, task.Arguments);
        Assert.Contains(paths.OutputRoot, task.Arguments);
        Assert.Contains("-DryRun", task.Arguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
