namespace AxTools.Core.Models;

public enum UpdateSource
{
    GitHub,
    Gitee
}

public enum UpdateChannel
{
    Stable,
    Beta
}

public sealed record UpdateAsset(
    string Runtime,
    string FileName,
    string DownloadUrl,
    string Sha256,
    long SizeBytes);

public sealed record UpdateRelease(
    string Version,
    UpdateChannel Channel,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    UpdateAsset Asset,
    UpdateSource Source);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    UpdateRelease? Release,
    string Status);

public sealed record ValidatedUpdatePackage(
    string Version,
    string Runtime,
    string EntryExe);

public sealed record UpdaterLaunchRequest(
    string PackagePath,
    string TargetDirectory,
    int MainProcessId,
    string EntryExe,
    string TargetVersion,
    string ReadySignalPath,
    string LogPath);
