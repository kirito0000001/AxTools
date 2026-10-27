using Microsoft.UI.Xaml.Controls;

namespace AxTools.Views;

/// <summary>
/// 游戏页通用面板。三个游戏共用，数据由父级 DataContext（ToolPageViewModel）提供。
/// 这里只负责呈现，不判断“哪个游戏”。
/// </summary>
public sealed partial class GameWorkbenchPanel : UserControl
{
    public GameWorkbenchPanel()
    {
        InitializeComponent();
    }
}
