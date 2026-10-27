using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 三个游戏的工程档案：每个游戏两个项目（本体 + 安卓启动器）。
///
/// 页面、状态判断、以后的项目操作全部读这里，不再出现“零境专用”的分支；
/// 加第四个游戏只要在 <see cref="All"/> 里补一条。
/// 幻杀 / 火影的本地路径与仓库还没给，先留空，界面显示“未配置”。
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
                "https://github.com/kirito0000001/CrossingVoid-Downloader-Android")
        ]);

    public static GameWorkbenchProfile Fantasy { get; } = new(
        ManagedToolKey.FantasyGame,
        "幻杀",
        [
            new GameProjectDefinition("client", "游戏本体", "本体项目", string.Empty, string.Empty),
            new GameProjectDefinition("android", "安卓启动器", "启动器", string.Empty, string.Empty)
        ]);

    public static GameWorkbenchProfile Naruto { get; } = new(
        ManagedToolKey.NarutoGame,
        "火影BP",
        [
            new GameProjectDefinition("client", "游戏本体", "本体项目", string.Empty, string.Empty),
            new GameProjectDefinition("android", "安卓启动器", "启动器", string.Empty, string.Empty)
        ]);

    public static IReadOnlyList<GameWorkbenchProfile> All { get; } =
        [CrossingVoid, Fantasy, Naruto];

    public static GameWorkbenchProfile? Find(ManagedToolKey key) =>
        All.FirstOrDefault(profile => profile.GameToolKey == key);
}
