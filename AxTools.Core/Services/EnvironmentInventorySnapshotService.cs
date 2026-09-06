using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record EnvironmentInventorySnapshotResult(
    IReadOnlyList<ToolchainStatusItem> Items,
    string ChangeSummary);

public sealed class EnvironmentInventorySnapshotService(AtomicJsonFileService writer)
{
    private const string FileName = "EnvironmentInventory.snapshot.json";
    private readonly AtomicJsonFileService _writer = writer;

    public async Task<EnvironmentInventorySnapshotResult> ApplyAsync(
        string projectRoot,
        IReadOnlyList<ToolchainStatusItem> items,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectRoot, FileName);
        var previous = await ReadAsync(path, cancellationToken);
        var previousByKey = previous.Items.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var now = DateTimeOffset.Now;
        var annotated = new List<ToolchainStatusItem>(items.Count);
        var changes = new List<string>();
        var snapshots = new List<EnvironmentSnapshotItem>(items.Count);

        foreach (var item in items)
        {
            previousByKey.TryGetValue(item.Key, out var old);
            var isNew = old is null;
            var change = isNew
                ? "首次发现"
                : !string.Equals(old!.CurrentVersion, item.CurrentVersion, StringComparison.Ordinal)
                    ? $"版本变化：{old.CurrentVersion} -> {item.CurrentVersion}"
                    : !string.Equals(old.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                        ? $"路径变化：{old.Path} -> {item.Path}"
                        : string.Empty;
            if (!string.IsNullOrWhiteSpace(change))
            {
                changes.Add($"{item.DisplayName}：{change}");
            }

            annotated.Add(item with { IsNew = isNew, ChangeSummary = change });
            snapshots.Add(new EnvironmentSnapshotItem(
                item.Key,
                item.DisplayName,
                item.CurrentVersion,
                item.Path,
                old?.FirstSeenAt ?? now,
                now));
        }

        foreach (var removed in previous.Items.Where(old => items.All(item => item.Key != old.Key)))
        {
            changes.Add($"{removed.DisplayName}：本次未检测到");
        }

        var json = JsonSerializer.Serialize(
            new EnvironmentSnapshotDocument(1, now, snapshots),
            new JsonSerializerOptions { WriteIndented = true });
        await _writer.WriteTextAsync(path, json, cancellationToken);
        return new EnvironmentInventorySnapshotResult(
            annotated,
            changes.Count == 0 ? "与上次扫描一致" : string.Join("；", changes));
    }

    private static async Task<EnvironmentSnapshotDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new EnvironmentSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<EnvironmentSnapshotDocument>(json) ??
                new EnvironmentSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }
        catch (JsonException)
        {
            return new EnvironmentSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }
    }

    private sealed record EnvironmentSnapshotDocument(
        int SchemaVersion,
        DateTimeOffset ScannedAt,
        IReadOnlyList<EnvironmentSnapshotItem> Items);

    private sealed record EnvironmentSnapshotItem(
        string Key,
        string DisplayName,
        string CurrentVersion,
        string Path,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt);
}
