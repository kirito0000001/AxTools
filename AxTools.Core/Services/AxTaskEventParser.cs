using System.Text.Json;
using System.Text.RegularExpressions;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class AxTaskEventParser
{
    public const string Prefix = "::axtools ";

    private static readonly Regex AnsiPattern = new(
        "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AxTaskParsedLine Parse(string? line)
    {
        var rawText = line ?? string.Empty;
        var cleanText = AnsiPattern.Replace(rawText, string.Empty);
        if (!cleanText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AxTaskParsedLine(cleanText, IsProtocolLine: false);
        }

        try
        {
            using var document = JsonDocument.Parse(cleanText[Prefix.Length..]);
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var typeText))
            {
                return Invalid(cleanText, "结构化事件缺少 type。");
            }

            if (!TryParseType(typeText, out var type))
            {
                return Invalid(cleanText, $"未知事件类型：{typeText}。");
            }

            var taskEvent = new AxTaskEvent(
                type,
                Stage: GetString(root, "stage"),
                Message: GetString(root, "message"),
                Detail: GetString(root, "detail"),
                Percent: GetPercent(root),
                Code: GetString(root, "code"),
                Kind: GetString(root, "kind"),
                Path: GetString(root, "path"),
                ResultStatus: GetResultStatus(root),
                ExitCode: GetInt32(root, "exitCode"),
                CancellationMode: GetCancellationMode(root));

            return new AxTaskParsedLine(cleanText, IsProtocolLine: true, taskEvent);
        }
        catch (JsonException exception)
        {
            return Invalid(cleanText, $"事件 JSON 无效：{exception.Message}");
        }
    }

    private static AxTaskParsedLine Invalid(string text, string error) =>
        new(text, IsProtocolLine: true, Event: null, ProtocolError: error);

    private static bool TryParseType(string value, out AxTaskEventType type)
    {
        type = value.ToLowerInvariant() switch
        {
            "stage" => AxTaskEventType.Stage,
            "progress" => AxTaskEventType.Progress,
            "artifact" => AxTaskEventType.Artifact,
            "warning" => AxTaskEventType.Warning,
            "result" => AxTaskEventType.Result,
            "cancellation" => AxTaskEventType.Cancellation,
            _ => default
        };

        return value is "stage" or "progress" or "artifact" or "warning" or "result" or "cancellation";
    }

    private static double? GetPercent(JsonElement root)
    {
        if (!root.TryGetProperty("percent", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var percent))
        {
            return null;
        }

        return Math.Clamp(percent, 0, 100);
    }

    private static AxTaskResultStatus? GetResultStatus(JsonElement root) =>
        GetString(root, "status")?.ToLowerInvariant() switch
        {
            "success" or "succeeded" => AxTaskResultStatus.Succeeded,
            "stopped" or "cancelled" or "canceled" => AxTaskResultStatus.Stopped,
            "failure" or "failed" => AxTaskResultStatus.Failed,
            _ => null
        };

    private static AxTaskCancellationMode? GetCancellationMode(JsonElement root) =>
        GetString(root, "mode")?.ToLowerInvariant() switch
        {
            "cancel" or "cancelable" => AxTaskCancellationMode.Cancel,
            "stop" or "stoppable" => AxTaskCancellationMode.Stop,
            "locked" => AxTaskCancellationMode.Locked,
            _ => null
        };

    private static int? GetInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? GetString(JsonElement root, string propertyName) =>
        TryGetString(root, propertyName, out var value) ? value : null;

    private static bool TryGetString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
