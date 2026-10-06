using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class PublishedVersionService
{
    private const string GitHubBase = "https://api.github.com/repos";
    private const string GiteeRawBase = "https://gitee.com";
    private const string OfficialSiteBase = "https://www.crossingvoid.top";
    private const string DownloadSiteBase = "https://dl.crossingvoid.top";
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
            // 零境素材（ZD工具箱）只发 Gitee 一个源，Release 挂在 xiaojie578/CrossingVoidZDTool，
            // tag 形如 v1.0.1 / v1.0.2-Beta（见 ZD 工程 Docs\热更新-发布侧需求.md §1 / §3.1）。
            // 这里以前读的是 GitHub 上的另一个仓库，那边一个 Release 都没有。
            ManagedToolKey.CrossingVoidZDTool => await CheckCrossingVoidZDToolAsync(
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

    /// <summary>
    /// 零境素材（ZD工具箱）的线上版本。
    ///
    /// ★ 首选**不走 Gitee API**：<c>releases/download/latest/toolbox-update.json</c> 就是一条普通文件下载
    /// （302 → CDN），没有匿名配额这回事。而 Gitee 匿名 API 是 60 次/小时、**按 IP** 计，开发机上很容易
    /// 被发布动作与客户端探针一起打光 —— 2026-10-06 实测：403 Rate Limit Exceeded 让这一格永远显示
    /// "检查失败"。这与安卓启动器"官网清单优先、Gitee 兜底"是同一条思路：能读清单就不问 API。
    ///
    /// <c>latest</c> 指向的是"最新那条 Release"，所以还要核对清单里的 <c>channel</c>：
    /// 与本次要查的通道不一致（例如最新发的是 beta、这次查 stable）才回落 API，由 API 按 prerelease 精确筛。
    /// </summary>
    private async Task<PublishedVersionResult> CheckCrossingVoidZDToolAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        const string sourceName = "Gitee ZD工具箱最新版清单";
        try
        {
            var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var manifest = await ReadManifestIdentityAsync(
                $"{GiteeRawBase}/xiaojie578/CrossingVoidZDTool/releases/download/latest/toolbox-update.json?t={cacheBuster}",
                sourceName,
                cancellationToken).ConfigureAwait(false);
            var matchesChannel = string.IsNullOrWhiteSpace(manifest.Channel) ||
                string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase);
            var matchesProduct = string.IsNullOrWhiteSpace(manifest.StableKey) ||
                string.Equals(manifest.StableKey, "CrossingVoidZDTool", StringComparison.OrdinalIgnoreCase);
            if (matchesChannel && matchesProduct)
            {
                return new PublishedVersionResult(
                    true,
                    manifest.Version,
                    sourceName,
                    $"线上当前版本：{manifest.Version}（通道 {manifest.Channel ?? channel}）",
                    manifest.PublishedAt);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            // 清单还没发过（404）或取不回来 → 回落 API 再试一次，主源/兜底的关系与安卓一致。
        }

        return await ReadPrefixedReleasesAsync(
            $"{GiteeRawBase}/api/v5/repos/xiaojie578/CrossingVoidZDTool/releases?per_page=100",
            "v",
            "Gitee ZD工具箱发布页（最新版清单不可用时兜底）",
            channel,
            cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct ManifestIdentity(
        string? Version,
        string? Channel,
        string? StableKey,
        DateTimeOffset? PublishedAt);

    private async Task<ManifestIdentity> ReadManifestIdentityAsync(
        string url,
        string sourceName,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var version = ReadString(document.RootElement, "version")?.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException($"{sourceName}缺少版本字段：version");
        }

        return new ManifestIdentity(
            version,
            ReadString(document.RootElement, "channel"),
            ReadString(document.RootElement, "toolboxStableKey"),
            DateTimeOffset.TryParse(ReadString(document.RootElement, "publishedAt"), out var date) ? date : null);
    }

    /// <summary>
    /// 非 2xx 时**连正文一起**抛出去。Gitee 的限流只能靠正文辨认（403 + <c>Rate Limit Exceeded</c>，
    /// 既不是 429 也没有 <c>X-RateLimit-*</c> 头），而 <c>EnsureSuccessStatusCode</c> 抛出的消息里
    /// 只有一句 "403 (Forbidden)" —— 调用方于是分不清"限流"和"没权限"，用户看到的就是个吓人的 403。
    /// </summary>
    private static async Task ThrowForFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ObjectDisposedException)
        {
        }

        var summary = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : "：" + (body.Trim().Length > 200 ? body.Trim()[..200] : body.Trim());
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{summary}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private async Task<PublishedVersionResult> ReadPrefixedReleasesAsync(
        string url,
        string tagPrefix,
        string sourceName,
        string channel,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 读某个游戏在**下载站**上的最新版本：PC 段在 <c>games/&lt;产品段&gt;/manifests/</c>，
    /// 安卓段在 <c>games/&lt;产品段&gt;-android/manifests/</c>。
    ///
    /// 发布内核每次都会清掉更早的清单，目录里只留当前版本，所以"目录索引里版本号最高的那一份"
    /// 就是线上最新版；目录不存在（404）＝这个平台还没发过。
    /// </summary>
    public async Task<CrossingVoidGamePublishedVersionResult> CheckGameAsync(
        string productSegment,
        CancellationToken cancellationToken)
    {
        var pcTask = TryReadLatestPublishedVersionAsync(
            $"{DownloadSiteBase}/games/{productSegment}/manifests/",
            "下载站 PC 清单目录",
            cancellationToken);
        var androidTask = TryReadLatestPublishedVersionAsync(
            $"{DownloadSiteBase}/games/{productSegment}-android/manifests/",
            "下载站 Android 清单目录",
            cancellationToken);
        await Task.WhenAll(pcTask, androidTask);
        var pc = await pcTask;
        var android = await androidTask;
        var aligned = pc.IsAvailable &&
            android.IsAvailable &&
            string.Equals(pc.Version, android.Version, StringComparison.OrdinalIgnoreCase);
        var status = aligned
            ? $"下载站双端版本一致：{pc.Version}"
            : $"下载站版本：PC {FormatPlatformResult(pc)}；Android {FormatPlatformResult(android)}。";
        return new CrossingVoidGamePublishedVersionResult(
            pc.Version,
            android.Version,
            aligned,
            aligned ? pc.Version : null,
            status,
            // 两端都读不到（网络/代理问题）才算"检查失败"；某端没发过只是"还没发布"。
            pc.CheckFailed && android.CheckFailed);
    }

    private async Task<PublishedVersionResult> TryReadLatestPublishedVersionAsync(
        string directoryUrl,
        string sourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            // 目录索引也可能被缓存，带时间戳强制拿新的（和发布内核校验公网清单时同一招）。
            var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var response = await _client
                .GetAsync($"{directoryUrl}?t={cacheBuster}", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new PublishedVersionResult(false, null, sourceName, "还没发过这个平台的版本。");
            }

            await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
            var index = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var latest = ParseLatestVersionFromDirectoryIndex(index);
            return latest is null
                ? new PublishedVersionResult(false, null, sourceName, "清单目录里还没有版本。")
                : new PublishedVersionResult(true, latest, sourceName, $"线上当前版本：{latest}");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            return new PublishedVersionResult(
                false,
                null,
                sourceName,
                $"检查失败：{exception.Message}",
                null,
                true);
        }
    }

    /// <summary>
    /// 从目录索引里挑版本号最高的那份清单（nginx 生成的索引形如 <c>&lt;a href="0.5.13.json"&gt;</c>）。
    /// 版本比较统一用 <see cref="PublishVersionComparer"/>：它支持三段、四段和 -beta 这类预发布号，
    /// 和发布门禁用的是同一套规则。
    /// </summary>
    private static string? ParseLatestVersionFromDirectoryIndex(string index)
    {
        var candidates = new List<string>();
        foreach (Match match in Regex.Matches(index, "href=\"(?<name>[^\"/]+\\.json)\"", RegexOptions.IgnoreCase))
        {
            var version = Path.GetFileNameWithoutExtension(match.Groups["name"].Value);
            try
            {
                // 解析不了（例如 latest.json 这类名字）就跳过；能解析才参与比较。
                PublishVersionComparer.Compare(version, version);
                candidates.Add(version);
            }
            catch (FormatException)
            {
            }
        }

        return candidates
            .OrderByDescending(
                version => version,
                Comparer<string>.Create(PublishVersionComparer.Compare))
            .FirstOrDefault();
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
        await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
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
        await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
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
