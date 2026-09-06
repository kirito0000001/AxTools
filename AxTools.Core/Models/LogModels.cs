namespace AxTools.Core.Models;

public enum LogKind
{
    Info,
    User,
    Warning,
    Error
}

public sealed record LogEntry(
    DateTime Timestamp,
    LogKind Kind,
    string Message,
    string DisplayText,
    string CopyText);
