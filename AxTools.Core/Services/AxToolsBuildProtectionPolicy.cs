using AxTools.Core.Models;

namespace AxTools.Core.Services;

public static class AxToolsBuildProtectionPolicy
{
    public static IReadOnlyList<string> GetConflictExecutablePaths(
        ManagedToolAction action,
        string sourceRoot,
        string developmentExecutable,
        string? currentProcessPath)
    {
        if (action is not (ManagedToolAction.Build or ManagedToolAction.BuildAndRun or ManagedToolAction.ForceBuildAndRun))
        {
            return Array.Empty<string>();
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryNormalizeAbsolutePath(developmentExecutable, out var normalizedDevelopment))
        {
            targets.Add(normalizedDevelopment);
        }

        foreach (var protectedPath in GetProtectedExecutablePaths(
            action,
            sourceRoot,
            currentProcessPath))
        {
            targets.Add(protectedPath);
        }

        return targets.ToArray();
    }

    public static IReadOnlyList<string> GetProtectedExecutablePaths(
        ManagedToolAction action,
        string sourceRoot,
        string? currentProcessPath)
    {
        if (action is not (ManagedToolAction.Build or ManagedToolAction.BuildAndRun or ManagedToolAction.ForceBuildAndRun) ||
            !TryNormalizeAbsolutePath(sourceRoot, out var normalizedSourceRoot) ||
            !TryNormalizeAbsolutePath(currentProcessPath, out var normalizedProcessPath) ||
            !string.Equals(
                Path.GetFileName(normalizedProcessPath),
                "AxTools.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var debugOutputRoot = Path.GetFullPath(Path.Combine(
            normalizedSourceRoot,
            "bin",
            "x64",
            "Debug"));
        return IsWithinDirectory(normalizedProcessPath, debugOutputRoot)
            ? new[] { normalizedProcessPath }
            : Array.Empty<string>();
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return path.StartsWith(
            normalizedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeAbsolutePath(
        string? path,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            normalized = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
