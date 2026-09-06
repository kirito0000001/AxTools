using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_GitHubStable_IgnoresPrerelease()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("releases"))
            {
                return Json("[{\"tag_name\":\"v1.1.0-beta.1\",\"prerelease\":true,\"assets\":[{\"name\":\"toolbox-update.json\",\"browser_download_url\":\"https://assets/beta.json\"}]},{\"tag_name\":\"v1.0.1\",\"prerelease\":false,\"assets\":[{\"name\":\"toolbox-update.json\",\"browser_download_url\":\"https://assets/stable.json\"}]}]");
            }

            return Json(Manifest("1.0.1", "stable", "AxTools-v1.0.1-win-x64.zip", "abc", 3));
        });
        var service = new UpdateService(new HttpClient(handler));

        var result = await service.CheckAsync(UpdateSource.GitHub, UpdateChannel.Stable, "1.0.0", CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.0.1", result.Release!.Version);
        Assert.Contains("api.github.com/repos/kirito0000001/AxTools", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task CheckAsync_GiteeBeta_SelectsHighestSemanticVersion()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("releases")
            ? Json("[{\"tag_name\":\"v1.0.1-beta.2\",\"prerelease\":true,\"attach_files\":[{\"name\":\"toolbox-update.json\",\"browser_download_url\":\"https://assets/b2.json\"}]},{\"tag_name\":\"v1.0.1-beta.10\",\"prerelease\":true,\"attach_files\":[{\"name\":\"toolbox-update.json\",\"browser_download_url\":\"https://assets/b10.json\"}]}]")
            : Json(Manifest("1.0.1-beta.10", "beta", "AxTools-beta.zip", "abc", 3)));
        var service = new UpdateService(new HttpClient(handler));

        var result = await service.CheckAsync(UpdateSource.Gitee, UpdateChannel.Beta, "1.0.0", CancellationToken.None);

        Assert.Equal("1.0.1-beta.10", result.Release!.Version);
        Assert.Contains("gitee.com/api/v5/repos/xiaojie578/AxTools", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task DownloadAsync_UsesRangeOnlyWhenPartialFileExists()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent("def"u8.ToArray()) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "package.zip");
        await File.WriteAllTextAsync(destination + ".download", "abc");
        try
        {
            var service = new UpdateService(new HttpClient(handler));
            await service.DownloadAsync(new UpdateAsset("win-x64", "package.zip", "https://assets/package.zip", "", 6), destination, CancellationToken.None);
            Assert.Equal("abcdef", await File.ReadAllTextAsync(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoresRange_RestartsFromZero()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("new"u8.ToArray()) });
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "package.zip");
        await File.WriteAllTextAsync(destination + ".download", "old");
        try
        {
            var service = new UpdateService(new HttpClient(handler));
            await service.DownloadAsync(new UpdateAsset("win-x64", "package.zip", "https://assets/package.zip", "", 3), destination, CancellationToken.None);
            Assert.Equal("new", await File.ReadAllTextAsync(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string Manifest(string version, string channel, string fileName, string sha, long size) =>
        $$"""{"schemaVersion":1,"toolboxStableKey":"AxTools","version":"{{version}}","channel":"{{channel}}","assets":[{"runtime":"win-x64","fileName":"{{fileName}}","sha256":"{{sha}}","sizeBytes":{{size}}}]}""";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }
}
