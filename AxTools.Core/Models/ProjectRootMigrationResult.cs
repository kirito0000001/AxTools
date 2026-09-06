namespace AxTools.Core.Models;

public sealed record ProjectRootMigrationResult(
    int FileCount,
    int DirectoryCount,
    long TotalBytes);
