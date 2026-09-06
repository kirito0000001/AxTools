using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class StorageCleanupService
{
    private readonly StorageCatalogService _catalogService;
    private readonly DevelopmentBuildStateCleanupService _buildStateCleanupService;

    public StorageCleanupService(StorageCatalogService catalogService)
        : this(catalogService, new DevelopmentBuildStateCleanupService())
    {
    }

    public StorageCleanupService(
        StorageCatalogService catalogService,
        DevelopmentBuildStateCleanupService buildStateCleanupService)
    {
        _catalogService = catalogService;
        _buildStateCleanupService = buildStateCleanupService;
    }

    public Task<StorageCleanupResult> CleanupAsync(
        AppSettings settings,
        IReadOnlyList<StorageScanItem> selectedItems,
        string currentExecutablePath,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var allowedPaths = _catalogService.CreateCandidates(settings)
            .ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
        var currentExecutable = Path.GetFullPath(currentExecutablePath);
        foreach (var item in selectedItems)
        {
            ValidateSelection(
                settings,
                item,
                allowedPaths,
                currentExecutable,
                cancellationToken);
        }

        return Task.Run(() => CleanupCore(
            selectedItems,
            progress,
            cancellationToken), cancellationToken);
    }

    private StorageCleanupResult CleanupCore(
        IReadOnlyList<StorageScanItem> selectedItems,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var deletedPaths = new List<string>();
        var failures = new List<StorageCleanupFailure>();
        long releasedBytes = 0;

        for (var index = 0; index < selectedItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = selectedItems[index];
            progress?.Report(new ProgressUpdate(
                "正在清理所选存储项...",
                selectedItems.Count == 0
                    ? 100
                    : (index + 1d) / selectedItems.Count * 100,
                item.Path));

            if (!Directory.Exists(item.Path))
            {
                deletedPaths.Add(item.Path);
                continue;
            }

            try
            {
                var bytesBefore = CalculateDirectorySize(item.Path, cancellationToken);
                Directory.Delete(item.Path, recursive: true);
                if (Directory.Exists(item.Path))
                {
                    throw new IOException($"清理后目录仍然存在：{item.Path}");
                }

                releasedBytes += bytesBefore;
                deletedPaths.Add(item.Path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new StorageCleanupFailure(item.Path, exception.Message));
            }
        }

        failures.AddRange(
            _buildStateCleanupService.InvalidateForDeletedPaths(deletedPaths));
        return new StorageCleanupResult(deletedPaths, failures, releasedBytes);
    }

    private static void ValidateSelection(
        AppSettings settings,
        StorageScanItem item,
        IReadOnlyDictionary<string, StorageCandidate> allowedPaths,
        string currentExecutable,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(item.Path);
        if (!allowedPaths.TryGetValue(path, out var candidate) ||
            candidate.Category != item.Category ||
            !string.Equals(
                candidate.ToolStableKey,
                item.ToolStableKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"清理目标不在固定白名单中：{path}");
        }

        if (!candidate.CanClean || !item.CanClean)
        {
            throw new InvalidOperationException($"该存储项只允许统计，不允许由 AxTools 清理：{path}");
        }

        if (ContainsPathSegment(path, ".git"))
        {
            throw new InvalidOperationException($"禁止清理 Git 数据：{path}");
        }

        var projectRoot = Path.GetFullPath(settings.ProjectRootPath);
        if (PathsEqual(path, projectRoot) || IsInside(projectRoot, path))
        {
            throw new InvalidOperationException($"禁止清理整体项目目录：{path}");
        }

        if (PathsEqual(currentExecutable, path) || IsInside(currentExecutable, path))
        {
            throw new InvalidOperationException($"禁止清理当前正在运行的程序目录：{path}");
        }

        if (Directory.Exists(path))
        {
            var snapshot = CalculateDirectoryStatistics(path, cancellationToken);
            var scannedTicks = item.LastModifiedAt?.UtcTicks;
            var currentTicks = snapshot.LastModifiedAt?.UtcTicks;
            if (snapshot.SizeBytes != item.SizeBytes ||
                snapshot.FileCount != item.FileCount ||
                scannedTicks != currentTicks)
            {
                throw new InvalidOperationException(
                    $"扫描结果已过期，目录内容发生变化，请重新扫描：{path}");
            }
        }
    }

    private static long CalculateDirectorySize(
        string path,
        CancellationToken cancellationToken) =>
        CalculateDirectoryStatistics(path, cancellationToken).SizeBytes;

    private static DirectoryStatistics CalculateDirectoryStatistics(
        string path,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var fileCount = 0;
        DateTimeOffset? lastModifiedAt = null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            total += info.Length;
            fileCount++;
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastModifiedAt is null || modified > lastModifiedAt)
            {
                lastModifiedAt = modified;
            }
        }

        return new DirectoryStatistics(total, fileCount, lastModifiedAt);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool ContainsPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => string.Equals(value, segment, StringComparison.OrdinalIgnoreCase));

    private sealed record DirectoryStatistics(
        long SizeBytes,
        int FileCount,
        DateTimeOffset? LastModifiedAt);
}
