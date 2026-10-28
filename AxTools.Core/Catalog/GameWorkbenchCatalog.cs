using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 三个游戏的工程档案。每个游戏都声明「本体 + 安卓启动器」两个项目；
/// 幻杀、火影目前没有安卓启动器，路径留空、默认关闭，等有了再补。
///
/// 页面、状态判断、以后的项目操作全部读这里，不再出现“零境专用”的分支；
/// 加游戏或给某个游戏补项目，只改这个目录。
/// </summary>
public static class GameWorkbenchCatalog
{
    public static GameWorkbenchProfile CrossingVoid { get; } = new(
        ManagedToolKey.CrossingVoidGame,
        "零境交错",
        [
            new GameProjectDefinition(
                "client",
                "游戏本体",
                "本体项目",
                @"C:\CrossingVoid",
                "https://github.com/kirito0000001/CrossingVoid"),
            new GameProjectDefinition(
                "android",
                "安卓启动器",
                "启动器",
                @"D:\UnrealMap\CrossingVoidinitiator-Android",
                "https://github.com/kirito0000001/CrossingVoid-Downloader-Android",
                IsAndroidLauncher: true)
        ],
        HasAndroidLauncherByDefault: true,
        HasAndroidGameBuildByDefault: true);

    public static GameWorkbenchProfile Fantasy { get; } = new(
        ManagedToolKey.FantasyGame,
        "幻杀",
        [
            new GameProjectDefinition(
                "client",
                "游戏本体",
                "本体项目",
                @"D:\UnrealMap\FantasyTools",
                "https://github.com/kirito0000001/FantasyProject"),
            // 暂时没有安卓启动器：项目先占位，路径为空。
            // 设置页把「是否拥有安卓启动器」打开后，这里会因为缺路径而报“缺少：安卓启动器路径”。
            new GameProjectDefinition(
                "android",
                "安卓启动器",
                "启动器",
                string.Empty,
                string.Empty,
                IsAndroidLauncher: true)
        ]);

    public static GameWorkbenchProfile Naruto { get; } = new(
        ManagedToolKey.NarutoGame,
        "火影BP",
        [
            new GameProjectDefinition(
                "client",
                "游戏本体",
                "本体项目",
                @"D:\UnrealMap\NarutoBP",
                "https://github.com/kirito0000001/NarutoBP"),
            new GameProjectDefinition(
                "android",
                "安卓启动器",
                "启动器",
                string.Empty,
                string.Empty,
                IsAndroidLauncher: true)
        ]);

    public static IReadOnlyList<GameWorkbenchProfile> All { get; } =
        [CrossingVoid, Fantasy, Naruto];

    public static GameWorkbenchProfile? Find(ManagedToolKey key) =>
        All.FirstOrDefault(profile => profile.GameToolKey == key);
}
