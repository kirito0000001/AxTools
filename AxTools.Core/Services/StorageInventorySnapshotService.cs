using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class StorageInventorySnapshotService(AtomicJsonFileService writer)
{
    private const string FileName = "StorageInventory.snapshot.json";
    private readonly AtomicJsonFileService _writer = writer;

    public async Task<IReadOnlyList<StorageScanItem>> ApplyAsync(
        string projectRoot,
        IReadOnlyList<StorageScanItem> items,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectRoot, FileName);
        var previous = await ReadAsync(path, cancellationToken);
        var previousByPath = previous.Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var result = items.Select(item =>
        {
            var priorSize = previousByPath.TryGetValue(item.Path, out var old) ? old.SizeBytes : 0;
            return item with
            {
                PreviousSizeBytes = priorSize,
                GrowthBytes = old is null ? 0 : item.SizeBytes - priorSize
            };
        }).ToArray();

        var document = new StorageSnapshotDocument(
            1,
            DateTimeOffset.Now,
            result.Select(item => new StorageSnapshotItem(item.Path, item.SizeBytes)).ToArray());
        await _writer.WriteTextAsync(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return result;
    }

    private static async Task<StorageSnapshotDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new StorageSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<StorageSnapshotDocument>(json) ??
                new StorageSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }
        catch (JsonException)
        {
            return new StorageSnapshotDocument(1, DateTimeOffset.MinValue, []);
        }
    }

    private sealed record StorageSnapshotDocument(
        int SchemaVersion,
        DateTimeOffset ScannedAt,
        IReadOnlyList<StorageSnapshotItem> Items);

    private sealed record StorageSnapshotItem(string Path, long SizeBytes);
}
