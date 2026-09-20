using AxTools.Core.Catalog;
using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedToolCatalogTests
{
    [Fact]
    public void All_ReturnsAllToolsInNavigationOrder()
    {
        var tools = ManagedToolCatalog.All;

        Assert.Collection(
            tools,
            tool =>
            {
                Assert.Equal(ManagedToolKey.AxTools, tool.Key);
                Assert.Equal("AxTools", tool.StableKey);
                Assert.Equal("AxTools", tool.NavigationTag);
                Assert.Equal("Ax管理器", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidGame, tool.Key);
                Assert.Equal("CrossingVoid", tool.StableKey);
                Assert.Equal("CrossingVoidGame", tool.NavigationTag);
                Assert.Equal("游戏-零境交错", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyGame, tool.Key);
                Assert.Equal("FantasyGame", tool.StableKey);
                Assert.Equal("FantasyGame", tool.NavigationTag);
                Assert.Equal("游戏-幻杀", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.NarutoGame, tool.Key);
                Assert.Equal("NarutoGame", tool.StableKey);
                Assert.Equal("NarutoGame", tool.NavigationTag);
                Assert.Equal("游戏-火影BP", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidZDTool, tool.Key);
                Assert.Equal("CrossingVoidZDTool", tool.StableKey);
                Assert.Equal("CrossingVoidZDTool", tool.NavigationTag);
                Assert.Equal("工具-零境素材", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.GalExcleTools, tool.Key);
                Assert.Equal("GalExcleTools", tool.StableKey);
                Assert.Equal("GalExcleTools", tool.NavigationTag);
                Assert.Equal("工具-剧情填表", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyTools, tool.Key);
                Assert.Equal("FantasyTools", tool.StableKey);
                Assert.Equal("FantasyTools", tool.NavigationTag);
                Assert.Equal("工具-幻杀卡牌", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidPc, tool.Key);
                Assert.Equal("CrossingVoidinitiator-PC", tool.StableKey);
                Assert.Equal("CrossingVoidPc", tool.NavigationTag);
                Assert.Equal("TFAC启动平台", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidAndroid, tool.Key);
                Assert.Equal("CrossingVoidinitiator-Android", tool.StableKey);
                Assert.Equal("CrossingVoidAndroid", tool.NavigationTag);
                Assert.Equal("平台-零境-安卓", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyAndroid, tool.Key);
                Assert.Equal("FantasyProject-Downloader-Android", tool.StableKey);
                Assert.Equal("FantasyAndroid", tool.NavigationTag);
                Assert.Equal("平台-幻杀-安卓", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyProjectPc, tool.Key);
                Assert.Equal("FantasyProject-PC", tool.StableKey);
                Assert.Equal("FantasyProjectPc", tool.NavigationTag);
                Assert.Equal("幻杀启动器 PC", tool.DisplayName);
            });
    }

    [Fact]
    public void StableKeys_AreUnique()
    {
        var keys = ManagedToolCatalog.All.Select(tool => tool.StableKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
