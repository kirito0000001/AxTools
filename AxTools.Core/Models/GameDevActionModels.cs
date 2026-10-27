namespace AxTools.Core.Models;

/// <summary>
/// 开发版卡片上的一个动作按钮。
/// 三个游戏共用同一份清单，页面不再为某个游戏单独写按钮。
/// </summary>
public sealed record GameDevActionItem(
    string Key,
    string Title,
    string Glyph,
    bool IsEnabled = true);
