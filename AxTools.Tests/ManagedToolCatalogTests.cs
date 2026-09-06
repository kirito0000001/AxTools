using AxTools.Core.Catalog;
using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedToolCatalogTests
{
    [Fact]
    public void All_ReturnsSevenToolsInNavigationOrder()
    {
        var tools = ManagedToolCatalog.All;

        Assert.Collection(
            tools,
            tool =>
            {
                Assert.Equal(ManagedToolKey.AxTools, tool.Key);
                Assert.Equal("Ax工具箱", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyTools, tool.Key);
                Assert.Equal("幻杀工具箱", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.GalExcleTools, tool.Key);
                Assert.Equal("GalExcleTools", tool.StableKey);
                Assert.Equal("GalExcleTools", tool.NavigationTag);
                Assert.Equal("剧情工具箱", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidZDTool, tool.Key);
                Assert.Equal("CrossingVoidZDTool", tool.StableKey);
                Assert.Equal("CrossingVoidZDTool", tool.NavigationTag);
                Assert.Equal("ZD空界幻境", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.FantasyProjectPc, tool.Key);
                Assert.Equal("FantasyProject-PC", tool.StableKey);
                Assert.Equal("FantasyProjectPc", tool.NavigationTag);
                Assert.Equal("幻杀启动器 PC", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidPc, tool.Key);
                Assert.Equal("CrossingVoidinitiator-PC", tool.StableKey);
                Assert.Equal("零境启动器 PC", tool.DisplayName);
            },
            tool =>
            {
                Assert.Equal(ManagedToolKey.CrossingVoidAndroid, tool.Key);
                Assert.Equal("CrossingVoidinitiator-Android", tool.StableKey);
                Assert.Equal("CrossingVoidAndroid", tool.NavigationTag);
                Assert.Equal("零境启动器 Android", tool.DisplayName);
            });
    }

    [Fact]
    public void StableKeys_AreUnique()
    {
        var keys = ManagedToolCatalog.All.Select(tool => tool.StableKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
