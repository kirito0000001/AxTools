using System.Net;
using System.Text;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class PublishedVersionServiceTests
{
    [Fact]
    public async Task CheckLauncherAsync_ReadsHighestStableGitHubReleaseTag()
    {
        var handler = new StubHandler(_ => Json(
            "[{\"tag_name\":\"v2.1.0-beta.1\",\"prerelease\":true,\"published_at\":\"2026-08-20T01:00:00Z\"}," +
            "{\"tag_name\":\"v2.0.0\",\"prerelease\":false,\"published_at\":\"2026-06-24T19:43:51Z\"}]"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.FantasyTools,
            "stable",
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("2.0.0", result.Version);
        Assert.Equal("GitHub Release", result.SourceName);
        Assert.Contains("kirito0000001/FantasyTools/releases", handler.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task CheckLauncherAsync_ReadsAndroidVersionNameFromCanonicalManifest()
    {
        var handler = new StubHandler(_ => Json(
            "{\"versionName\":\"1.0.27\",\"publishedAt\":\"2026-07-21T03:37:01Z\"}"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.CrossingVoidAndroid,
            "stable",
            CancellationToken.None);

        Assert.Equal("1.0.27", result.Version);
        Assert.Equal(
            "https://www.crossingvoid.top/manifests/launcher/android-latest.json",
            handler.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task CheckLauncherAsync_FallsBackToLatestAndroidGiteeReleaseWhenOfficialManifestIsUnauthorized()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "www.crossingvoid.top"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json("[{\"tag_name\":\"android-installer-v1.1.5\",\"prerelease\":false},{\"tag_name\":\"android-installer-v1.2.0\",\"prerelease\":false}]"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.CrossingVoidAndroid,
            "stable",
            CancellationToken.None);

        Assert.Equal("1.2.0", result.Version);
        Assert.Contains("Gitee Android Release", result.SourceName);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckGameAsync_ReadsHighestVersionFromEachPlatformDirectoryIndex()
    {
        // 下载站上每份清单按版本号命名，发布内核只留当前版本；取目录索引里版本号最高的那一份。
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("-android")
            ? Html("<a href=\"0.1.1.json\">0.1.1.json</a>")
            : Html("<a href=\"../\">../</a><a href=\"0.5.12.json\">0.5.12.json</a><a href=\"0.5.13.json\">0.5.13.json</a>"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckGameAsync("crossingvoid", CancellationToken.None);

        Assert.Equal("0.5.13", result.PcVersion);
        Assert.Equal("0.1.1", result.AndroidVersion);
        Assert.False(result.IsAligned);
        Assert.Null(result.UnifiedVersion);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            handler.Requests,
            request => request.AbsoluteUri.StartsWith(
                "https://dl.crossingvoid.top/games/crossingvoid/manifests/",
                StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.AbsoluteUri.StartsWith(
                "https://dl.crossingvoid.top/games/crossingvoid-android/manifests/",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckGameAsync_TreatsMissingPlatformDirectoryAsUnpublished()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("-android")
            ? Html("<a href=\"0.1.1.json\">0.1.1.json</a>")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckGameAsync("fantasyproject", CancellationToken.None);

        Assert.Null(result.PcVersion);
        Assert.Equal("0.1.1", result.AndroidVersion);
        Assert.False(result.IsAligned);
        Assert.Contains("还没发过", result.Status);
    }

    [Fact]
    public async Task CheckGameAsync_AlignsWhenBothPlatformsPublishSameVersion()
    {
        var handler = new StubHandler(_ =>
            Html("<a href=\"0.5.13.json\">0.5.13.json</a>"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckGameAsync("crossingvoid", CancellationToken.None);

        Assert.True(result.IsAligned);
        Assert.Equal("0.5.13", result.UnifiedVersion);
        Assert.Contains("下载站双端版本一致", result.Status);
    }

    [Fact]
    public async Task CheckLauncherAsync_ReadsPcVersionFromOfficialTauriApi()
    {
        var handler = new StubHandler(_ => Json("{\"version\":\"1.1.1\"}"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.CrossingVoidPc,
            "stable",
            CancellationToken.None);

        Assert.Equal("1.1.1", result.Version);
        Assert.Equal(
            "https://www.crossingvoid.top/manifests/launcher/pc-installer-latest.json",
            handler.Requests.Single().AbsoluteUri);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/html")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }
}
