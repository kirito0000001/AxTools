using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 三个游戏的工程档案。零境有本体 + 安卓启动器两个项目；
/// 幻杀、火影目前只有本体（暂时没有安卓启动器）。
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
        HasAndroidLauncherByDefault: true);

    public static GameWorkbenchProfile Fantasy { get; } = new(
        ManagedToolKey.FantasyGame,
        "幻杀",
        [
            new GameProjectDefinition(
                "client",
                "游戏本体",
                "本体项目",
                @"D:\UnrealMap\FantasyTools",
                "https://github.com/kirito0000001/FantasyProject")
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
                "https://github.com/kirito0000001/NarutoBP")
        ]);

    public static IReadOnlyList<GameWorkbenchProfile> All { get; } =
        [CrossingVoid, Fantasy, Naruto];

    public static GameWorkbenchProfile? Find(ManagedToolKey key) =>
        All.FirstOrDefault(profile => profile.GameToolKey == key);
}
