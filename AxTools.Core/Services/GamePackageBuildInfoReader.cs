using System.Text.Json;

namespace AxTools.Core.Services;

/// <summary>打包插件（CrossingChunkEditor）在产物根目录写出的 Version.json。</summary>
public sealed record GamePackageBuildInfo(
    string Version,
    string ReleaseVersion,
    string Mode,
    string Platform,
    string ArtifactDirectory,
    string SourcePath)
{
    public bool IsPatch => string.Equals(Mode, "Patch", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 游戏版本只认打包产物：优先 &lt;包根&gt;\Version.json，其次包根上一级。
/// AxTools 不再自己维护一份可编辑的版本号，避免出现"旧包写新版本号"。
/// </summary>
public static class GamePackageBuildInfoReader
{
    public static GamePackageBuildInfo? Read(
        string? packageRoot,
        out string? error,
        string? expectedPlatform = null)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            error = "尚未选择游戏包目录。";
            return null;
        }

        string root;
        try
        {
            root = Path.GetFullPath(packageRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "游戏包目录格式无效。";
            return null;
        }

        if (!Directory.Exists(root))
        {
            error = $"游戏包目录不存在：{root}";
            return null;
        }

        var candidates = new List<string>();
        var platformNames = expectedPlatform?.Equals("Android", StringComparison.OrdinalIgnoreCase) == true
            ? new[] { "Version-Android.json", "Version.Android.json" }
            : new[] { "Version-Windows.json", "Version.Win64.json" };
        foreach (var name in platformNames)
        {
            candidates.Add(Path.Combine(root, name));
        }
        candidates.Add(Path.Combine(root, "Version.json"));
        var parent = Directory.GetParent(root)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            foreach (var name in platformNames)
            {
                candidates.Add(Path.Combine(parent, name));
            }
            candidates.Add(Path.Combine(parent, "Version.json"));
        }

        var lastPlatformError = string.Empty;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object)
                {
                    error = $"版本文件不是 JSON 对象：{candidate}";
                    return null;
                }

                var version = ReadString(data, "version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    error = $"版本文件缺少 version：{candidate}";
                    return null;
                }

                var platform = ReadString(data, "platform");
                if (!string.IsNullOrWhiteSpace(expectedPlatform) &&
                    !PlatformMatches(platform, expectedPlatform))
                {
                    // 公共父目录的 Version.json 可能是另一平台的最近一次构建；
                    // 继续找 Version-Windows.json / Version-Android.json。
                    lastPlatformError = $"版本文件平台不匹配：期望 {expectedPlatform}，实际 {platform}（{candidate}）";
                    continue;
                }

                return new GamePackageBuildInfo(
                    version.Trim(),
                    ReadString(data, "releaseVersion").Trim(),
                    ReadString(data, "mode"),
                    platform,
                    ReadString(data, "artifactDir"),
                    candidate);
            }
            catch (JsonException exception)
            {
                error = $"版本文件解析失败：{candidate}（{exception.Message}）";
                return null;
            }
            catch (IOException exception)
            {
                error = $"版本文件读取失败：{candidate}（{exception.Message}）";
                return null;
            }
        }

        error = string.IsNullOrWhiteSpace(lastPlatformError)
            ? "游戏包里没有 Version.json（应在包根或上一级），无法确定版本。"
            : lastPlatformError;
        return null;
    }

    private static bool PlatformMatches(string? value, string expected) =>
        expected.Equals("Android", StringComparison.OrdinalIgnoreCase)
            ? value?.Contains("Android", StringComparison.OrdinalIgnoreCase) == true
            : value?.Contains("Win", StringComparison.OrdinalIgnoreCase) == true ||
              value?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

    private static string ReadString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
