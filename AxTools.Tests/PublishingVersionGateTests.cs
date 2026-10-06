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
    public async Task ValidatePublishingVersionAsync_AllowsWhenOnlineCheckFails()
    {
        // 2026-10-05 用户实测踩到：本机 IP 被 Gitee 匿名配额限流 → 线上版本读不到 →
        // 「出更新包 / 发布演练 / 完整发布」全被这条门禁挡住，**连纯本地的出包都点不动**。
        // 改成读不到就放行（与游戏包发布那条路一致），兜底交给脚本侧：
        // 版本倒退的本地断言（PC 侧 Assert-CrossingVersionConfigured）、
        // 发布脚本的幂等（同名同大小**且内容一致**才跳过）、清单最后生效、发布后匿名自检。
        var viewModel = CreateAxToolsViewModel(_ =>
            throw new HttpRequestException("offline"));
        viewModel.PublishVersion = "1.0.2";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Contains("未能确认线上版本", result.Message);
        Assert.Contains("先放行", result.Message);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_StillBlocksOlderVersionWhenOnlineIsReadable()
    {
        // 放行只针对"读不到"；能读到线上版本时，倒退依然要被拦住 —— 这条是门禁的本意，不能一起松掉。
        var viewModel = CreateAxToolsViewModel(_ => Json(
            "[{\"tag_name\":\"v1.0.9\",\"prerelease\":false}]"));
        viewModel.PublishVersion = "1.0.2";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Contains("低于线上最新版本", result.Message);
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

    [Fact]
    public async Task CheckLauncherAsync_PrefersZDToolboxLatestManifestAndNeverCallsGiteeApi()
    {
        // 2026-10-06 实测：Gitee 匿名 API 只有 60 次/小时、按 IP 计，被发布动作与客户端探针打光之后
        // 「线上当前版本」永远是 403 检查失败。所以零境素材的第一来源改成**普通文件下载**
        // （releases/download/latest/toolbox-update.json，302 → CDN，没有配额这回事），
        // 和安卓启动器读官网清单是同一条思路。
        var urls = new List<string>();
        var service = new PublishedVersionService(new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            urls.Add(uri);
            return uri.Contains("/releases/download/latest/", StringComparison.Ordinal)
                ? Json("{\"version\":\"2.0.3\",\"channel\":\"stable\"," +
                    "\"toolboxStableKey\":\"CrossingVoidZDTool\",\"publishedAt\":\"2026-10-06T12:33:13+08:00\"}")
                : throw new HttpRequestException("不应该走到 Gitee API");
        })));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.CrossingVoidZDTool,
            "stable",
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("2.0.3", result.Version);
        Assert.DoesNotContain(urls, url => url.Contains("/api/v5/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckLauncherAsync_FallsBackToGiteeReleaseApiWhenZDToolboxManifestIsAnotherChannel()
    {
        // latest 指的是"最新那条 Release"，未必是本次要查的通道：清单里 channel=beta 而这次查 stable 时，
        // 必须回落 API 由它按 prerelease 精确筛选，不能把 beta 的版本号当成 stable 的线上版本。
        var urls = new List<string>();
        var service = new PublishedVersionService(new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            urls.Add(uri);
            return uri.Contains("/releases/download/latest/", StringComparison.Ordinal)
                ? Json("{\"version\":\"2.1.0-Beta\",\"channel\":\"beta\"," +
                    "\"toolboxStableKey\":\"CrossingVoidZDTool\"}")
                : Json("[{\"tag_name\":\"v2.0.3\",\"prerelease\":false}]");
        })));

        var result = await service.CheckLauncherAsync(
            ManagedToolKey.CrossingVoidZDTool,
            "stable",
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("2.0.3", result.Version);
        Assert.Contains(urls, url =>
            url.Contains("/api/v5/repos/xiaojie578/CrossingVoidZDTool/releases", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_AllowsZDToolboxPublishWhenGiteeRateLimits()
    {
        // 用户实测那条：Gitee 匿名限流（403 + Rate Limit Exceeded）时「出更新包 / 发布演练 / 完整发布」
        // 一个都不能被挡住 —— 门禁只防"确认过的版本倒退"。
        var viewModel = CreateZDToolboxViewModel(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("403 Forbidden (Rate Limit Exceeded)", Encoding.UTF8, "text/plain")
        });
        viewModel.PublishVersion = "2.0.4";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Contains("未能确认线上版本", result.Message);
        Assert.Contains("限流", viewModel.LauncherVersionCheckStatus);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_StillBlocksZDToolboxDowngradeWhenVersionIsReadable()
    {
        // 松开的只是"读不到"；读得到线上版本时，倒退发布依然要被拦住。
        var viewModel = CreateZDToolboxViewModel(request =>
            request.RequestUri!.AbsoluteUri.Contains("/releases/download/latest/", StringComparison.Ordinal)
                ? Json("{\"version\":\"2.0.3\",\"channel\":\"stable\"}")
                : throw new HttpRequestException("不应该走到 Gitee API"));
        viewModel.PublishVersion = "2.0.2";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("2.0.3", result.OnlineVersion);
        Assert.Contains("低于线上最新版本", result.Message);
    }

    [Fact]
    public async Task ValidatePublishingVersionAsync_AllowsZDToolboxWhenOnlineVersionTextIsUnparsable()
    {
        // 线上版本号读不出来属于"检查本身有问题"，不该把发布挡死；本地版本号写错才拦。
        var viewModel = CreateZDToolboxViewModel(
            _ => Json("{\"version\":\"vNext\",\"channel\":\"stable\"}"));
        viewModel.PublishVersion = "2.0.4";

        var result = await viewModel.ValidatePublishingVersionAsync(
            ManagedToolAction.Publish,
            CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Contains("无法解析", result.Message);
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

    private ToolPageViewModel CreateZDToolboxViewModel(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var sourceRoot = Path.Combine(_root, "CrossingVoidZDTool");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "CrossingVoidZDTool.csproj"), string.Empty);
        var descriptor = ManagedToolCatalog.All.Single(item =>
            item.Key == ManagedToolKey.CrossingVoidZDTool);
        return new ToolPageViewModel(
            descriptor,
            new ManagedToolPaths { SourceRoot = sourceRoot },
            new CrossingVoidZDToolAdapter(Path.Combine(_root, "Scripts")),
            pendingPackageAvailable: () => false,
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
