using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

public static class ManagedToolCatalog
{
    public static IReadOnlyList<ManagedToolDescriptor> All { get; } =
    [
        new(
            ManagedToolKey.AxTools,
            "AxTools",
            "Ax管理器",
            "AxTools",
            "管理 AxTools 自身的开发、构建和发布。"),
        new(
            ManagedToolKey.CrossingVoidGame,
            "CrossingVoid",
            "游戏-零境交错",
            "CrossingVoidGame",
            "管理零境交错游戏本体的 PC 与 Android 分片、上传和版本清单。"),
        new(
            ManagedToolKey.FantasyGame,
            "FantasyGame",
            "游戏-幻杀",
            "FantasyGame",
            "幻杀游戏本体发布入口，当前先作为占位分类。"),
        new(
            ManagedToolKey.NarutoGame,
            "NarutoGame",
            "游戏-火影BP",
            "NarutoGame",
            "管理火影游戏本体的 PC 与 Android 发布。"),
        new(
            ManagedToolKey.CrossingVoidZDTool,
            "CrossingVoidZDTool",
            "工具-零境素材",
            "CrossingVoidZDTool",
            "管理 ZD空界幻境的开发、测试和正式打包。"),
        new(
            ManagedToolKey.GalExcleTools,
            "GalExcleTools",
            "工具-剧情填表",
            "GalExcleTools",
            "管理剧情工具箱的开发、检查和正式打包。"),
        new(
            ManagedToolKey.FantasyTools,
            "FantasyTools",
            "工具-幻杀卡牌",
            "FantasyTools",
            "管理幻杀工具箱的开发、构建和发布。"),
        new(
            ManagedToolKey.CrossingVoidPc,
            "CrossingVoidinitiator-PC",
            "TFAC启动平台",
            "CrossingVoidPc",
            "管理零境启动器 PC 的开发与发布。"),
        new(
            ManagedToolKey.CrossingVoidAndroid,
            "CrossingVoidinitiator-Android",
            "平台-零境-安卓",
            "CrossingVoidAndroid",
            "管理零境启动器 Android 的测试、构建与发布。"),
        new(
            ManagedToolKey.FantasyAndroid,
            "FantasyProject-Downloader-Android",
            "平台-幻杀-安卓",
            "FantasyAndroid",
            "管理幻杀启动器 Android 的开发、构建和发布。"),
        // 幻杀 PC 启动器：已不在左侧导航显示，但路径检测（ManagedToolPathDetector）、
        // 存储清理候选（StorageCatalogService）、仓库目录（ManagedRepositoryCatalog）、
        // 版本服务（PublishedVersionService）仍按 ManagedToolCatalog.All 建键取用，
        // 因此保留在目录末尾。要彻底移除需先给这些服务去掉硬引用。
        new(
            ManagedToolKey.FantasyProjectPc,
            "FantasyProject-PC",
            "幻杀启动器 PC",
            "FantasyProjectPc",
            "管理幻杀 PC 启动器的开发、测试与发布。")
    ];
}
