using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class AxToolsPathDetector
{
    private const string ProjectFileName = "AxTools.csproj";
    private const string TargetFramework = "net8.0-windows10.0.19041.0";

    public AxToolsPathDetectionResult DetectAndApply(
        string applicationBaseDirectory,
        ManagedToolPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sourceRoot = TryGetProjectRoot(paths.SourceRoot) ??
            FindProjectRoot(applicationBaseDirectory);
        if (sourceRoot is null)
        {
            return new AxToolsPathDetectionResult(
                SourceFound: false,
                Changed: false,
                "未找到 AxTools.csproj，请手动选择源码目录。");
        }

        var changed = false;
        if (IsDifferent(paths.SourceRoot, sourceRoot))
        {
            paths.SourceRoot = sourceRoot;
            changed = true;
        }

        var developmentExecutable = GetExecutablePath(sourceRoot, "Debug");
        if (ShouldRepairExecutable(
            paths.DevelopmentExecutable,
            developmentExecutable,
            GetLegacyExecutablePath(sourceRoot, "Debug")))
        {
            paths.DevelopmentExecutable = developmentExecutable;
            changed = true;
        }

        var releaseExecutable = GetExecutablePath(sourceRoot, "Release");
        if (ShouldRepairExecutable(
            paths.ReleaseExecutable,
            releaseExecutable,
            GetLegacyExecutablePath(sourceRoot, "Release")))
        {
            paths.ReleaseExecutable = releaseExecutable;
            changed = true;
        }

        return new AxToolsPathDetectionResult(
            SourceFound: true,
            Changed: changed,
            changed
                ? "AxTools 路径已自动检测并保存。"
                : "AxTools 路径已通过检查。");
    }

    private static string? FindProjectRoot(string applicationBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
        {
            return null;
        }

        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(applicationBaseDirectory));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, ProjectFileName)))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    private static string? TryGetProjectRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(Path.Combine(fullPath, ProjectFileName))
                ? fullPath
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string GetExecutablePath(string sourceRoot, string configuration) =>
        Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            TargetFramework,
            "win-x64",
            "AxTools.exe");

    private static string GetLegacyExecutablePath(
        string sourceRoot,
        string configuration) =>
        Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            TargetFramework,
            "AxTools.exe");

    private static bool ShouldRepairExecutable(
        string current,
        string detected,
        string legacy)
    {
        if (!IsDifferent(current, legacy))
        {
            return IsDifferent(current, detected);
        }

        return !IsExistingFile(current) && IsDifferent(current, detected);
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

    private static bool IsDifferent(string current, string detected) =>
        !string.Equals(current, detected, StringComparison.OrdinalIgnoreCase);
}
