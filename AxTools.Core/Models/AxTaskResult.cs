using System.Text;

namespace AxTools.Core.Models;

public sealed record AxTaskResult(
    string TaskId,
    AxTaskStatus Status,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Summary,
    IReadOnlyList<string> Output,
    IReadOnlyList<AxTaskEvent> Events)
{
    public TimeSpan Duration => FinishedAt - StartedAt;

    public string CreateDiagnosticLogText()
    {
        if (Status != AxTaskStatus.Failed)
        {
            return Summary;
        }

        var builder = new StringBuilder(Summary.Trim());
        var reportedMessage = Events
            .LastOrDefault(taskEvent =>
                taskEvent.Type == AxTaskEventType.Result &&
                taskEvent.ResultStatus == AxTaskResultStatus.Failed)
            ?.Message
            ?.Trim();
        if (!string.IsNullOrWhiteSpace(reportedMessage) &&
            !string.Equals(reportedMessage, Summary.Trim(), StringComparison.Ordinal))
        {
            builder.AppendLine().Append("脚本报告：").Append(reportedMessage);
        }

        var plainOutput = Output
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("::axtools ", StringComparison.Ordinal) &&
                !string.Equals(line.Trim(), reportedMessage, StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToArray();
        if (plainOutput.Length == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine().Append("任务输出：");
        foreach (var line in plainOutput)
        {
            builder.AppendLine().Append("  ").Append(line);
        }

        return builder.ToString();
    }

}
