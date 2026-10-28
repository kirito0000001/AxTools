using System.Text.Json;

namespace AxTools.Core.Services;

/// <summary>打包插件在归档根写出的产物信息（Version/&lt;Scope&gt;/Latest.json 指路）。</summary>
public sealed record GamePackageBuildInfo(
    string Version,
    string ReleaseVersion,
    string Mode,
    string Platform,
    string ArtifactDirectory,
    string SourcePath,
    IReadOnlyList<GamePackageFileEntry> Files,
    string Kind,
    string Scope = "",
    string BaseVersion = "",
    string GeneratedAt = "")
{
    public bool IsPatch => string.Equals(Kind, "Patch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Mode, "Patch", StringComparison.OrdinalIgnoreCase);

    public long TotalBytes => Files.Sum(file => file.SizeBytes);
}

/// <summary>产物清单里的一个文件；Path 相对归档根，不是 scope 目录。</summary>
public sealed record GamePackageFileEntry(
    string Path,
    long SizeBytes,
    string Sha256,
    string Modified = "");

/// <summary>
/// 读取游戏打包产物版本。
///
/// 契约（插件 2026-10-28 定案）：
///   &lt;归档根&gt;\Version\&lt;Scope&gt;\Latest.json
///   - latestFile 相对**归档根**，且必须落在当前 scope 目录内
///   - latestKind 为 Full 或 Patch（Base 与 Patch 共用目录，发整包时要核对）
///   - 清单 files[].path 相对归档根；sizeBytes 为字节；sha256 小写；modified 无时区
///   - baseVersion 只有 Patch 有；recoveredAt / recoveredFrom 是补戳专有，不可依赖
///
/// 旧格式仍兼容：包根或上一级的 Version.json / Version-Windows.json / Version-Android.json。
/// </summary>
public static class GamePackageBuildInfoReader
{
    /// <summary>平台对应的 scope 目录，按优先级排列。</summary>
    private static IReadOnlyList<string> ScopeNamesFor(string? platform) =>
        platform?.Equals("Android", StringComparison.OrdinalIgnoreCase) == true
            ? ["Android-Client", "Android"]
            : ["Win64-Client", "Win64", "Windows"];

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

        if (TryReadLatest(root, expectedPlatform, out var latest, out var latestError))
        {
            return latest;
        }

        var legacy = ReadLegacy(root, expectedPlatform, out var legacyError);
        if (legacy is not null)
        {
            return legacy;
        }

        error = latestError ?? legacyError;
        return null;
    }

    private static bool TryReadLatest(
        string root,
        string? expectedPlatform,
        out GamePackageBuildInfo? info,
        out string? error)
    {
        info = null;
        error = null;

        var versionRoot = Path.Combine(root, "Version");
        if (!Directory.Exists(versionRoot))
        {
            return false;
        }

        var candidates = new List<string>();
        foreach (var scope in ScopeNamesFor(expectedPlatform))
        {
            candidates.Add(Path.Combine(versionRoot, scope, "Latest.json"));
        }

        candidates.AddRange(Directory
            .EnumerateFiles(versionRoot, "Latest.json", SearchOption.AllDirectories)
            .Take(8));

        foreach (var latestPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(latestPath))
            {
                continue;
            }

            try
            {
                using var latestDocument = JsonDocument.Parse(File.ReadAllText(latestPath));
                var latestData = latestDocument.RootElement;
                var scope = ReadString(latestData, "scope");
                var latestKind = ReadString(latestData, "latestKind");
                var latestFile = ReadString(latestData, "latestFile");
                if (string.IsNullOrWhiteSpace(latestFile))
                {
                    error = $"Latest.json 缺少 latestFile：{latestPath}";
                    continue;
                }

                var manifestPath = Path.IsPathRooted(latestFile)
                    ? latestFile
                    : Path.Combine(root, latestFile.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(manifestPath))
                {
                    error = $"Latest.json 指向的清单不存在：{manifestPath}";
                    continue;
                }

                // 自检：指针不能跨 scope。
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopeDirectory = Path.GetDirectoryName(latestPath) ?? string.Empty;
                    if (!string.IsNullOrEmpty(scopeDirectory) &&
                        !manifestPath.StartsWith(scopeDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"Latest.json 的 latestFile 跨了 scope：{latestFile}（scope={scope}）";
                        continue;
                    }
                }

                using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var data = manifestDocument.RootElement;
                var version = ReadString(data, "version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    error = $"产物清单缺少 version：{manifestPath}";
                    continue;
                }

                var platform = FirstNonEmpty(
                    ReadString(data, "platform"),
                    ReadString(latestData, "platform"));
                if (!string.IsNullOrWhiteSpace(expectedPlatform) &&
                    !string.IsNullOrWhiteSpace(platform) &&
                    !PlatformMatches(platform, expectedPlatform))
                {
                    error = $"产物平台不匹配：期望 {expectedPlatform}，实际 {platform}（{manifestPath}）";
                    continue;
                }

                info = new GamePackageBuildInfo(
                    version.Trim(),
                    ReadString(data, "releaseVersion").Trim(),
                    ReadString(data, "mode"),
                    platform,
                    ReadString(data, "artifactDir"),
                    manifestPath,
                    ReadFiles(data),
                    FirstNonEmpty(latestKind, ReadString(data, "kind"), ReadString(data, "mode")),
                    FirstNonEmpty(scope, ReadString(data, "scope")),
                    ReadString(data, "baseVersion"),
                    FirstNonEmpty(ReadString(data, "generatedAt"), ReadString(latestData, "generatedAt")));
                return true;
            }
            catch (JsonException exception)
            {
                error = $"产物清单解析失败：{latestPath}（{exception.Message}）";
            }
            catch (IOException exception)
            {
                error = $"产物清单读取失败：{latestPath}（{exception.Message}）";
            }
        }

        return false;
    }

    private static GamePackageBuildInfo? ReadLegacy(
        string root,
        string? expectedPlatform,
        out string? error)
    {
        error = null;
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
                    lastPlatformError = $"版本文件平台不匹配：期望 {expectedPlatform}，实际 {platform}（{candidate}）";
                    continue;
                }

                var mode = ReadString(data, "mode");
                return new GamePackageBuildInfo(
                    version.Trim(),
                    ReadString(data, "releaseVersion").Trim(),
                    mode,
                    platform,
                    ReadString(data, "artifactDir"),
                    candidate,
                    ReadFiles(data),
                    FirstNonEmpty(ReadString(data, "kind"), mode),
                    ReadString(data, "scope"),
                    ReadString(data, "baseVersion"),
                    ReadString(data, "generatedAt"));
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
            ? "没有找到产物版本信息（Version\\<Scope>\\Latest.json 或 Version.json）。"
            : lastPlatformError;
        return null;
    }

    private static IReadOnlyList<GamePackageFileEntry> ReadFiles(JsonElement data)
    {
        if (!data.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<GamePackageFileEntry>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = ReadString(file, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            // sizeBytes 是定案字段；旧戳还叫 size，做一次兼容。
            var size = ReadInt64(file, "sizeBytes");
            if (size == 0)
            {
                size = ReadInt64(file, "size");
            }

            list.Add(new GamePackageFileEntry(
                path.Replace('\\', '/'),
                size,
                ReadString(file, "sha256").ToLowerInvariant(),
                ReadString(file, "modified")));
        }

        return list;
    }

    private static long ReadInt64(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

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
