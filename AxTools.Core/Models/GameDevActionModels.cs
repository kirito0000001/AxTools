namespace AxTools.Core.Models;

/// <summary>
/// 开发版卡片上的一个动作按钮。
/// 三个游戏共用同一份清单，页面不再为某个游戏单独写按钮。
/// <see cref="ProjectId"/> 由卡片构建时填（client / android），点击时用它定位是对哪个项目操作。
/// </summary>
public sealed record GameDevActionItem(
    string Key,
    string Title,
    string Glyph,
    bool IsEnabled = true,
    string ProjectId = "");

/// <summary>开发版里的一张项目卡片：一个项目 + 它对应的动作按钮。</summary>
public sealed record GameProjectCard(
    string ProjectId,
    string Title,
    IReadOnlyList<GameDevActionItem> Actions);
