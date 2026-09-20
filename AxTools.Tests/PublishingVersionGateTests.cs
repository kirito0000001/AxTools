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
    public async Task ValidatePublishingVersionAsync_RejectsOlderGameVersionThanCurrentPlatform()
    {
        var handler = new StubHandler(_ =>
            Html("<a href=\"0.5.12.2.json\">0.5.12.2.json</a>"));
        var service = new PublishedVersionService(new HttpClient(handler));
        var packageRoot = Path.Combine(_root, "GamePackage");
        Directory.CreateDirectory(packageRoot);
        var gamePaths = new ManagedToolPaths
        {
            GamePackageRoot = packageRoot,
            GamePublishVersion = "0.5.12.1"
        };
        var state = new CrossingVoidGamePublishState(gamePaths, gamePaths);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.CrossingVoidGame);
        var viewModel = new ToolPageViewModel(
            descriptor,
            gamePaths,
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")),
            publishedVersionService: service,
            gamePublishState: state);

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.PublishGameVersion,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("0.5.12.2", result.OnlineVersion);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_AllowsFirstPcPublishWhenOnlyAndroidIsOnline()
    {
        // PC 还没发过、安卓已经有 0.5.13：PC 首次发布不该被"另一个平台的版本"拦住。
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("-android")
            ? Html("<a href=\"0.5.13.json\">0.5.13.json</a>")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new PublishedVersionService(new HttpClient(handler));
        var packageRoot = Path.Combine(_root, "GamePackage");
        Directory.CreateDirectory(packageRoot);
        var gamePaths = new ManagedToolPaths
        {
            GamePackageRoot = packageRoot,
            GamePublishVersion = "0.5.13"
        };
        var state = new CrossingVoidGamePublishState(gamePaths, gamePaths);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.CrossingVoidGame);
        var viewModel = new ToolPageViewModel(
            descriptor,
            gamePaths,
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")),
            publishedVersionService: service,
            gamePublishState: state);

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.PublishGameVersion,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_AllowsGamePublishWhenOnlineCheckFails()
    {
        // 网络/代理抖动读不到下载站时不再拦死；发布内核推完还会校验公网清单。
        var service = new PublishedVersionService(new HttpClient(new StubHandler(
            _ => throw new HttpRequestException("offline"))));
        var packageRoot = Path.Combine(_root, "GamePackage");
        Directory.CreateDirectory(packageRoot);
        var gamePaths = new ManagedToolPaths
        {
            GamePackageRoot = packageRoot,
            GamePublishVersion = "0.5.14"
        };
        var state = new CrossingVoidGamePublishState(gamePaths, gamePaths);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.CrossingVoidGame);
        var viewModel = new ToolPageViewModel(
            descriptor,
            gamePaths,
            new CrossingVoidGameAdapter(Path.Combine(_root, "Scripts")),
            publishedVersionService: service,
            gamePublishState: state);

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.PublishGameVersion,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Contains("未能确认线上版本", result.Message);
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

    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/html")
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
