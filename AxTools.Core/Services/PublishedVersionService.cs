using System.Net;
using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class PublishedVersionService
{
    private const string GitHubBase = "https://api.github.com/repos";
    private const string GiteeRawBase = "https://gitee.com";
    private const string OfficialSiteBase = "https://www.crossingvoid.top";
    private readonly HttpClient _client;

    public PublishedVersionService(HttpClient client)
    {
        _client = client;
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("AxTools-PublishedVersion/1.0");
        }
    }

    public async Task<PublishedVersionResult> CheckLauncherAsync(
        ManagedToolKey key,
        string channel,
        CancellationToken cancellationToken)
    {
        return key switch
        {
            ManagedToolKey.CrossingVoidPc => await ReadManifestAsync(
                $"{OfficialSiteBase}/api/toolbox-updates/tauri/crossingvoid-launcher-pc/windows/x86_64/0.0.0",
                "官网 PC 启动器更新接口",
                ["version"],
                cancellationToken),
            ManagedToolKey.CrossingVoidAndroid => await CheckCrossingVoidAndroidLauncherAsync(
                channel,
                cancellationToken),
            ManagedToolKey.FantasyProjectPc => await ReadManifestAsync(
                $"{GiteeRawBase}/xiaojie578/FantasyProject-Downloader-PC/raw/master/launcher/latest.json",
                "Gitee 启动器发布清单",
                ["version"],
                cancellationToken),
            ManagedToolKey.FantasyTools => await ReadGitHubReleasesAsync(
                "kirito0000001/FantasyTools",
                channel,
                cancellationToken),
            ManagedToolKey.GalExcleTools => await ReadGitHubReleasesAsync(
                "kirito0000001/TFACStorybox",
                channel,
                cancellationToken),
            ManagedToolKey.CrossingVoidZDTool => await ReadGitHubReleasesAsync(
                "kirito0000001/CrossingVoidZDTool",
                channel,
                cancellationToken),
            ManagedToolKey.AxTools => await ReadGitHubReleasesAsync(
                "kirito0000001/AxTools",
                channel,
                cancellationToken),
            _ => new PublishedVersionResult(
                false,
                null,
                "未配置",
                "此工具没有固定的线上发布来源。")
        };
    }

    private async Task<PublishedVersionResult> CheckCrossingVoidAndroidLauncherAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadManifestAsync(
                $"{OfficialSiteBase}/manifests/launcher/android-latest.json",
                "官网 Android 启动器清单",
                ["versionName"],
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return await ReadPrefixedReleasesAsync(
                $"{GiteeRawBase}/api/v5/repos/xiaojie578/CrossingVoid-Downloader-Android/releases?per_page=100",
                "android-installer-v",
                "Gitee Android Release（官网清单不可用时兜底）",
                channel,
                cancellationToken);
        }
    }

    private async Task<PublishedVersionResult> ReadPrefixedReleasesAsync(
        string url,
        string tagPrefix,
        string sourceName,
        string channel,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = new List<SemanticVersion>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var tag = ReadString(release, "tag_name");
            if (tag is null || !tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var version = SemanticVersion.Parse(tag[tagPrefix.Length..]);
                var prerelease = release.TryGetProperty("prerelease", out var prereleaseNode) &&
                    prereleaseNode.ValueKind == JsonValueKind.True;
                if (!string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase) ||
                    (!prerelease && !version.IsPrerelease))
                {
                    candidates.Add(version);
                }
            }
            catch (FormatException)
            {
            }
        }

        var selected = candidates.OrderByDescending(version => version).FirstOrDefault();
        return selected is null
            ? new PublishedVersionResult(false, null, sourceName, "发布页暂无版本。")
            : new PublishedVersionResult(true, selected.ToString(), sourceName, $"线上当前版本：{selected}");
    }

    public async Task<CrossingVoidGamePublishedVersionResult> CheckCrossingVoidGameAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var suffix = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "-test-latest.json"
            : "-latest.json";
        var pcTask = TryReadManifestAsync(
            $"{OfficialSiteBase}/manifests/game/windows{suffix}",
            "官网 PC 游戏清单",
            ["latest", "version"],
            cancellationToken);
        var androidTask = TryReadManifestAsync(
            $"{OfficialSiteBase}/manifests/game/android{suffix}",
            "官网 Android 游戏清单",
            ["latest", "version"],
            cancellationToken);
        await Task.WhenAll(pcTask, androidTask);
        var pc = await pcTask;
        var android = await androidTask;
        var aligned = pc.IsAvailable &&
            android.IsAvailable &&
            string.Equals(pc.Version, android.Version, StringComparison.OrdinalIgnoreCase);
        var status = aligned
            ? $"双端线上版本一致：{pc.Version}"
            : $"双端线上版本未能确认一致：PC {FormatPlatformResult(pc)}；Android {FormatPlatformResult(android)}。";
        return new CrossingVoidGamePublishedVersionResult(
            pc.Version,
            android.Version,
            aligned,
            aligned ? pc.Version : null,
            status);
    }

    private async Task<PublishedVersionResult> TryReadManifestAsync(
        string url,
        string sourceName,
        IReadOnlyList<string> versionPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadManifestAsync(url, sourceName, versionPath, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            return new PublishedVersionResult(false, null, sourceName, $"检查失败：{exception.Message}");
        }
    }

    private static string FormatPlatformResult(PublishedVersionResult result) =>
        result.IsAvailable ? result.Version ?? "未发布" : result.Status;

    private async Task<PublishedVersionResult> ReadGitHubReleasesAsync(
        string repository,
        string channel,
        CancellationToken cancellationToken)
    {
        var url = $"{GitHubBase}/{repository}/releases?per_page=30";
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PublishedVersionResult(false, null, "GitHub Release", "发布页暂无版本。");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = new List<(SemanticVersion Version, DateTimeOffset? PublishedAt)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            var tag = ReadString(release, "tag_name")?.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }
            SemanticVersion version;
            try
            {
                version = SemanticVersion.Parse(tag);
            }
            catch (FormatException)
            {
                continue;
            }
            var prerelease = release.TryGetProperty("prerelease", out var prereleaseNode) &&
                prereleaseNode.ValueKind == JsonValueKind.True;
            if (string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase) &&
                (prerelease || version.IsPrerelease))
            {
                continue;
            }
            candidates.Add((
                version,
                DateTimeOffset.TryParse(ReadString(release, "published_at"), out var date)
                    ? date
                    : null));
        }
        var selected = candidates.OrderByDescending(item => item.Version).FirstOrDefault();
        return selected.Version is null
            ? new PublishedVersionResult(false, null, "GitHub Release", "当前通道的发布页暂无版本。")
            : new PublishedVersionResult(
                true,
                selected.Version.ToString(),
                "GitHub Release",
                $"线上当前版本：{selected.Version}",
                selected.PublishedAt);
    }

    private async Task<PublishedVersionResult> ReadManifestAsync(
        string url,
        string sourceName,
        IReadOnlyList<string> versionPath,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PublishedVersionResult(false, null, sourceName, "发布页暂无版本。");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var node = document.RootElement;
        foreach (var property in versionPath)
        {
            if (!node.TryGetProperty(property, out node))
            {
                throw new InvalidDataException($"{sourceName}缺少版本字段：{string.Join('.', versionPath)}");
            }
        }
        var version = node.ValueKind == JsonValueKind.String ? node.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException($"{sourceName}的版本为空。");
        }
        DateTimeOffset? publishedAt = DateTimeOffset.TryParse(
            ReadString(document.RootElement, "publishedAt"),
            out var date)
            ? date
            : null;
        return new PublishedVersionResult(
            true,
            version,
            sourceName,
            $"线上当前版本：{version}",
            publishedAt);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
}
