namespace AxTools.Core.Models;

public enum StorageCategory
{
    SafeTemporary,
    RebuildableCache,
    HighCostCache,
    ReleaseArtifact
}

public sealed record SharedCacheRoots(
    string UserProfile,
    string LocalAppData,
    string Temp);

public sealed record UnrealProjectRoots(
    string CrossingVoid,
    string FantasyProject);

public sealed record StorageCandidate(
    string ToolStableKey,
    string DisplayName,
    string Path,
    StorageCategory Category,
    string Impact,
    bool CanClean = true);

public sealed record StorageScanItem(
    string ToolStableKey,
    string DisplayName,
    string Path,
    StorageCategory Category,
    string Impact,
    long SizeBytes,
    int FileCount,
    DateTimeOffset? LastModifiedAt,
    DateTimeOffset ScannedAt,
    bool CanClean = true,
    long PreviousSizeBytes = 0,
    long GrowthBytes = 0);

public sealed record StorageScanResult(
    IReadOnlyList<StorageScanItem> Items,
    long TotalBytes,
    DateTimeOffset ScannedAt);

public sealed record StorageCleanupFailure(
    string Path,
    string Error);

public sealed record StorageCleanupResult(
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<StorageCleanupFailure> Failures,
    long ReleasedBytes);

public sealed record StorageProcessConflict(
    ProcessSnapshot Process,
    string Ecosystem,
    string Reason);
