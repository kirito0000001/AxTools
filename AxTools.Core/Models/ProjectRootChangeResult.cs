namespace AxTools.Core.Models;

public sealed record ProjectRootChangeResult(
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    bool OldDirectoryDeleted,
    string? CleanupError);
