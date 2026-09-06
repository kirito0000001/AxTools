using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class DevelopmentBuildStateCleanupService
{
    private readonly string _stateRoot;

    public DevelopmentBuildStateCleanupService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxTools",
            "BuildState"))
    {
    }

    public DevelopmentBuildStateCleanupService(string stateRoot)
    {
        _stateRoot = Path.GetFullPath(stateRoot);
    }

    public IReadOnlyList<StorageCleanupFailure> InvalidateForDeletedPaths(
        IEnumerable<string> deletedPaths)
    {
        if (!Directory.Exists(_stateRoot))
        {
            return Array.Empty<StorageCleanupFailure>();
        }

        var normalizedDeletedPaths = deletedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedDeletedPaths.Length == 0)
        {
            return Array.Empty<StorageCleanupFailure>();
        }

        var failures = new List<StorageCleanupFailure>();
        foreach (var statePath in Directory.EnumerateFiles(
            _stateRoot,
            "*.json",
            SearchOption.TopDirectoryOnly))
        {
            var shouldDelete = false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = document.RootElement;
                var projectRoot = root.GetProperty("projectRoot").GetString() ?? string.Empty;
                var executablePath = root
                    .GetProperty("executable")
                    .GetProperty("path")
                    .GetString() ?? string.Empty;
                shouldDelete = normalizedDeletedPaths.Any(deletedPath =>
                    IsSameOrInside(projectRoot, deletedPath) ||
                    IsSameOrInside(executablePath, deletedPath));
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                shouldDelete = true;
            }

            if (!shouldDelete)
            {
                continue;
            }

            try
            {
                File.Delete(statePath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new StorageCleanupFailure(statePath, exception.Message));
            }
        }

        return failures;
    }

    private static bool IsSameOrInside(string candidate, string directory)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var normalizedCandidate = Path.GetFullPath(candidate);
            var normalizedDirectory = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(normalizedDirectory, normalizedCandidate);
            return relative.Equals(".", StringComparison.Ordinal) ||
                (!Path.IsPathRooted(relative) &&
                 !relative.Equals("..", StringComparison.Ordinal) &&
                 !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
