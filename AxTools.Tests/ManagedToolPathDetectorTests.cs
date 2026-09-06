using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedToolPathDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ManagedToolPathDetectorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DetectAndApply_PreservesWorkspaceOwnedOutputRootsEvenWhenTheyDoNotExist()
    {
        var axRoot = CreateAxToolsProject();
        CreateFantasyProject();
        CreateGalExcleToolsProject();
        CreateCrossingVoidZDToolProject();
        CreateFantasyProjectPcProject();
        CreateCrossingProject();
        CreateCrossingAndroidProject();
        var settings = new AppSettings();
        var workspaceRoot = Path.Combine(_root, "Workspace", "Ax工具箱项目");
        WorkspaceArtifactPathPolicy.Apply(workspaceRoot, settings.ManagedTools);
        var expected = settings.ManagedTools.ToDictionary(
            item => item.Key,
            item => item.Value.OutputRoot,
            StringComparer.Ordinal);

        new ManagedToolPathDetector(Path.Combine(_root, "DabaoV")).DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.All(expected, item => Assert.Equal(
            item.Value,
            settings.ManagedTools[item.Key].OutputRoot));
        Assert.False(Directory.Exists(Path.Combine(workspaceRoot, "Artifacts")));
    }

    [Fact]
    public void DetectAndApply_FindsAllSevenSiblingProjects()
    {
        var axRoot = CreateAxToolsProject();
        var fantasyRoot = CreateFantasyProject();
        var galExcleRoot = CreateGalExcleToolsProject();
        var zdRoot = CreateCrossingVoidZDToolProject();
        var fantasyProjectPcRoot = CreateFantasyProjectPcProject();
        var crossingRoot = CreateCrossingProject();
        var crossingAndroidRoot = CreateCrossingAndroidProject();
        var applicationDirectory = Path.Combine(
            axRoot,
            "bin",
            "x64",
            "Release",
            "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(applicationDirectory);
        var fantasyDevelopment = CreateFile(Path.Combine(
            fantasyRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "幻杀工具箱.exe"));
        CreateFile(Path.Combine(
            fantasyRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "AppX",
            "FantasyTools.exe"));
        var galExcleDevelopment = CreateFile(Path.Combine(
            galExcleRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "TFAC剧情箱-轮椅版.exe"));
        var galExcleOutput = Path.Combine(_root, "DabaoV");
        var galExcleRelease = CreateCompleteGalExclePackage(galExcleOutput, "2.10.0");
        var zdDevelopment = CreateFile(Path.Combine(
            zdRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "零境交错：ZD工具箱.exe"));
        var zdRelease = CreateCompleteZdPackage(galExcleOutput, "1.2.0");
        var crossingDevelopment = CreateFile(Path.Combine(
            crossingRoot,
            "src-tauri",
            "target",
            "debug",
            "tauri-vue-launcher.exe"));
        var fantasyProjectPcDevelopment = CreateFile(Path.Combine(
            fantasyProjectPcRoot,
            "src-tauri",
            "target",
            "debug",
            "fantasyproject-pc-launcher.exe"));
        var fantasyProjectPcRelease = CreateFile(Path.Combine(
            fantasyProjectPcRoot,
            "src-tauri",
            "target",
            "release",
            "fantasyproject-pc-launcher.exe"));
        var settings = new AppSettings();
        var workspaceRoot = Path.Combine(_root, "Ax工具箱项目");
        WorkspaceArtifactPathPolicy.Apply(workspaceRoot, settings.ManagedTools);

        var result = new ManagedToolPathDetector(galExcleOutput).DetectAndApply(
            applicationDirectory,
            settings.ManagedTools);

        Assert.True(result.Changed);
        Assert.Equal(axRoot, settings.ManagedTools["AxTools"].SourceRoot);
        Assert.Equal(fantasyRoot, settings.ManagedTools["FantasyTools"].SourceRoot);
        Assert.Equal(
            fantasyDevelopment,
            settings.ManagedTools["FantasyTools"].DevelopmentExecutable);
        Assert.Equal(galExcleRoot, settings.ManagedTools["GalExcleTools"].SourceRoot);
        Assert.Equal(galExcleDevelopment, settings.ManagedTools["GalExcleTools"].DevelopmentExecutable);
        Assert.Equal(galExcleRelease, settings.ManagedTools["GalExcleTools"].ReleaseExecutable);
        Assert.Equal(zdRoot, settings.ManagedTools["CrossingVoidZDTool"].SourceRoot);
        Assert.Equal(zdDevelopment, settings.ManagedTools["CrossingVoidZDTool"].DevelopmentExecutable);
        Assert.Equal(zdRelease, settings.ManagedTools["CrossingVoidZDTool"].ReleaseExecutable);
        Assert.Equal(
            fantasyProjectPcRoot,
            settings.ManagedTools["FantasyProject-PC"].SourceRoot);
        Assert.Equal(
            fantasyProjectPcDevelopment,
            settings.ManagedTools["FantasyProject-PC"].DevelopmentExecutable);
        Assert.Equal(
            fantasyProjectPcRelease,
            settings.ManagedTools["FantasyProject-PC"].ReleaseExecutable);
        Assert.Equal(
            crossingRoot,
            settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot);
        Assert.Equal(
            crossingDevelopment,
            settings.ManagedTools["CrossingVoidinitiator-PC"].DevelopmentExecutable);
        Assert.Equal(
            crossingAndroidRoot,
            settings.ManagedTools["CrossingVoidinitiator-Android"].SourceRoot);
        Assert.Equal(
            Path.Combine(
                crossingAndroidRoot,
                "android",
                "app",
                "build",
                "outputs",
                "apk",
                "debug",
                "app-debug.apk"),
            settings.ManagedTools["CrossingVoidinitiator-Android"].DevelopmentExecutable);
        Assert.All(settings.ManagedTools, item => Assert.Equal(
            Path.Combine(workspaceRoot, "Artifacts", item.Key),
            item.Value.OutputRoot));
        Assert.All(result.Statuses.Values, status => Assert.Contains("自动检测", status));
    }

    [Fact]
    public void DetectAndApply_PreservesCrossingVoidZdToolValidCustomPaths()
    {
        var axRoot = CreateAxToolsProject();
        var sourceRoot = CreateCrossingVoidZDToolProject();
        var customDevelopment = CreateFile(Path.Combine(_root, "CustomZD", "Development.exe"));
        var customRelease = CreateFile(Path.Combine(_root, "CustomZD", "Release.exe"));
        var customOutput = Path.Combine(_root, "CustomZDOutput");
        Directory.CreateDirectory(customOutput);
        var settings = new AppSettings();
        var paths = settings.ManagedTools["CrossingVoidZDTool"];
        paths.SourceRoot = sourceRoot;
        paths.DevelopmentExecutable = customDevelopment;
        paths.ReleaseExecutable = customRelease;
        paths.OutputRoot = customOutput;

        new ManagedToolPathDetector(Path.Combine(_root, "DabaoV")).DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.Equal(customDevelopment, paths.DevelopmentExecutable);
        Assert.Equal(customRelease, paths.ReleaseExecutable);
        Assert.Equal(customOutput, paths.OutputRoot);
    }

    [Fact]
    public void DetectAndApply_MigratesCrossingVoidZdToolCanonicalReleaseDevelopmentPathToDebug()
    {
        var axRoot = CreateAxToolsProject();
        var sourceRoot = CreateCrossingVoidZDToolProject();
        var legacyRelease = CreateFile(Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            "Release",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "零境交错：ZD工具箱.exe"));
        var settings = new AppSettings();
        var paths = settings.ManagedTools["CrossingVoidZDTool"];
        paths.SourceRoot = sourceRoot;
        paths.DevelopmentExecutable = legacyRelease;

        var result = new ManagedToolPathDetector(Path.Combine(_root, "DabaoV")).DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.True(result.Changed);
        Assert.Equal(
            Path.Combine(
                sourceRoot,
                "bin",
                "x64",
                "Debug",
                "net8.0-windows10.0.19041.0",
                "win-x64",
                "零境交错：ZD工具箱.exe"),
            paths.DevelopmentExecutable);
    }

    [Fact]
    public void DetectAndApply_UsesNewestCompleteGalExclePackageAndPreservesCustomPaths()
    {
        var axRoot = CreateAxToolsProject();
        var galExcleRoot = CreateGalExcleToolsProject();
        var outputRoot = Path.Combine(_root, "DabaoV");
        CreateCompleteGalExclePackage(outputRoot, "2.9.0");
        var newest = CreateCompleteGalExclePackage(outputRoot, "2.10.0");
        CreateFile(Path.Combine(
            outputRoot,
            "TFAC剧情箱-轮椅版V9.0.0",
            "TFAC剧情箱-轮椅版",
            "TFAC剧情箱-轮椅版.exe"));
        var customDevelopment = CreateFile(Path.Combine(_root, "Custom", "Debug.exe"));
        var customOutput = Path.Combine(_root, "CustomOutput");
        Directory.CreateDirectory(customOutput);
        var settings = new AppSettings();
        var paths = settings.ManagedTools["GalExcleTools"];
        paths.SourceRoot = galExcleRoot;
        paths.DevelopmentExecutable = customDevelopment;
        paths.OutputRoot = customOutput;

        new ManagedToolPathDetector(outputRoot).DetectAndApply(axRoot, settings.ManagedTools);

        Assert.Equal(customDevelopment, paths.DevelopmentExecutable);
        Assert.Equal(customOutput, paths.OutputRoot);
        Assert.Equal(newest, paths.ReleaseExecutable);
    }

    [Fact]
    public void DetectAndApply_PreservesFantasyProjectPcValidCustomPaths()
    {
        var axRoot = CreateAxToolsProject();
        var sourceRoot = CreateFantasyProjectPcProject();
        var developmentExecutable = CreateFile(Path.Combine(
            _root,
            "Custom FantasyProject-PC Debug",
            "launcher.exe"));
        var releaseExecutable = CreateFile(Path.Combine(
            _root,
            "Custom FantasyProject-PC Release",
            "launcher.exe"));
        var outputRoot = Path.Combine(_root, "Custom FantasyProject-PC Output");
        Directory.CreateDirectory(outputRoot);
        var settings = new AppSettings();
        var paths = settings.ManagedTools["FantasyProject-PC"];
        paths.SourceRoot = sourceRoot;
        paths.DevelopmentExecutable = developmentExecutable;
        paths.ReleaseExecutable = releaseExecutable;
        paths.OutputRoot = outputRoot;

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.Equal(developmentExecutable, paths.DevelopmentExecutable);
        Assert.Equal(releaseExecutable, paths.ReleaseExecutable);
        Assert.Equal(outputRoot, paths.OutputRoot);
        Assert.Contains("检查", result.Statuses[ManagedToolKey.FantasyProjectPc]);
    }

    [Fact]
    public void DetectAndApply_StoresFantasyProjectPcExpectedExecutablesBeforeFirstBuild()
    {
        var axRoot = CreateAxToolsProject();
        var sourceRoot = CreateFantasyProjectPcProject();
        var settings = new AppSettings();

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.True(result.Changed);
        Assert.Equal(
            Path.Combine(
                sourceRoot,
                "src-tauri",
                "target",
                "debug",
                "fantasyproject-pc-launcher.exe"),
            settings.ManagedTools["FantasyProject-PC"].DevelopmentExecutable);
        Assert.Equal(
            Path.Combine(
                sourceRoot,
                "src-tauri",
                "target",
                "release",
                "fantasyproject-pc-launcher.exe"),
            settings.ManagedTools["FantasyProject-PC"].ReleaseExecutable);
    }

    [Fact]
    public void DetectAndApply_RejectsIncompleteFantasyProjectPcMarkers()
    {
        var axRoot = CreateAxToolsProject();
        var incompleteRoot = Path.Combine(_root, "FantasyProject-PC");
        CreateFile(Path.Combine(incompleteRoot, "package.json"));
        CreateFile(Path.Combine(incompleteRoot, "src-tauri", "Cargo.toml"));
        var settings = new AppSettings();

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.Equal(string.Empty, settings.ManagedTools["FantasyProject-PC"].SourceRoot);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.FantasyProjectPc]);
    }

    [Fact]
    public void DetectAndApply_PreservesExistingValidCustomPaths()
    {
        var axRoot = CreateAxToolsProject();
        var fantasyRoot = CreateFantasyProject();
        var customExecutable = CreateFile(Path.Combine(
            _root,
            "Custom Fantasy",
            "FantasyTools.exe"));
        var customOutput = Path.Combine(_root, "Custom Output");
        Directory.CreateDirectory(customOutput);
        var settings = new AppSettings();
        var fantasy = settings.ManagedTools["FantasyTools"];
        fantasy.SourceRoot = fantasyRoot;
        fantasy.DevelopmentExecutable = customExecutable;
        fantasy.OutputRoot = customOutput;

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.Equal(customExecutable, fantasy.DevelopmentExecutable);
        Assert.Equal(customOutput, fantasy.OutputRoot);
        Assert.Contains("检查", result.Statuses[ManagedToolKey.FantasyTools]);
    }

    [Fact]
    public void DetectAndApply_RepairsFantasyLegacyAppXPathToCurrentUnpackagedOutput()
    {
        var axRoot = CreateAxToolsProject();
        var fantasyRoot = CreateFantasyProject();
        var legacyExecutable = CreateFile(Path.Combine(
            fantasyRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "AppX",
            "FantasyTools.exe"));
        var currentExecutable = CreateFile(Path.Combine(
            fantasyRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "幻杀工具箱.exe"));
        var settings = new AppSettings();
        var fantasy = settings.ManagedTools["FantasyTools"];
        fantasy.SourceRoot = fantasyRoot;
        fantasy.DevelopmentExecutable = legacyExecutable;

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.True(result.Changed);
        Assert.Equal(currentExecutable, fantasy.DevelopmentExecutable);
    }

    [Fact]
    public void DetectAndApply_MissingSiblingProjectDoesNotGuessPaths()
    {
        var axRoot = CreateAxToolsProject();
        var settings = new AppSettings();

        var result = new ManagedToolPathDetector().DetectAndApply(
            axRoot,
            settings.ManagedTools);

        Assert.Equal(string.Empty, settings.ManagedTools["FantasyTools"].SourceRoot);
        Assert.Equal(string.Empty, settings.ManagedTools["GalExcleTools"].SourceRoot);
        Assert.Equal(string.Empty, settings.ManagedTools["FantasyProject-PC"].SourceRoot);
        Assert.Equal(
            string.Empty,
            settings.ManagedTools["CrossingVoidinitiator-PC"].SourceRoot);
        Assert.Equal(
            string.Empty,
            settings.ManagedTools["CrossingVoidinitiator-Android"].SourceRoot);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.FantasyTools]);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.GalExcleTools]);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.FantasyProjectPc]);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.CrossingVoidPc]);
        Assert.Contains("未找到", result.Statuses[ManagedToolKey.CrossingVoidAndroid]);
    }

    private string CreateAxToolsProject()
    {
        var path = Path.Combine(_root, "AxTools");
        CreateFile(Path.Combine(path, "AxTools.csproj"));
        CreateFile(Path.Combine(path, "AxTools.sln"));
        return path;
    }

    private string CreateFantasyProject()
    {
        var path = Path.Combine(_root, "FantasyTools");
        CreateFile(Path.Combine(path, "FantasyTools.csproj"));
        CreateFile(Path.Combine(path, "Scripts", "打包工具箱.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "发布新版本.ps1"));
        return path;
    }

    private string CreateCrossingProject()
    {
        var path = Path.Combine(_root, "CrossingVoidinitiator-PC");
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, "src-tauri", "Cargo.toml"));
        CreateFile(Path.Combine(path, "Scripts", "Build-LauncherUpdaterPackage.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "Publish-LauncherGiteePackage.ps1"));
        return path;
    }

    private string CreateCrossingAndroidProject()
    {
        var path = Path.Combine(_root, "CrossingVoidinitiator-Android");
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, "android", "gradlew.bat"));
        CreateFile(Path.Combine(path, "android", "gradle", "wrapper", "gradle-wrapper.properties"));
        CreateFile(Path.Combine(path, "Scripts", "Publish-AndroidLauncher.ps1"));
        return path;
    }

    private string CreateGalExcleToolsProject()
    {
        var path = Path.Combine(_root, "GalExcleTools");
        CreateFile(Path.Combine(path, "GalExcleTools.csproj"));
        CreateFile(Path.Combine(path, "GalExcleTools.sln"));
        CreateFile(Path.Combine(path, "Scripts", "Test-SourceHealth.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "Package-App.ps1"));
        return path;
    }

    private string CreateCrossingVoidZDToolProject()
    {
        var path = Path.Combine(_root, "CrossingVoidZDTool");
        CreateFile(Path.Combine(path, "CrossingVoidZDTool.csproj"));
        CreateFile(Path.Combine(
            path,
            "Tests",
            "CrossingVoidZDTool.RegressionTests",
            "CrossingVoidZDTool.RegressionTests.csproj"));
        CreateFile(Path.Combine(path, "Pakout.ps1"));
        return path;
    }

    private static string CreateCompleteGalExclePackage(string outputRoot, string version)
    {
        var programRoot = Path.Combine(
            outputRoot,
            $"TFAC剧情箱-轮椅版V{version}",
            "TFAC剧情箱-轮椅版");
        var executable = CreateFile(Path.Combine(programRoot, "TFAC剧情箱-轮椅版.exe"));
        CreateFile(Path.Combine(programRoot, "TFAC剧情箱-轮椅版.pri"));
        CreateFile(Path.Combine(programRoot, "App.xbf"));
        CreateFile(Path.Combine(programRoot, "MainWindow.xbf"));
        return executable;
    }

    private static string CreateCompleteZdPackage(string outputRoot, string version)
    {
        var programRoot = Path.Combine(
            outputRoot,
            $"零境交错：ZD工具箱V{version}",
            "零境交错：ZD工具箱");
        var executable = CreateFile(Path.Combine(programRoot, "零境交错：ZD工具箱.exe"));
        CreateFile(Path.Combine(programRoot, "零境交错：ZD工具箱.pri"));
        CreateFile(Path.Combine(programRoot, "App.xbf"));
        CreateFile(Path.Combine(programRoot, "MainWindow.xbf"));
        return executable;
    }

    private string CreateFantasyProjectPcProject()
    {
        var path = Path.Combine(_root, "FantasyProject-PC");
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, "launcher.config.json"));
        CreateFile(Path.Combine(path, "src-tauri", "Cargo.toml"));
        CreateFile(Path.Combine(path, "Scripts", "Build-LauncherUpdaterPackage.ps1"));
        CreateFile(Path.Combine(path, "Scripts", "Publish-LauncherGiteePackage.ps1"));
        return path;
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
