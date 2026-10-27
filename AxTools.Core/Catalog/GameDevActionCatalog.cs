using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 开发版卡片上的动作清单（项目本体）。
///
/// 顺序即界面顺序；<see cref="GameDevActionItem.IsEnabled"/> 为 false 的按钮显示但不可点
/// （例如「清理项目缓存」当前还没接入，先占位）。
/// 以后安卓启动器要用同一套按钮时，在这里再加一份清单，不要在页面里复制。
/// </summary>
public static class GameDevActionCatalog
{
    public static IReadOnlyList<GameDevActionItem> ClientActions { get; } =
    [
        new("install-from-github", "从 GitHub 安装项目", "\uE896"),
        new("copy-repo-link", "获取项目链接", "\uE71B"),
        new("launch-uproject", "从 UProject 启动", "\uE768"),
        new("launch-rider", "从 Rider 启动", "\uE7B8"),
        new("clean-cache", "清理项目缓存", "\uE74D", IsEnabled: false),
        new("check-upstream", "检查上游并更新", "\uE895")
    ];
}
