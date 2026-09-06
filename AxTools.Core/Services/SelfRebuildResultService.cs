using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public static class SelfRebuildResultService
{
    public static string GetDefaultResultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxTools",
        "SelfRebuild",
        "last-result.json");

    public static SelfRebuildResult? Take(string resultPath)
    {
        var fullPath = Path.GetFullPath(resultPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize<SelfRebuildResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SelfRebuildResult(
                false,
                $"无法读取上次外部自重建结果：{exception.Message}",
                fullPath,
                DateTimeOffset.Now);
        }
        finally
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException)
            {
                // A stale result is harmless and can be consumed on the next launch.
            }
            catch (UnauthorizedAccessException)
            {
                // A stale result is harmless and can be consumed on the next launch.
            }
        }
    }
}
