using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AxTools.Views;

/// <summary>
/// 服务器页通用卡片：标题 / 说明 / 内容 / 脚注。
///
/// 版式在 CardPanel.xaml 里，调用方只负责给内容，不再自己拼 Border、StackPanel 和字号。
/// 需要新样式时改 XAML 或加设计令牌，不要在这里加视觉属性。
/// </summary>
public sealed partial class CardPanel : UserControl
{
    public CardPanel()
    {
        InitializeComponent();
        // 初始没有标题和脚注，先收起来，避免空行占位。
        RefreshHeader();
        RefreshFooter();
    }

    /// <summary>卡片标题。空字符串表示不显示标题行。</summary>
    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value ?? string.Empty;
            RefreshHeader();
        }
    }

    /// <summary>标题下方的一句说明。空字符串表示不显示。</summary>
    public string Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text = value ?? string.Empty;
            RefreshHeader();
        }
    }

    /// <summary>卡片正文。</summary>
    public object? Body
    {
        get => BodyHost.Content;
        set => BodyHost.Content = value;
    }

    /// <summary>卡片底部的补充说明。空字符串表示不显示。</summary>
    public string Footer
    {
        get => FooterText.Text;
        set
        {
            FooterText.Text = value ?? string.Empty;
            RefreshFooter();
        }
    }

    private void RefreshHeader()
    {
        var hasTitle = !string.IsNullOrWhiteSpace(TitleText.Text);
        var hasSubtitle = !string.IsNullOrWhiteSpace(SubtitleText.Text);
        // 单独收起空行：空的 TextBlock 仍然会被 StackPanel 的 Spacing 算一份间隙。
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = hasTitle || hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshFooter()
    {
        FooterText.Visibility = string.IsNullOrWhiteSpace(FooterText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
