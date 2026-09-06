using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class StorageScanService
{
    private readonly StorageCatalogService _catalogService;
    private readonly StorageInventorySnapshotService _snapshotService;

    public StorageScanService(
        StorageCatalogService catalogService,
        StorageInventorySnapshotService? snapshotService = null)
    {
        _catalogService = catalogService;
        _snapshotService = snapshotService ?? new StorageInventorySnapshotService(
            new AtomicJsonFileService());
    }

    public async Task<StorageScanResult> ScanAsync(
        AppSettings settings,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var scannedAt = DateTimeOffset.Now;
        var candidates = _catalogService.CreateCandidates(settings)
            .Where(candidate => Directory.Exists(candidate.Path))
            .ToArray();
        var items = new List<StorageScanItem>(candidates.Length);

        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            progress?.Report(new ProgressUpdate(
                "正在扫描存储空间...",
                candidates.Length == 0
                    ? 100
                    : (index + 1d) / candidates.Length * 100,
                candidate.Path));
            var statistics = await Task.Run(
                () => ScanDirectory(candidate.Path, cancellationToken),
                cancellationToken);
            items.Add(new StorageScanItem(
                candidate.ToolStableKey,
                candidate.DisplayName,
                candidate.Path,
                candidate.Category,
                candidate.Impact,
                statistics.SizeBytes,
                statistics.FileCount,
                statistics.LastModifiedAt,
                scannedAt,
                candidate.CanClean));
        }

        var annotated = await _snapshotService.ApplyAsync(
            settings.ProjectRootPath,
            items,
            cancellationToken);
        return new StorageScanResult(
            annotated,
            annotated.Sum(item => item.SizeBytes),
            scannedAt);
    }

    private static DirectoryStatistics ScanDirectory(
        string path,
        CancellationToken cancellationToken)
    {
        long sizeBytes = 0;
        var fileCount = 0;
        DateTimeOffset? lastModifiedAt = null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                sizeBytes += info.Length;
                fileCount++;
                var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (lastModifiedAt is null || modified > lastModifiedAt)
                {
                    lastModifiedAt = modified;
                }
            }
            catch (IOException)
            {
                // A changing cache may lose files during a read-only scan.
            }
            catch (UnauthorizedAccessException)
            {
                // Inaccessible entries are left untouched and contribute no size.
            }
        }

        return new DirectoryStatistics(sizeBytes, fileCount, lastModifiedAt);
    }

    private sealed record DirectoryStatistics(
        long SizeBytes,
        int FileCount,
        DateTimeOffset? LastModifiedAt);
}
