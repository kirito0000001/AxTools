using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class GamePublishRequestBuilderTests
{
    [Fact]
    public void Build_RejectsPatchOnAndroid()
    {
        var paths = new ManagedToolPaths
        {
            GamePublishPlatform = "Android",
            GamePackageRootAndroid = @"D:\Packages\Android",
            GamePackageRoot = @"D:\Packages\PC"
        };

        var built = GamePublishRequestBuilder.TryBuild(
            ManagedToolKey.FantasyGame,
            paths,
            ManagedToolAction.PublishGamePatch,
            string.Empty,
            out _,
            out var error);

        Assert.False(built);
        Assert.Contains("补丁", error);
    }

    [Fact]
    public void Build_ReportsMissingPackageRootPerPlatform()
    {
        var androidPaths = new ManagedToolPaths { GamePublishPlatform = "Android" };
        Assert.False(GamePublishRequestBuilder.TryBuild(
            ManagedToolKey.FantasyGame, androidPaths, ManagedToolAction.PublishGameVersion,
            string.Empty, out _, out var androidError));
        Assert.Contains("安卓", androidError);

        var pcPaths = new ManagedToolPaths { GamePublishPlatform = "Windows" };
        Assert.False(GamePublishRequestBuilder.TryBuild(
            ManagedToolKey.FantasyGame, pcPaths, ManagedToolAction.PublishGameVersion,
            string.Empty, out _, out var pcError));
        Assert.Contains("PC", pcError);
    }

    [Fact]
    public void Build_CarriesPlatformVersionAndChannelIntoSummary()
    {
        var paths = new ManagedToolPaths
        {
            GamePublishPlatform = "Windows",
            GamePackageRoot = @"D:\Packages\PC",
            GamePublishVersion = "1.2.3",
            GamePublishChannel = "beta"
        };

        Assert.True(GamePublishRequestBuilder.TryBuild(
            ManagedToolKey.CrossingVoidGame, paths, ManagedToolAction.PublishGameVersion,
            "说明", out var request, out _));
        Assert.Equal("Windows", request.Platform);
        Assert.Equal("1.2.3", request.Version);
        Assert.Equal("beta", request.Channel);
        Assert.False(request.IsPatch);
        Assert.Contains("1.2.3", request.Summary);
        Assert.Contains("通道 beta", request.Summary);
    }
}
