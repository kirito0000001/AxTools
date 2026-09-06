namespace AxTools.Core.Models;

public sealed record ManagedToolValidationResult(
    bool IsValid,
    string Message,
    IReadOnlyList<string> MissingPaths)
{
    public static ManagedToolValidationResult Success(string message) =>
        new(true, message, Array.Empty<string>());

    public static ManagedToolValidationResult Failure(
        string message,
        IReadOnlyList<string>? missingPaths = null) =>
        new(false, message, missingPaths ?? Array.Empty<string>());
}
