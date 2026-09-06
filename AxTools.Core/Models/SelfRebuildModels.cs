namespace AxTools.Core.Models;

public sealed record SelfRebuildLaunchRequest(
    string ProjectRoot,
    string DevelopmentExecutable,
    int MainProcessId,
    string ReadySignalPath,
    string LogPath,
    string ResultPath);

public sealed record SelfRebuildResult(
    bool Success,
    string Message,
    string LogPath,
    DateTimeOffset FinishedAt);
