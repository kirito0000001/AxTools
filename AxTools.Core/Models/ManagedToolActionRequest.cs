namespace AxTools.Core.Models;

public sealed record ManagedToolActionRequest(
    ManagedToolAction Action,
    string Version = "",
    string Channel = "stable",
    string ReleaseNotes = "");
