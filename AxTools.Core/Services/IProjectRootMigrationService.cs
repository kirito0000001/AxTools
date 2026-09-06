using AxTools.Core.Models;

namespace AxTools.Core.Services;

public interface IProjectRootMigrationService
{
    Task<ProjectRootMigrationResult> CopyAndVerifyAsync(
        string oldRoot,
        string newRoot,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);

    bool TryDeleteDirectory(string path, out string? error);
}
