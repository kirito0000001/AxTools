namespace AxTools.Core.Models;

public sealed record PublishingVersionValidationResult(
    bool IsAllowed,
    string Message,
    string? OnlineVersion = null)
{
    public static PublishingVersionValidationResult Allowed(
        string message,
        string? onlineVersion = null) => new(true, message, onlineVersion);

    public static PublishingVersionValidationResult Blocked(
        string message,
        string? onlineVersion = null) => new(false, message, onlineVersion);
}
