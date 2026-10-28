using AxTools.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AxTools;

public sealed partial class MainWindow
{
    private const double PageEntranceOffsetX = -96;
    private static readonly TimeSpan PageEntranceDuration = TimeSpan.FromMilliseconds(280);

    private void ShellNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ShowPageByTag(tag, playAnimation: true);
        }
    }

    private void ShowPageByTag(string tag, bool playAnimation)
    {
        // 2026-10-27：左栏固定 8 项——AX管理器 / 三个工具 / TFAC / 三个游戏。
        // 「服务器」与两个安卓页已从左栏移除，但页面实例仍留在 PageHost：
        // 旧配置里存着这些页签时统一落回 AX管理器，等后续把功能迁走再删页面。
        var normalizedTag = tag switch
        {
            "AxTools" or "CrossingVoidZDTool" or "GalExcleTools" or "FantasyTools" or
                "CrossingVoidPc" or "CrossingVoidGame" or "FantasyGame" or "NarutoGame" or
                "Settings" => tag,
            _ => "AxTools"
        };
        FrameworkElement visiblePage = normalizedTag switch
        {
            "FantasyTools" => FantasyToolsPage,
            "GalExcleTools" => GalExcleToolsPage,
            "CrossingVoidZDTool" => CrossingVoidZDToolPage,
            "CrossingVoidPc" => CrossingVoidPcPage,
            "CrossingVoidGame" => CrossingVoidGamePage,
            "FantasyGame" => FantasyGamePage,
            "NarutoGame" => NarutoGamePage,
            "Settings" => GlobalSettingsPage,
            _ => AxToolsPage
        };

        AxToolsPage.Visibility = visiblePage == AxToolsPage ? Visibility.Visible : Visibility.Collapsed;
        FantasyToolsPage.Visibility = visiblePage == FantasyToolsPage ? Visibility.Visible : Visibility.Collapsed;
        GalExcleToolsPage.Visibility = visiblePage == GalExcleToolsPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidZDToolPage.Visibility = visiblePage == CrossingVoidZDToolPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidPcPage.Visibility = visiblePage == CrossingVoidPcPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidGamePage.Visibility = visiblePage == CrossingVoidGamePage ? Visibility.Visible : Visibility.Collapsed;
        FantasyGamePage.Visibility = visiblePage == FantasyGamePage ? Visibility.Visible : Visibility.Collapsed;
        NarutoGamePage.Visibility = visiblePage == NarutoGamePage ? Visibility.Visible : Visibility.Collapsed;
        // 下面三页已无左栏入口，恒为 Collapsed；保留赋值是为了将来恢复入口时不用再想状态。
        CrossingVoidAndroidPage.Visibility = visiblePage == CrossingVoidAndroidPage ? Visibility.Visible : Visibility.Collapsed;
        FantasyAndroidPage.Visibility = visiblePage == FantasyAndroidPage ? Visibility.Visible : Visibility.Collapsed;
        GlobalSettingsPage.Visibility = visiblePage == GlobalSettingsPage ? Visibility.Visible : Visibility.Collapsed;

        // 游戏页只有发布动作，首次进入时落到「打包与发布」；否则开局停在空的开发版面板。
        if (visiblePage is ToolPage gamePage &&
            normalizedTag is "CrossingVoidGame" or "FantasyGame" or "NarutoGame")
        {
            gamePage.EnsurePublishMode();
        }

        _viewModel.CurrentPageTag = normalizedTag;
        ShellNavigation.SelectedItem = normalizedTag switch
        {
            "FantasyTools" => FantasyToolsNavItem,
            "GalExcleTools" => GalExcleToolsNavItem,
            "CrossingVoidZDTool" => CrossingVoidZDToolNavItem,
            "CrossingVoidPc" => CrossingVoidPcNavItem,
            "CrossingVoidGame" => CrossingVoidGameNavItem,
            "FantasyGame" => FantasyGameNavItem,
            "NarutoGame" => NarutoGameNavItem,
            "Settings" => SettingsNavItem,
            _ => AxToolsNavItem
        };

        if (playAnimation)
        {
            PlayPageEntrance(visiblePage);
        }
    }

    private static void PlayPageEntrance(FrameworkElement page)
    {
        if (page.Visibility != Visibility.Visible)
        {
            return;
        }

        var transform = page.RenderTransform as TranslateTransform ?? new TranslateTransform();
        page.RenderTransform = transform;
        transform.X = PageEntranceOffsetX;
        transform.Y = 0;
        page.Opacity = 0.82;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slideAnimation = new DoubleAnimation
        {
            From = PageEntranceOffsetX,
            To = 0,
            Duration = PageEntranceDuration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(slideAnimation, transform);
        Storyboard.SetTargetProperty(slideAnimation, nameof(TranslateTransform.X));

        var fadeAnimation = new DoubleAnimation
        {
            From = 0.82,
            To = 1,
            Duration = PageEntranceDuration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(fadeAnimation, page);
        Storyboard.SetTargetProperty(fadeAnimation, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(slideAnimation);
        storyboard.Children.Add(fadeAnimation);
        storyboard.Completed += (_, _) =>
        {
            transform.X = 0;
            transform.Y = 0;
            page.Opacity = 1;
        };
        storyboard.Begin();
    }
}
