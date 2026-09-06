namespace AxTools.Core.Models;

public sealed record AxToolsPathDetectionResult(
    bool SourceFound,
    bool Changed,
    string Message);
