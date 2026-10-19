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
        // 2026-09-20：三个游戏包发布页并进了「服务器」页的游戏页签，旧的导航标识统一回到服务器分区
        // （设置里存着旧页签时，重开也会落在服务器页，而不是弹回 Ax管理器）。
        var normalizedTag = tag switch
        {
            "CrossingVoidGame" or "FantasyGame" or "NarutoGame" => "ServerAliyun",
            "FantasyTools" or "GalExcleTools" or "CrossingVoidZDTool" or "CrossingVoidPc" or
                "CrossingVoidAndroid" or "FantasyProjectPc" or "FantasyAndroid" or "ServerAliyun" or "Settings" => tag,
            _ => "AxTools"
        };
        FrameworkElement visiblePage = normalizedTag switch
        {
            "FantasyTools" => FantasyToolsPage,
            "GalExcleTools" => GalExcleToolsPage,
            "CrossingVoidZDTool" => CrossingVoidZDToolPage,
            "CrossingVoidPc" => CrossingVoidPcPage,
            "CrossingVoidAndroid" => CrossingVoidAndroidPage,
            "FantasyProjectPc" => FantasyProjectPcPage,
            "FantasyAndroid" => FantasyAndroidPage,
            "ServerAliyun" => ServerAliyunPage,
            "Settings" => GlobalSettingsPage,
            _ => AxToolsPage
        };

        AxToolsPage.Visibility = visiblePage == AxToolsPage ? Visibility.Visible : Visibility.Collapsed;
        FantasyToolsPage.Visibility = visiblePage == FantasyToolsPage ? Visibility.Visible : Visibility.Collapsed;
        GalExcleToolsPage.Visibility = visiblePage == GalExcleToolsPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidZDToolPage.Visibility = visiblePage == CrossingVoidZDToolPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidPcPage.Visibility = visiblePage == CrossingVoidPcPage ? Visibility.Visible : Visibility.Collapsed;
        CrossingVoidAndroidPage.Visibility = visiblePage == CrossingVoidAndroidPage ? Visibility.Visible : Visibility.Collapsed;
        FantasyProjectPcPage.Visibility = visiblePage == FantasyProjectPcPage ? Visibility.Visible : Visibility.Collapsed;
        FantasyAndroidPage.Visibility = visiblePage == FantasyAndroidPage ? Visibility.Visible : Visibility.Collapsed;
        ServerAliyunPage.Visibility = visiblePage == ServerAliyunPage ? Visibility.Visible : Visibility.Collapsed;
        GlobalSettingsPage.Visibility = visiblePage == GlobalSettingsPage ? Visibility.Visible : Visibility.Collapsed;

        if (visiblePage == ServerAliyunPage)
        {
            ServerAliyunPage.OnNavigatedTo();
        }
        else
        {
            ServerAliyunPage.OnNavigatedFrom();
        }

        _viewModel.CurrentPageTag = normalizedTag;
        ShellNavigation.SelectedItem = normalizedTag switch
        {
            "FantasyTools" => FantasyToolsNavItem,
            "GalExcleTools" => GalExcleToolsNavItem,
            "CrossingVoidZDTool" => CrossingVoidZDToolNavItem,
            "CrossingVoidPc" => CrossingVoidPcNavItem,
            "CrossingVoidAndroid" => CrossingVoidAndroidNavItem,
            "FantasyProjectPc" => FantasyProjectPcNavItem,
            "FantasyAndroid" => FantasyAndroidNavItem,
            "ServerAliyun" => ServerAliyunNavItem,
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
