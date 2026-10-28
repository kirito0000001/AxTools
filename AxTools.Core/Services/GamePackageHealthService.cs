using System.Net.Http;
using System.Text.Json;

namespace AxTools.Core.Services;

/// <summary>体检结果：Healthy 为总判定，Lines 逐条列出问题或通过项。</summary>
public sealed record GamePackageHealthReport(
    bool Healthy,
    string Summary,
    IReadOnlyList<string> Lines);

/// <summary>
/// 游戏包体检：拿本地打包戳（Latest.json → 清单）与线上下载站比对。
///
/// 本地戳在打包时已经算好每个文件的 sha256，所以这里**不重新扫盘**，
/// 只比两份清单：版本、包型、文件数、总大小、逐文件哈希。
/// </summary>
public static class GamePackageHealthService
{
    private const string DownloadSiteBase = "https://dl.crossingvoid.top";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<GamePackageHealthReport> CheckAsync(
        GamePackageBuildInfo local,
        string segment,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var problems = 0;

        void Fail(string message)
        {
            problems++;
            lines.Add("✗ " + message);
        }

        void Pass(string message) => lines.Add("✓ " + message);

        JsonElement latest;
        try
        {
            latest = await GetJsonAsync($"{DownloadSiteBase}/games/{segment}/latest.json", cancellationToken);
        }
        catch (Exception exception)
        {
            lines.Add("✗ 读取线上 latest.json 失败：" + exception.Message);
            return new GamePackageHealthReport(false, "体检未通过：线上清单读取失败。", lines);
        }

        var onlineVersion = ReadString(latest, "version");
        var manifestUrl = ReadString(latest, "manifestUrl");
        if (string.IsNullOrWhiteSpace(onlineVersion) || string.IsNullOrWhiteSpace(manifestUrl))
        {
            Fail("线上 latest.json 缺少 version 或 manifestUrl。");
            return new GamePackageHealthReport(false, "体检未通过：线上清单不完整。", lines);
        }

        if (string.Equals(onlineVersion, local.Version, StringComparison.OrdinalIgnoreCase))
        {
            Pass($"版本一致：{local.Version}");
        }
        else
        {
            lines.Add($"• 版本不同：本地 {local.Version}，线上 {onlineVersion}（发布后会变成 {local.Version}）");
        }

        JsonElement manifest;
        try
        {
            manifest = await GetJsonAsync(manifestUrl, cancellationToken);
        }
        catch (Exception exception)
        {
            Fail("读取线上 manifest 失败：" + exception.Message);
            return new GamePackageHealthReport(false, "体检未通过：线上 manifest 读取失败。", lines);
        }

        var onlineFiles = new Dictionary<string, (long Size, string Sha)>(StringComparer.OrdinalIgnoreCase);
        if (manifest.TryGetProperty("files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesNode.EnumerateArray())
            {
                var path = ReadString(file, "path").Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var size = file.TryGetProperty("sizeBytes", out var sizeNode) && sizeNode.ValueKind == JsonValueKind.Number
                    ? sizeNode.GetInt64()
                    : (file.TryGetProperty("size", out var legacySize) && legacySize.ValueKind == JsonValueKind.Number
                        ? legacySize.GetInt64()
                        : 0);
                onlineFiles[path] = (size, ReadString(file, "sha256").ToLowerInvariant());
            }
        }

        if (onlineFiles.Count == 0)
        {
            Fail("线上 manifest 没有文件清单。");
            return new GamePackageHealthReport(false, "体检未通过：线上清单为空。", lines);
        }

        Pass($"线上清单：{onlineFiles.Count} 个文件");

        var missingOnline = 0;
        var missingLocal = 0;
        var hashMismatch = 0;
        var sizeMismatch = 0;
        var localPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in local.Files)
        {
            localPaths.Add(file.Path);
            if (!onlineFiles.TryGetValue(file.Path, out var online))
            {
                missingOnline++;
                continue;
            }

            if (online.Size != file.SizeBytes)
            {
                sizeMismatch++;
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256) &&
                !string.IsNullOrWhiteSpace(online.Sha) &&
                !string.Equals(file.Sha256, online.Sha, StringComparison.OrdinalIgnoreCase))
            {
                hashMismatch++;
            }
        }

        foreach (var path in onlineFiles.Keys)
        {
            if (!localPaths.Contains(path))
            {
                missingLocal++;
            }
        }

        if (missingOnline == 0)
        {
            Pass("本地每个文件都能在线上找到");
        }
        else
        {
            lines.Add($"• 有 {missingOnline} 个本地文件线上没有（首次发布会这样）");
        }

        if (missingLocal > 0)
        {
            lines.Add($"• 线上有 {missingLocal} 个文件本地这份产物里没有（删除或换包会导致）");
        }

        if (sizeMismatch > 0)
        {
            Fail($"{sizeMismatch} 个文件大小与线上不一致");
        }

        if (hashMismatch > 0)
        {
            Fail($"{hashMismatch} 个文件 sha256 与线上不一致");
        }
        else if (local.Files.Count > 0)
        {
            Pass("同名文件哈希全部一致");
        }

        var localBytes = local.TotalBytes;
        var onlineBytes = onlineFiles.Values.Sum(item => item.Size);
        lines.Add($"• 体积：本地 {FormatBytes(localBytes)}，线上 {FormatBytes(onlineBytes)}");
        lines.Add($"• 包型：本地 {local.Kind}（{local.Files.Count} 项）");

        var healthy = problems == 0;
        var summary = healthy
            ? $"体检通过：本地 {local.Version} 与线上清单对齐。"
            : $"体检发现 {problems} 类问题，详见下方。";
        return new GamePackageHealthReport(healthy, summary, lines);
    }

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var payload = await Http.GetStringAsync(
            $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            cancellationToken);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };

    private static string ReadString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
