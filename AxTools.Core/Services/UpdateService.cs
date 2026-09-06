using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class UpdateService
{
    public const string GitHubReleasesUrl = "https://api.github.com/repos/kirito0000001/AxTools/releases";
    public const string GiteeReleasesUrl = "https://gitee.com/api/v5/repos/xiaojie578/AxTools/releases";

    private readonly HttpClient _client;

    public UpdateService(HttpClient client)
    {
        _client = client;
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("AxTools-Updater/1.0");
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateSource source,
        UpdateChannel channel,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var endpoint = source == UpdateSource.GitHub ? GitHubReleasesUrl : GiteeReleasesUrl;
        using var response = await _client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = new List<(SemanticVersion Version, string ManifestUrl, Dictionary<string, string> Assets)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var versionText = ReadString(release, "tag_name")?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(versionText)) continue;
            SemanticVersion version;
            try { version = SemanticVersion.Parse(versionText); } catch (FormatException) { continue; }
            var prerelease = release.TryGetProperty("prerelease", out var prereleaseNode) && prereleaseNode.ValueKind == JsonValueKind.True;
            if (channel == UpdateChannel.Stable && (prerelease || version.IsPrerelease)) continue;

            var assets = ReadAssets(release, source);
            if (!assets.TryGetValue("toolbox-update.json", out var manifestUrl)) continue;
            candidates.Add((version, manifestUrl, assets));
        }

        var selected = candidates.OrderByDescending(item => item.Version).FirstOrDefault();
        if (selected.Version is null)
        {
            return new UpdateCheckResult(false, null, "未找到适用于当前通道的版本。");
        }

        using var manifestResponse = await _client.GetAsync(selected.ManifestUrl, cancellationToken).ConfigureAwait(false);
        manifestResponse.EnsureSuccessStatusCode();
        var manifestJson = await manifestResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var manifestDocument = JsonDocument.Parse(manifestJson);
        var root = manifestDocument.RootElement;
        if (ReadString(root, "toolboxStableKey") != "AxTools") throw new InvalidDataException("更新清单 stable key 不匹配。");
        var manifestVersion = ReadString(root, "version") ?? throw new InvalidDataException("更新清单缺少版本。");
        if (!string.Equals(manifestVersion, selected.Version.ToString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release 与更新清单版本不一致。");
        var assetNode = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item => ReadString(item, "runtime") == "win-x64");
        if (assetNode.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("更新清单缺少 win-x64 资产。");
        var fileName = ReadString(assetNode, "fileName") ?? throw new InvalidDataException("更新资产缺少文件名。");
        selected.Assets.TryGetValue(fileName, out var downloadUrl);
        downloadUrl ??= selected.ManifestUrl.Replace("toolbox-update.json", Uri.EscapeDataString(fileName), StringComparison.OrdinalIgnoreCase);
        var updateAsset = new UpdateAsset(
            "win-x64",
            fileName,
            downloadUrl,
            ReadString(assetNode, "sha256") ?? string.Empty,
            assetNode.GetProperty("sizeBytes").GetInt64());
        var releaseInfo = new UpdateRelease(
            manifestVersion,
            selected.Version.IsPrerelease ? UpdateChannel.Beta : UpdateChannel.Stable,
            ReadString(root, "releaseNotes") ?? string.Empty,
            root.TryGetProperty("publishedAt", out var published) && DateTimeOffset.TryParse(published.GetString(), out var date) ? date : null,
            updateAsset,
            source);
        var available = selected.Version.CompareTo(SemanticVersion.Parse(currentVersion)) > 0;
        return new UpdateCheckResult(available, releaseInfo, available ? $"发现新版本 {manifestVersion}" : "当前已是最新版本。");
    }

    public async Task DownloadAsync(UpdateAsset asset, string destinationPath, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        var partial = destination + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var existingLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (asset.SizeBytes > 0 && new FileInfo(partial).Length != asset.SizeBytes) throw new InvalidDataException("下载文件大小与更新清单不一致。");
        File.Move(partial, destination, true);
    }

    private static Dictionary<string, string> ReadAssets(JsonElement release, UpdateSource source)
    {
        var property = source == UpdateSource.GitHub ? "assets" : "attach_files";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!release.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Array) return result;
        foreach (var asset in node.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            var url = ReadString(asset, "browser_download_url") ?? ReadString(asset, "download_url") ?? ReadString(asset, "url");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url)) result[name] = url;
        }
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
}

