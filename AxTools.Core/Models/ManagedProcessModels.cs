namespace AxTools.Core.Models;

public enum ManagedProcessCloseStatus
{
    Exited,
    ForceRequired,
    TargetChanged,
    Failed
}

public sealed record ManagedProcessCloseResult(
    ManagedProcessCloseStatus Status,
    string Message);
