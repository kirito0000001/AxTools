using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AxTools;

public sealed partial class MainWindow
{
    private void GlobalProgress_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_viewModel.GlobalProgress.IsVisible))
        {
            if (_viewModel.GlobalProgress.IsVisible)
            {
                ShowGlobalProgress();
            }
            else
            {
                HideGlobalProgress();
            }
        }
        else if (e.PropertyName == nameof(_viewModel.GlobalProgress.Percent))
        {
            UpdateGlobalProgressGeometry(_viewModel.GlobalProgress.Percent);
        }
    }

    private void ShowGlobalProgress()
    {
        GlobalProgressHost.Visibility = Visibility.Visible;
        var transform = (TranslateTransform)GlobalProgressHost.RenderTransform;
        transform.Y = 130;
        GlobalProgressHost.Opacity = 0;

        var storyboard = CreateProgressStoryboard(
            transform,
            fromY: 130,
            toY: 0,
            fromOpacity: 0,
            toOpacity: 1,
            slideDurationMilliseconds: 240,
            fadeDurationMilliseconds: 220);
        storyboard.Begin();
    }

    private void HideGlobalProgress()
    {
        if (GlobalProgressHost.Visibility != Visibility.Visible)
        {
            return;
        }

        var transform = (TranslateTransform)GlobalProgressHost.RenderTransform;
        var storyboard = CreateProgressStoryboard(
            transform,
            fromY: 0,
            toY: 130,
            fromOpacity: 1,
            toOpacity: 0,
            slideDurationMilliseconds: 180,
            fadeDurationMilliseconds: 160);
        storyboard.Completed += (_, _) => GlobalProgressHost.Visibility = Visibility.Collapsed;
        storyboard.Begin();
    }

    private void UpdateGlobalProgressGeometry(double percent)
    {
        GlobalProgressRing.Value = Math.Clamp(percent, 0, 100);
    }

    private async Task CompleteAndHideGlobalProgressAsync()
    {
        await Task.Delay(1400);
        _viewModel.GlobalProgress.Hide();
    }

    private Storyboard CreateProgressStoryboard(
        TranslateTransform transform,
        double fromY,
        double toY,
        double fromOpacity,
        double toOpacity,
        double slideDurationMilliseconds,
        double fadeDurationMilliseconds)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation
        {
            From = fromY,
            To = toY,
            Duration = TimeSpan.FromMilliseconds(slideDurationMilliseconds),
            EasingFunction = easing
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));

        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = TimeSpan.FromMilliseconds(fadeDurationMilliseconds),
            EasingFunction = easing
        };
        Storyboard.SetTarget(fade, GlobalProgressHost);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        return storyboard;
    }
}
