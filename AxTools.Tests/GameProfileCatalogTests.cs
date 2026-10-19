using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 游戏档案的守卫：零境/幻杀的工具页、产品段、标记名、旁车名和包名前缀必须各归各的，
/// 不能再让碎片算法靠游戏名字面量猜。
/// </summary>
public sealed class GameProfileCatalogTests
{
    [Fact]
    public void MapsBothGameLauncherPagesToTheirProfiles()
    {
        Assert.Same(
            GameProfileCatalog.CrossingVoid,
            GameProfileCatalog.FindByToolKey(ManagedToolKey.CrossingVoidPc));
        Assert.Same(
            GameProfileCatalog.CrossingVoid,
            GameProfileCatalog.FindByToolKey(ManagedToolKey.CrossingVoidAndroid));
        Assert.Same(
            GameProfileCatalog.Fantasy,
            GameProfileCatalog.FindByToolKey(ManagedToolKey.FantasyProjectPc));
        Assert.Same(
            GameProfileCatalog.Fantasy,
            GameProfileCatalog.FindByToolKey(ManagedToolKey.FantasyAndroid));

        Assert.Null(GameProfileCatalog.FindByToolKey(ManagedToolKey.FantasyTools));
        Assert.Null(GameProfileCatalog.FindByToolKey(ManagedToolKey.CrossingVoidGame));
    }

    [Fact]
    public void MapsTheGamePublishToolsToTheirProfiles()
    {
        Assert.Same(
            GameProfileCatalog.CrossingVoid,
            GameProfileCatalog.FindByGameToolKey(ManagedToolKey.CrossingVoidGame));
        Assert.Same(
            GameProfileCatalog.Fantasy,
            GameProfileCatalog.FindByGameToolKey(ManagedToolKey.FantasyGame));
        Assert.Null(GameProfileCatalog.FindByGameToolKey(ManagedToolKey.NarutoGame));
    }

    [Fact]
    public void KeepsMarkerAndSidecarNamesPerGame()
    {
        Assert.Equal("CrossingVoid.version.json", GameProfileCatalog.CrossingVoid.VersionMarkerFileName);
        Assert.Equal("CrossingVoid.obb.json", GameProfileCatalog.CrossingVoid.ObbSidecarFileName);
        Assert.Equal("FantasyProject.version.json", GameProfileCatalog.Fantasy.VersionMarkerFileName);
        Assert.Equal("FantasyProject.obb.json", GameProfileCatalog.Fantasy.ObbSidecarFileName);

        Assert.Equal("crossingvoid-android", GameProfileCatalog.CrossingVoid.ProductSegmentFor(android: true));
        Assert.Equal("fantasyproject-android", GameProfileCatalog.Fantasy.ProductSegmentFor(android: true));
        Assert.Equal("fantasyproject", GameProfileCatalog.Fantasy.ProductSegmentFor(android: false));
    }

    [Theory]
    [InlineData("0.6.0", "PC", "零境", "零境V0.6PC")]
    [InlineData("0.6.0", "安卓", "零境", "零境V0.6安卓")]
    [InlineData("0.1.0", "PC", "幻杀", "幻杀V0.1PC")]
    [InlineData("0.1.0", "安卓", "幻杀", "幻杀V0.1安卓")]
    public void BuildsArchiveNamePrefixPerGame(
        string version,
        string platformLabel,
        string shortName,
        string expected) =>
        Assert.Equal(
            expected,
            CrossingVoidFragmentPlanner.BuildArchiveNamePrefix(version, platformLabel, shortName));
}
