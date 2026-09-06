using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

public static class ManagedToolCatalog
{
    public static IReadOnlyList<ManagedToolDescriptor> All { get; } =
    [
        new(
            ManagedToolKey.AxTools,
            "AxTools",
            "Ax工具箱",
            "AxTools",
            "管理 AxTools 自身的开发、构建和发布。"),
        new(
            ManagedToolKey.FantasyTools,
            "FantasyTools",
            "幻杀工具箱",
            "FantasyTools",
            "管理幻杀工具箱的开发、构建和发布。"),
        new(
            ManagedToolKey.GalExcleTools,
            "GalExcleTools",
            "剧情工具箱",
            "GalExcleTools",
            "管理剧情工具箱的开发、检查和正式打包。"),
        new(
            ManagedToolKey.CrossingVoidZDTool,
            "CrossingVoidZDTool",
            "ZD空界幻境",
            "CrossingVoidZDTool",
            "管理 ZD空界幻境的开发、测试和正式打包。"),
        new(
            ManagedToolKey.FantasyProjectPc,
            "FantasyProject-PC",
            "幻杀启动器 PC",
            "FantasyProjectPc",
            "管理幻杀 PC 启动器的开发、测试与发布。"),
        new(
            ManagedToolKey.CrossingVoidPc,
            "CrossingVoidinitiator-PC",
            "零境启动器 PC",
            "CrossingVoidPc",
            "管理零境启动器 PC 的开发与发布。"),
        new(
            ManagedToolKey.CrossingVoidAndroid,
            "CrossingVoidinitiator-Android",
            "零境启动器 Android",
            "CrossingVoidAndroid",
            "管理零境启动器 Android 的测试、构建与发布。"),
        new(
            ManagedToolKey.FantasyGame,
            "FantasyGame",
            "幻杀",
            "FantasyGame",
            "幻杀游戏本体发布入口，当前先作为占位分类。"),
        new(
            ManagedToolKey.FantasyAndroid,
            "FantasyProject-Downloader-Android",
            "幻杀启动器-安卓",
            "FantasyAndroid",
            "管理幻杀启动器 Android 的开发、构建和发布。"),
        new(
            ManagedToolKey.CrossingVoidGame,
            "CrossingVoid",
            "零境交错：空界幻境",
            "CrossingVoidGame",
            "管理零境交错游戏本体的 PC 与 Android 分片、上传和版本清单。")
    ];
}
