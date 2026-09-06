using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ManagedToolPathDetector
{
    private readonly string _galExcleOutputRoot;

    public ManagedToolPathDetector(string? galExcleOutputRoot = null)
    {
        _galExcleOutputRoot = string.IsNullOrWhiteSpace(galExcleOutputRoot)
            ? @"D:\DabaoV"
            : Path.GetFullPath(galExcleOutputRoot);
    }

    public ManagedToolPathDetectionSummary DetectAndApply(
        string applicationBaseDirectory,
        IDictionary<string, ManagedToolPaths> managedTools)
    {
        ArgumentNullException.ThrowIfNull(managedTools);
        var statuses = new Dictionary<ManagedToolKey, string>();
        var changed = false;

        var axPaths = managedTools["AxTools"];
        var axResult = new AxToolsPathDetector().DetectAndApply(
            applicationBaseDirectory,
            axPaths);
        statuses[ManagedToolKey.AxTools] = axResult.Message;
        changed |= axResult.Changed;

        var siblingRoot = axResult.SourceFound
            ? Directory.GetParent(axPaths.SourceRoot)?.FullName
            : null;

        changed |= DetectFantasyTools(
            managedTools["FantasyTools"],
            siblingRoot,
            statuses);
        changed |= DetectGalExcleTools(
            managedTools["GalExcleTools"],
            siblingRoot,
            _galExcleOutputRoot,
            statuses);
        changed |= DetectCrossingVoidZDTool(
            managedTools["CrossingVoidZDTool"],
            siblingRoot,
            _galExcleOutputRoot,
            statuses);
        changed |= DetectFantasyProjectPc(
            managedTools["FantasyProject-PC"],
            siblingRoot,
            statuses);
        changed |= DetectCrossingVoidPc(
            managedTools["CrossingVoidinitiator-PC"],
            siblingRoot,
            statuses);
        changed |= DetectCrossingVoidAndroid(
            managedTools["CrossingVoidinitiator-Android"],
            siblingRoot,
            statuses);

        return new ManagedToolPathDetectionSummary(changed, statuses);
    }

    private static bool DetectCrossingVoidZDTool(
        ManagedToolPaths paths,
        string? siblingRoot,
        string outputRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var markers = new[]
        {
            "CrossingVoidZDTool.csproj",
            Path.Combine(
                "Tests",
                "CrossingVoidZDTool.RegressionTests",
                "CrossingVoidZDTool.RegressionTests.csproj"),
            "Pakout.ps1"
        };
        var sourceRoot = IsProjectRoot(paths.SourceRoot, markers)
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(siblingRoot, "CrossingVoidZDTool", markers);
        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.CrossingVoidZDTool] =
                "未找到 ZD空界幻境项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(
            paths.SourceRoot,
            sourceRoot,
            value => paths.SourceRoot = value);
        var expectedDevelopmentExecutable = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "零境交错：ZD工具箱.exe");
        var legacyReleaseDevelopmentExecutable = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            "Release",
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "零境交错：ZD工具箱.exe");
        changed |= RepairExpectedDevelopmentExecutable(
            paths.DevelopmentExecutable,
            expectedDevelopmentExecutable,
            legacyReleaseDevelopmentExecutable,
            value => paths.DevelopmentExecutable = value);
        changed |= RepairExpectedExecutable(
            paths.ReleaseExecutable,
            FindNewestCompleteZdPackage(outputRoot) ?? Path.Combine(
                outputRoot,
                "零境交错：ZD工具箱V1.0.0",
                "零境交错：ZD工具箱",
                "零境交错：ZD工具箱.exe"),
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.CrossingVoidZDTool] = changed
            ? "ZD空界幻境路径已自动检测并保存。"
            : "ZD空界幻境路径已通过检查。";
        return changed;
    }

    private static string? FindNewestCompleteZdPackage(string outputRoot)
    {
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        const string prefix = "零境交错：ZD工具箱V";
        return Directory.GetDirectories(outputRoot, $"{prefix}*")
            .Select(directory => new
            {
                Directory = directory,
                Version = TryParseSemanticVersion(Path.GetFileName(directory)[prefix.Length..])
            })
            .Where(item => item.Version is not null)
            .Select(item => new
            {
                item.Directory,
                Version = item.Version!,
                ProgramRoot = Path.Combine(item.Directory, "零境交错：ZD工具箱")
            })
            .Where(item =>
                File.Exists(Path.Combine(item.ProgramRoot, "零境交错：ZD工具箱.exe")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "零境交错：ZD工具箱.pri")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "App.xbf")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "MainWindow.xbf")))
            .OrderByDescending(item => item.Version)
            .Select(item => Path.Combine(item.ProgramRoot, "零境交错：ZD工具箱.exe"))
            .FirstOrDefault();
    }

    private static bool DetectCrossingVoidAndroid(
        ManagedToolPaths paths,
        string? siblingRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var markers = new[]
        {
            "package.json",
            Path.Combine("android", "gradlew.bat"),
            Path.Combine("android", "gradle", "wrapper", "gradle-wrapper.properties"),
            Path.Combine("Scripts", "Publish-AndroidLauncher.ps1")
        };
        var sourceRoot = IsProjectRoot(paths.SourceRoot, markers)
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(siblingRoot, "CrossingVoidinitiator-Android", markers);
        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.CrossingVoidAndroid] =
                "未找到零境启动器 Android 项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(paths.SourceRoot, sourceRoot, value => paths.SourceRoot = value);
        changed |= RepairExpectedExecutable(
            paths.DevelopmentExecutable,
            Path.Combine(sourceRoot, "android", "app", "build", "outputs", "apk", "debug", "app-debug.apk"),
            value => paths.DevelopmentExecutable = value);
        changed |= RepairExpectedExecutable(
            paths.ReleaseExecutable,
            Path.Combine(sourceRoot, "android", "app", "build", "outputs", "apk", "release", "app-release.apk"),
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.CrossingVoidAndroid] = changed
            ? "零境启动器 Android 路径已自动检测并保存。"
            : "零境启动器 Android 路径已通过检查。";
        return changed;
    }

    private static bool DetectGalExcleTools(
        ManagedToolPaths paths,
        string? siblingRoot,
        string outputRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var markers = new[]
        {
            "GalExcleTools.csproj",
            "GalExcleTools.sln",
            Path.Combine("Scripts", "Test-SourceHealth.ps1"),
            Path.Combine("Scripts", "Package-App.ps1")
        };
        var sourceRoot = IsProjectRoot(paths.SourceRoot, markers)
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(siblingRoot, "GalExcleTools", markers);
        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.GalExcleTools] =
                "未找到剧情工具箱项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(
            paths.SourceRoot,
            sourceRoot,
            value => paths.SourceRoot = value);
        changed |= RepairExpectedExecutable(
            paths.DevelopmentExecutable,
            GetGalExcleExecutable(sourceRoot, "Debug"),
            value => paths.DevelopmentExecutable = value);
        var packageExecutable = FindNewestCompleteGalExclePackage(outputRoot);
        changed |= RepairExpectedExecutable(
            paths.ReleaseExecutable,
            packageExecutable ?? GetGalExcleExecutable(sourceRoot, "Release"),
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.GalExcleTools] = changed
            ? "剧情工具箱路径已自动检测并保存。"
            : "剧情工具箱路径已通过检查。";
        return changed;
    }

    private static string GetGalExcleExecutable(string sourceRoot, string configuration) =>
        Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "TFAC剧情箱-轮椅版.exe");

    private static string? FindNewestCompleteGalExclePackage(string outputRoot)
    {
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        const string prefix = "TFAC剧情箱-轮椅版V";
        return Directory.GetDirectories(outputRoot, $"{prefix}*")
            .Select(directory => new
            {
                Directory = directory,
                Version = TryParseSemanticVersion(Path.GetFileName(directory)[prefix.Length..])
            })
            .Where(item => item.Version is not null)
            .Select(item => new
            {
                item.Directory,
                Version = item.Version!,
                ProgramRoot = Path.Combine(item.Directory, "TFAC剧情箱-轮椅版")
            })
            .Where(item =>
                File.Exists(Path.Combine(item.ProgramRoot, "TFAC剧情箱-轮椅版.exe")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "TFAC剧情箱-轮椅版.pri")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "App.xbf")) &&
                File.Exists(Path.Combine(item.ProgramRoot, "MainWindow.xbf")))
            .OrderByDescending(item => item.Version)
            .Select(item => Path.Combine(item.ProgramRoot, "TFAC剧情箱-轮椅版.exe"))
            .FirstOrDefault();
    }

    private static SemanticVersion? TryParseSemanticVersion(string value)
    {
        try
        {
            return SemanticVersion.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool DetectFantasyTools(
        ManagedToolPaths paths,
        string? siblingRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var sourceRoot = IsProjectRoot(
            paths.SourceRoot,
            "FantasyTools.csproj",
            Path.Combine("Scripts", "打包工具箱.ps1"),
            Path.Combine("Scripts", "发布新版本.ps1"))
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(
                siblingRoot,
                "FantasyTools",
                "FantasyTools.csproj",
                Path.Combine("Scripts", "打包工具箱.ps1"),
                Path.Combine("Scripts", "发布新版本.ps1"));

        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.FantasyTools] =
                "未找到 FantasyTools 项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(paths.SourceRoot, sourceRoot, value => paths.SourceRoot = value);
        var developmentCandidates = GetFantasyExecutableCandidates(sourceRoot, "Debug");
        changed |= RepairExpectedDevelopmentExecutable(
            paths.DevelopmentExecutable,
            developmentCandidates[0],
            developmentCandidates[2],
            value => paths.DevelopmentExecutable = value);
        changed |= RepairExecutable(
            paths.ReleaseExecutable,
            GetFantasyExecutableCandidates(sourceRoot, "Release"),
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.FantasyTools] = changed
            ? "FantasyTools 路径已自动检测并保存。"
            : "FantasyTools 路径已通过检查。";
        return changed;
    }

    private static bool DetectFantasyProjectPc(
        ManagedToolPaths paths,
        string? siblingRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var markers = new[]
        {
            "package.json",
            "launcher.config.json",
            Path.Combine("src-tauri", "Cargo.toml"),
            Path.Combine("Scripts", "Build-LauncherUpdaterPackage.ps1"),
            Path.Combine("Scripts", "Publish-LauncherGiteePackage.ps1")
        };
        var sourceRoot = IsProjectRoot(paths.SourceRoot, markers)
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(siblingRoot, "FantasyProject-PC", markers);

        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.FantasyProjectPc] =
                "未找到 FantasyProject-PC 项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(
            paths.SourceRoot,
            sourceRoot,
            value => paths.SourceRoot = value);
        changed |= RepairExpectedExecutable(
            paths.DevelopmentExecutable,
            Path.Combine(
                sourceRoot,
                "src-tauri",
                "target",
                "debug",
                "fantasyproject-pc-launcher.exe"),
            value => paths.DevelopmentExecutable = value);
        changed |= RepairExpectedExecutable(
            paths.ReleaseExecutable,
            Path.Combine(
                sourceRoot,
                "src-tauri",
                "target",
                "release",
                "fantasyproject-pc-launcher.exe"),
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.FantasyProjectPc] = changed
            ? "FantasyProject-PC 路径已自动检测并保存。"
            : "FantasyProject-PC 路径已通过检查。";
        return changed;
    }

    private static bool DetectCrossingVoidPc(
        ManagedToolPaths paths,
        string? siblingRoot,
        IDictionary<ManagedToolKey, string> statuses)
    {
        var sourceRoot = IsProjectRoot(
            paths.SourceRoot,
            "package.json",
            Path.Combine("src-tauri", "Cargo.toml"),
            Path.Combine("Scripts", "Build-LauncherUpdaterPackage.ps1"))
            ? Path.GetFullPath(paths.SourceRoot)
            : FindSiblingProject(
                siblingRoot,
                "CrossingVoidinitiator-PC",
                "package.json",
                Path.Combine("src-tauri", "Cargo.toml"),
                Path.Combine("Scripts", "Build-LauncherUpdaterPackage.ps1"));

        if (sourceRoot is null)
        {
            statuses[ManagedToolKey.CrossingVoidPc] =
                "未找到 CrossingVoidinitiator-PC 项目特征，请手动选择源码目录。";
            return false;
        }

        var changed = ReplaceIfDifferent(paths.SourceRoot, sourceRoot, value => paths.SourceRoot = value);
        changed |= RepairExecutable(
            paths.DevelopmentExecutable,
            [Path.Combine(sourceRoot, "src-tauri", "target", "debug", "tauri-vue-launcher.exe")],
            value => paths.DevelopmentExecutable = value);
        changed |= RepairExecutable(
            paths.ReleaseExecutable,
            [Path.Combine(sourceRoot, "src-tauri", "target", "release", "tauri-vue-launcher.exe")],
            value => paths.ReleaseExecutable = value);
        statuses[ManagedToolKey.CrossingVoidPc] = changed
            ? "CrossingVoidinitiator-PC 路径已自动检测并保存。"
            : "CrossingVoidinitiator-PC 路径已通过检查。";
        return changed;
    }

    private static IReadOnlyList<string> GetFantasyExecutableCandidates(
        string sourceRoot,
        string configuration)
    {
        var outputRoot = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            "net8.0-windows10.0.19041.0");
        return
        [
            Path.Combine(outputRoot, "win-x64", "幻杀工具箱.exe"),
            Path.Combine(outputRoot, "win-x64", "FantasyTools.exe"),
            Path.Combine(outputRoot, "win-x64", "AppX", "FantasyTools.exe"),
            Path.Combine(outputRoot, "FantasyTools.exe"),
            Path.Combine(outputRoot, "幻杀工具箱.exe")
        ];
    }

    private static string? FindSiblingProject(
        string? siblingRoot,
        string directoryName,
        params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(siblingRoot))
        {
            return null;
        }

        var candidate = Path.Combine(siblingRoot, directoryName);
        return IsProjectRoot(candidate, markers)
            ? Path.GetFullPath(candidate)
            : null;
    }

    private static bool IsProjectRoot(string path, params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return markers.All(marker => File.Exists(Path.Combine(fullPath, marker)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool RepairExecutable(
        string current,
        IReadOnlyList<string> candidates,
        Action<string> assign)
    {
        if (IsExistingFile(current))
        {
            return false;
        }

        var detected = candidates.FirstOrDefault(File.Exists);
        return detected is not null && ReplaceIfDifferent(current, detected, assign);
    }

    private static bool RepairExpectedExecutable(
        string current,
        string expected,
        Action<string> assign) =>
        IsExistingFile(current)
            ? false
            : ReplaceIfDifferent(current, expected, assign);

    private static bool RepairExpectedDevelopmentExecutable(
        string current,
        string expected,
        string legacyExpected,
        Action<string> assign)
    {
        if (IsExistingFile(current) && !PathsEqual(current, legacyExpected))
        {
            return false;
        }

        return ReplaceIfDifferent(current, expected, assign);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(
                       Path.GetFullPath(left),
                       Path.GetFullPath(right),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ReplaceIfDifferent(
        string current,
        string detected,
        Action<string> assign)
    {
        if (string.Equals(current, detected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        assign(detected);
        return true;
    }

    private static bool IsExistingFile(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsExistingDirectory(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
