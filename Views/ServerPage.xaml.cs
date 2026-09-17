using AxTools.Core.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AxTools.Views;

public sealed partial class ServerPage : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private DispatcherQueueTimer? _timer;
    private bool _isRefreshing;

    public ServerPage()
    {
        InitializeComponent();
    }

    /// <summary>进入服务器分区时开始轮询；离开时停止，避免后台空转。</summary>
    public void OnNavigatedTo()
    {
        _timer ??= CreateTimer();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void OnNavigatedFrom() => _timer?.Stop();

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = PollInterval;
        timer.Tick += (_, _) => _ = RefreshAsync();
        return timer;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isRefreshing || DataContext is not ServerPageViewModel viewModel)
        {
            return;
        }

        _isRefreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            await viewModel.RefreshAsync(CancellationToken.None);
            Render(viewModel);
        }
        catch (Exception exception)
        {
            HeadlineText.Text = "刷新服务器状态失败";
            DetailText.Text = exception.Message;
        }
        finally
        {
            _isRefreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(ServerPageViewModel viewModel)
    {
        HeadlineText.Text = viewModel.Headline;
        DetailText.Text = viewModel.LastUpdatedAt is { } updatedAt
            ? $"{viewModel.Detail} · 更新于 {updatedAt.LocalDateTime:HH:mm:ss}"
            : viewModel.Detail;

        InstancePanel.Children.Clear();
        foreach (var instance in viewModel.Instances)
        {
            InstancePanel.Children.Add(CreateInstanceRow(instance));
        }
    }

    private static Border CreateInstanceRow(ServerInstanceViewModel instance)
    {
        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = CreateStateBrush(instance.StateKey),
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameStack = new StackPanel { Spacing = 2 };
        nameStack.Children.Add(new TextBlock
        {
            Text = instance.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = instance.EndpointText,
            FontSize = 12,
            Opacity = 0.7
        });

        var statusStack = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        statusStack.Children.Add(new TextBlock
        {
            Text = instance.StatusText,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        var detail = instance.DetailText;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            statusStack.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(statusStack, 2);
        grid.Children.Add(dot);
        grid.Children.Add(nameStack);
        grid.Children.Add(statusStack);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = CreateBorderBrush(),
            Child = grid
        };
    }

    private static SolidColorBrush CreateStateBrush(string stateKey) => stateKey switch
    {
        "healthy" or "recovered" => new SolidColorBrush(Color.FromArgb(255, 46, 160, 67)),
        "warning" => new SolidColorBrush(Color.FromArgb(255, 209, 139, 23)),
        "maintenance" => new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
        "error" => new SolidColorBrush(Color.FromArgb(255, 205, 60, 60)),
        _ => new SolidColorBrush(Color.FromArgb(255, 150, 150, 150))
    };

    private static SolidColorBrush CreateBorderBrush() =>
        new(Color.FromArgb(40, 128, 128, 128));
}
