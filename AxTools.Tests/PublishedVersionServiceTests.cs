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
    public async Task CheckCrossingVoidGameAsync_RequiresMatchingPcAndAndroidVersions()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("windows")
            ? Json("{\"latest\":{\"version\":\"V0.5.12\"}}")
            : Json("{\"latest\":{\"version\":\"V0.5.13\"}}"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckCrossingVoidGameAsync(
            "stable",
            CancellationToken.None);

        Assert.False(result.IsAligned);
        Assert.Equal("V0.5.12", result.PcVersion);
        Assert.Equal("V0.5.13", result.AndroidVersion);
        Assert.Null(result.UnifiedVersion);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.StartsWith("https://www.crossingvoid.top/manifests/game/", request.AbsoluteUri));
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
            "https://www.crossingvoid.top/api/toolbox-updates/tauri/crossingvoid-launcher-pc/windows/x86_64/0.0.0",
            handler.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task CheckCrossingVoidGameAsync_ReturnsUnifiedVersionWhenBothMatch()
    {
        var handler = new StubHandler(_ => Json(
            "{\"latest\":{\"version\":\"V0.5.12\"}}"));
        var service = new PublishedVersionService(new HttpClient(handler));

        var result = await service.CheckCrossingVoidGameAsync(
            "stable",
            CancellationToken.None);

        Assert.True(result.IsAligned);
        Assert.Equal("V0.5.12", result.UnifiedVersion);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
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
