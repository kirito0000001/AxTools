using System.Net;
using System.Text;
using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class PublishingVersionGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AxTools-VersionGate-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.1", "1.0.1", 0)]
    [InlineData("1.0.2", "1.0.1", 1)]
    [InlineData("V0.5.12.1", "V0.5.12", 1)]
    [InlineData("1.1.0-beta.2", "1.1.0-beta.1", 1)]
    public void PublishVersionComparer_OrdersSupportedReleaseVersions(
        string candidate,
        string online,
        int expectedSign)
    {
        var actual = PublishVersionComparer.Compare(candidate, online);

        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_RejectsLauncherVersionOlderThanOnline()
    {
        var viewModel = CreateAxToolsViewModel(_ => Json(
            "[{\"tag_name\":\"v1.0.1\",\"prerelease\":false}]"));
        viewModel.PublishVersion = "1.0.0";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.PackageStable,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("1.0.1", result.OnlineVersion);
        Assert.Contains("低于线上最新版本", result.Message);
        Assert.Contains("1.0.1", viewModel.LauncherPublishedVersionText);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_AllowsSameVersionForReupload()
    {
        var viewModel = CreateAxToolsViewModel(_ => Json(
            "[{\"tag_name\":\"v1.0.1\",\"prerelease\":false}]"));
        viewModel.PublishVersion = "1.0.1";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Upload,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Contains("重新打包或续传", result.Message);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_BlocksWhenOnlineCheckFails()
    {
        var viewModel = CreateAxToolsViewModel(_ =>
            throw new HttpRequestException("offline"));
        viewModel.PublishVersion = "1.0.2";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Contains("无法确认线上最新版本", result.Message);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_RejectsOlderFourPartGameVersion()
    {
        var handler = new StubHandler(_ => Json(
            "{\"latest\":{\"version\":\"V0.5.12.2\"}}"));
        var service = new PublishedVersionService(new HttpClient(handler));
        var pc = new ManagedToolPaths { GamePublishVersion = "0.5.12.1" };
        var android = new ManagedToolPaths { GamePublishVersion = "0.5.12.1" };
        var shared = new CrossingVoidGamePublishState(pc, android);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.CrossingVoidAndroid);
        var viewModel = new ToolPageViewModel(
            descriptor,
            android,
            new CrossingVoidAndroidAdapter(Path.Combine(_root, "Scripts"), new ToolchainSettings()),
            publishedVersionService: service,
            sharedGamePublishState: shared);

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.BuildGameChunks,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("V0.5.12.2", result.OnlineVersion);
    }

    private ToolPageViewModel CreateAxToolsViewModel(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var sourceRoot = Path.Combine(_root, "AxTools");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "AxTools.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(sourceRoot, "AxTools.sln"), string.Empty);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.AxTools);
        return new ToolPageViewModel(
            descriptor,
            new ManagedToolPaths { SourceRoot = sourceRoot },
            new AxToolsAdapter(Path.Combine(_root, "Scripts")),
            publishedVersionService: new PublishedVersionService(
                new HttpClient(new StubHandler(responder))));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
