using AxTools.Core.Catalog;
using Xunit;

namespace AxTools.Tests;

public sealed class GameWorkbenchCatalogTests
{
    [Fact]
    public void EveryGameDeclaresExactlyOneAndroidLauncherProject()
    {
        // 踩过：幻杀缺安卓项目定义，设置页把开关打开后没有项目可查，仍然显示“已就绪”。
        foreach (var profile in GameWorkbenchCatalog.All)
        {
            Assert.Single(profile.Projects.Where(project => project.IsAndroidLauncher));
        }
    }

    [Fact]
    public void EveryGameHasAClientProjectWithPathAndRepository()
    {
        foreach (var profile in GameWorkbenchCatalog.All)
        {
            var client = Assert.Single(profile.Projects.Where(project => !project.IsAndroidLauncher));
            Assert.False(
                string.IsNullOrWhiteSpace(client.DefaultPath),
                $"{profile.DisplayName} 缺少本体项目路径。");
            Assert.False(
                string.IsNullOrWhiteSpace(client.RepositoryUrl),
                $"{profile.DisplayName} 缺少本体仓库地址。");
        }
    }

    [Fact]
    public void OnlyCrossingVoidHasAndroidLauncherEnabledByDefault()
    {
        Assert.True(GameWorkbenchCatalog.CrossingVoid.HasAndroidLauncherByDefault);
        Assert.False(GameWorkbenchCatalog.Fantasy.HasAndroidLauncherByDefault);
        Assert.False(GameWorkbenchCatalog.Naruto.HasAndroidLauncherByDefault);
    }

    [Fact]
    public void DevelopmentCardListsTheSixProjectActions()
    {
        Assert.Equal(
            [
                "install-from-github",
                "copy-repo-link",
                "launch-uproject",
                "launch-rider",
                "clean-cache",
                "check-upstream"
            ],
            GameDevActionCatalog.ClientActions.Select(action => action.Key));

        // 清理项目缓存还没接入：按钮要在，但不能点。
        var cleanCache = GameDevActionCatalog.ClientActions.Single(action => action.Key == "clean-cache");
        Assert.False(cleanCache.IsEnabled);
    }
}
