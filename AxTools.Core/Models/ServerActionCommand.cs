using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.Models;

/// <summary>
/// 发布区「服务端状态」卡片上的一个操作按钮。
/// 只描述按钮本身；能不能点由服务端状态决定（见 ToolPageViewModel 的刷新逻辑）。
/// </summary>
public sealed partial class ServerActionCommand(string key, string title, string glyph) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    [ObservableProperty]
    private bool _isEnabled = true;
}
