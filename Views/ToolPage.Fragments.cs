using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AxTools.Views;

/// <summary>
/// 碎片区的界面事件（生成 / 发布 / 选输出目录）。
///
/// **和 `ToolPage.xaml.cs` 分开**：ToolPage 是通用控件（开发、发布、存储几块都在里面），
/// 碎片只是其中一块；以后加幻杀/火影的碎片入口时，只动这个文件。
/// </summary>
public sealed partial class ToolPage
{
    /// <summary>点《碎片生成》。（零境的 PC 页和安卓页各有一个入口，走同一套事件。）</summary>
    public event EventHandler? FragmentGenerationRequested;

    /// <summary>点《发布碎片清单》（把 fragments.json 传到下载站）。</summary>
    public event EventHandler? FragmentManifestPublishRequested;

    /// <summary>点碎片区里那个"选择文件夹"（碎片输出目录）。</summary>
    public event EventHandler<FragmentPathRequestedEventArgs>? FragmentPathSelectionRequested;

    private void GenerateFragments_Click(object sender, RoutedEventArgs e) =>
        FragmentGenerationRequested?.Invoke(this, EventArgs.Empty);

    private void PublishFragmentManifest_Click(object sender, RoutedEventArgs e) =>
        FragmentManifestPublishRequested?.Invoke(this, EventArgs.Empty);

    private void FragmentPathBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pathKind })
        {
            FragmentPathSelectionRequested?.Invoke(this, new FragmentPathRequestedEventArgs(pathKind));
        }
    }
}

/// <summary>碎片区里点的是哪个"选择文件夹"（目前只有 OutputDirectory）。</summary>
public sealed class FragmentPathRequestedEventArgs(string pathKind) : EventArgs
{
    public string PathKind { get; } = pathKind;
}
