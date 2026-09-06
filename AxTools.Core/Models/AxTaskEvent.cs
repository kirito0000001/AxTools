namespace AxTools.Core.Models;

public sealed record AxTaskEvent(
    AxTaskEventType Type,
    string? Stage = null,
    string? Message = null,
    string? Detail = null,
    double? Percent = null,
    string? Code = null,
    string? Kind = null,
    string? Path = null,
    AxTaskResultStatus? ResultStatus = null,
    int? ExitCode = null,
    AxTaskCancellationMode? CancellationMode = null);

public sealed record AxTaskParsedLine(
    string Text,
    bool IsProtocolLine,
    AxTaskEvent? Event = null,
    string? ProtocolError = null);
