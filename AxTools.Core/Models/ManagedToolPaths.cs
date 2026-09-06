namespace AxTools.Core.Models;

public sealed class ManagedToolPaths
{
    public string SourceRoot { get; set; } = string.Empty;

    public string DevelopmentExecutable { get; set; } = string.Empty;

    public string ReleaseExecutable { get; set; } = string.Empty;

    public string OutputRoot { get; set; } = string.Empty;

    public string PublishVersion { get; set; } = string.Empty;

    public string PublishChannel { get; set; } = "stable";

    public string PublishReleaseNotes { get; set; } = string.Empty;

    public string GamePackageRoot { get; set; } = string.Empty;

    public string GamePublishVersion { get; set; } = string.Empty;

    public string GamePublishChannel { get; set; } = "stable";

    public string GamePublishPlatform { get; set; } = "Windows";

    public string GamePublishReleaseNotes { get; set; } = string.Empty;
}
