namespace AxTools.Core.Models;

public sealed record PublishedVersionResult(
    bool IsAvailable,
    string? Version,
    string SourceName,
    string Status,
    DateTimeOffset? PublishedAt = null,
    bool CheckFailed = false);

public sealed record CrossingVoidGamePublishedVersionResult(
    string? PcVersion,
    string? AndroidVersion,
    bool IsAligned,
    string? UnifiedVersion,
    string Status,
    bool CheckFailed = false);
