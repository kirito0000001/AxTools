using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class CrossingVoidPackageInspector
{
    public const long ChunkSizeBytes = 500L * 1024L * 1024L;

    public CrossingVoidPackageInspection Inspect(
        string gameDirectory,
        string outputDirectory,
        string manualVersion)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            errors.Add("游戏包目录不存在。");
            return Empty(errors);
        }

        var gameRoot = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errors.Add("尚未设置输出目录。");
        }
        else
        {
            var outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (IsSameOrChild(outputRoot, gameRoot))
            {
                errors.Add("输出目录不能位于游戏包目录内部。");
            }
        }

        var includedCount = 0;
        var excludedCount = 0;
        long includedBytes = 0;
        long excludedBytes = 0;
        foreach (var path in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            var file = new FileInfo(path);
            var relativePath = Path.GetRelativePath(gameRoot, path);
            if (IsExcluded(relativePath))
            {
                excludedCount++;
                excludedBytes += file.Length;
            }
            else
            {
                includedCount++;
                includedBytes += file.Length;
            }
        }

        if (includedCount == 0)
        {
            errors.Add("游戏包目录中没有可打包文件。");
        }

        var resolvedVersion = ResolveVersion(gameRoot, manualVersion, errors);
        return new CrossingVoidPackageInspection(
            errors.Count == 0,
            includedCount,
            includedBytes,
            excludedCount,
            excludedBytes,
            EstimateChunkCount(includedBytes),
            resolvedVersion,
            errors);
    }

    public static int EstimateChunkCount(long bytes) =>
        bytes <= 0 ? 0 : checked((int)((bytes + ChunkSizeBytes - 1) / ChunkSizeBytes));

    public static bool IsExcluded(string relativePath)
    {
        if (string.Equals(Path.GetExtension(relativePath), ".pdb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (string.Equals(segments[index], "_download", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[index], ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (index + 1 < segments.Length &&
                string.Equals(segments[index], "Saved", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(segments[index + 1], "Logs", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segments[index + 1], "Crashes", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveVersion(
        string gameRoot,
        string manualVersion,
        ICollection<string> errors)
    {
        var versionPath = Path.Combine(gameRoot, "CrossingVoid.version.json");
        if (File.Exists(versionPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionPath));
                if (document.RootElement.TryGetProperty("version", out var versionNode) &&
                    versionNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(versionNode.GetString()))
                {
                    return versionNode.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                if (!string.IsNullOrWhiteSpace(manualVersion))
                {
                    return manualVersion.Trim();
                }

                errors.Add($"版本文件无效：{versionPath}");
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(manualVersion))
            {
                return manualVersion.Trim();
            }

            errors.Add($"版本文件无效：{versionPath}");
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(manualVersion))
        {
            return manualVersion.Trim();
        }

        errors.Add("游戏目录没有 CrossingVoid.version.json，请填写游戏版本。");
        return string.Empty;
    }

    private static bool IsSameOrChild(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static CrossingVoidPackageInspection Empty(IReadOnlyList<string> errors) =>
        new(false, 0, 0, 0, 0, 0, string.Empty, errors);
}
