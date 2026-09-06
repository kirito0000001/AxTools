using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ManagedPublishVersionPersistenceService
{
    public void Persist(ManagedToolKey key, string sourceRoot, string version)
    {
        if (key != ManagedToolKey.CrossingVoidPc ||
            string.IsNullOrWhiteSpace(sourceRoot) ||
            !Directory.Exists(sourceRoot))
        {
            return;
        }

        var normalized = ThreePartVersion.ParseOrDefault(version).ToString();
        var path = Path.Combine(sourceRoot, "Saved", "Launcher", "developer-version.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["version"] = normalized },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
