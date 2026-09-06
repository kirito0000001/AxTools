namespace AxTools.Core.Models;

public sealed record ManagedToolPathDetectionSummary(
    bool Changed,
    IReadOnlyDictionary<ManagedToolKey, string> Statuses);
