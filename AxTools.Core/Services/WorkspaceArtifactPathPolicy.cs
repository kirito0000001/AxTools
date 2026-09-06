using AxTools.Core.Catalog;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public static class WorkspaceArtifactPathPolicy
{
    public static string GetToolOutputRoot(string projectRoot, string stableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);

        if (!ManagedToolCatalog.All.Any(descriptor =>
                string.Equals(descriptor.StableKey, stableKey, StringComparison.Ordinal)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableKey),
                stableKey,
                "未知的固定工具名称。");
        }

        return Path.Combine(
            Path.GetFullPath(projectRoot),
            "Artifacts",
            stableKey);
    }

    public static bool Apply(
        string projectRoot,
        IDictionary<string, ManagedToolPaths> managedTools)
    {
        ArgumentNullException.ThrowIfNull(managedTools);
        var changed = false;

        foreach (var descriptor in ManagedToolCatalog.All)
        {
            if (!managedTools.TryGetValue(descriptor.StableKey, out var paths))
            {
                paths = new ManagedToolPaths();
                managedTools.Add(descriptor.StableKey, paths);
                changed = true;
            }

            var expected = GetToolOutputRoot(projectRoot, descriptor.StableKey);
            if (!PathsEqual(paths.OutputRoot, expected))
            {
                paths.OutputRoot = expected;
                changed = true;
            }
        }

        return changed;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
