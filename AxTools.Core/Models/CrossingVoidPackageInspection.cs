namespace AxTools.Core.Models;

public sealed record CrossingVoidPackageInspection(
    bool IsValid,
    int IncludedFileCount,
    long IncludedBytes,
    int ExcludedFileCount,
    long ExcludedBytes,
    int EstimatedChunkCount,
    string ResolvedVersion,
    IReadOnlyList<string> Errors);
