using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record TaskHistoryPaths(
    string SummaryPath,
    string LogPath);

public sealed class TaskHistoryService
{
    private readonly object _rootLock = new();
    private string _projectRoot;
    private readonly AtomicJsonFileService _writer;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public TaskHistoryService(
        string projectRoot,
        AtomicJsonFileService writer)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _writer = writer;
    }

    public async Task<TaskHistoryPaths> SaveAsync(
        AxTaskResult result,
        CancellationToken cancellationToken)
    {
        string projectRoot;
        lock (_rootLock)
        {
            projectRoot = _projectRoot;
        }

        var dayDirectory = Path.Combine(
            projectRoot,
            "TaskHistory",
            result.StartedAt.ToString("yyyyMMdd"));
        var safeTaskId = SanitizeFileName(result.TaskId);
        var fileStem = $"{result.StartedAt:HHmmssfff}-{safeTaskId}";
        var summaryPath = Path.Combine(dayDirectory, fileStem + ".json");
        var logPath = Path.Combine(dayDirectory, fileStem + ".log");
        var summary = new
        {
            result.TaskId,
            result.Status,
            result.ExitCode,
            result.StartedAt,
            result.FinishedAt,
            durationMilliseconds = result.Duration.TotalMilliseconds,
            result.Summary,
            artifacts = result.Events
                .Where(taskEvent => taskEvent.Type == AxTaskEventType.Artifact)
                .Select(taskEvent => new { taskEvent.Kind, taskEvent.Path })
                .ToArray()
        };

        await _writer.WriteTextAsync(
            summaryPath,
            JsonSerializer.Serialize(summary, _jsonOptions),
            cancellationToken);
        await _writer.WriteTextAsync(
            logPath,
            string.Join(Environment.NewLine, result.Output),
            cancellationToken);
        return new TaskHistoryPaths(summaryPath, logPath);
    }

    public void Rebind(string projectRoot)
    {
        lock (_rootLock)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "task";
        }

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }
}
