using System.Text;
using System.Text.Json;
using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace AxTools.Views;

public sealed partial class ServerPage : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private DispatcherQueueTimer? _timer;
    private bool _isRefreshing;
    private bool _isBusy;

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

    private ServerPageViewModel? ViewModel => DataContext as ServerPageViewModel;

    /// <summary>耗时操作期间显示进度并锁住动作区，避免连点造成并发操作。</summary>
    private void SetBusy(bool busy, string message)
    {
        BusyRing.IsActive = busy;
        BusyText.Text = message;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActionPanelHost.IsHitTestVisible = !busy;
        ActionPanelHost.Opacity = busy ? 0.6 : 1;
        RefreshButton.IsEnabled = !busy;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isRefreshing || ViewModel is not { } viewModel)
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
            RefreshButton.IsEnabled = !_isBusy;
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

        RenderActionPanel(viewModel);
    }

    private void TargetSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewModel is { } viewModel)
        {
            RenderActionPanel(viewModel);
        }
    }

    /// <summary>按页签渲染动作区：阿里云服务器 / 零境交错 / 火影BP / 幻杀。</summary>
    private void RenderActionPanel(ServerPageViewModel viewModel)
    {
        ActionPanelHost.Children.Clear();
        var index = TargetSelector.SelectedItem is { } selected
            ? TargetSelector.Items.IndexOf(selected)
            : 0;

        switch (index)
        {
            case 1:
                ActionPanelHost.Children.Add(CreateProgramPanel(viewModel, "crossingvoid", "零境交错"));
                break;
            case 2:
                ActionPanelHost.Children.Add(CreateProgramPanel(viewModel, "narutobp", "火影BP"));
                break;
            case 3:
                ActionPanelHost.Children.Add(CreateProgramPanel(viewModel, "fantasyproject", "幻杀"));
                break;
            default:
                ActionPanelHost.Children.Add(CreateServerPanel(viewModel));
                break;
        }
    }

    private Border CreateServerPanel(ServerPageViewModel viewModel)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in viewModel.Actions)
        {
            actions.Children.Add(CreateActionButton(item.DisplayName, item.IconGlyph, async () =>
            {
                switch (item.Key)
                {
                    case "watchdog-log":
                        await ShowWatchdogLogAsync();
                        break;
                    case "old-logs":
                        await ShowCleanupDialogAsync(
                            "scan-old-logs",
                            "clean-old-logs",
                            "清除过期日志",
                            "将删除看门狗日志与 IIS 日志中超过 7 天的文件，正在写入的 watchdog.log 不受影响。");
                        break;
                    case "all-logs":
                        await ShowCleanupDialogAsync(
                            "scan-all-logs",
                            "clean-all-logs",
                            "清除全部日志",
                            "将删除看门狗日志与 IIS 日志的全部文件，正在写入的 watchdog.log 会被清空，历史内容无法恢复。");
                        break;
                    case "caches":
                        await ShowCleanupDialogAsync(
                            "scan-caches",
                            "clean-caches",
                            "清理缓存",
                            "将清空系统临时文件中超过 1 天的文件，并保留最新 5 份看门狗脚本备份。");
                        break;
                    case "crashes":
                        await ShowCleanupDialogAsync(
                            "scan-crashes",
                            "clean-crashes",
                            "清理崩溃文件",
                            "将删除各服务端 Saved\\Crashes 下较旧的目录，每个项目保留最近 20 个。这些目录包含崩溃原因、调用栈与内存转储。");
                        break;
                    case "watchdog-task":
                        await ToggleWatchdogTaskAsync();
                        break;
                }
            }));
        }

        return CreatePanel(
            "阿里云服务器",
            "看门狗运行状态、日志与系统目录清理。",
            actions,
            "日志与缓存清理会读取服务器上的目录；清理前请先确认扫描结果。");
    }

    private Border CreateProgramPanel(ServerPageViewModel viewModel, string programKey, string title)
    {
        var instances = viewModel.InstancesOf(programKey).ToArray();
        var content = new StackPanel { Spacing = 16 };

        foreach (var instance in instances)
        {
            var block = new StackPanel { Spacing = 8 };
            block.Children.Add(new TextBlock
            {
                Text = $"{instance.DisplayName} · {instance.EndpointText} · {instance.StatusText}",
                FontSize = 13,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(CreateActionButton("日志", "\uE7C3", () => ShowInstanceLogAsync(instance)));
            actions.Children.Add(CreateActionButton("启动", "\uE768", () => RunInstanceActionAsync(instance, "start", "启动")));
            actions.Children.Add(CreateActionButton("停止", "\uE71A", () => RunInstanceActionAsync(instance, "stop", "停止")));
            actions.Children.Add(CreateActionButton("重启", "\uE72C", () => RunInstanceActionAsync(instance, "restart", "重启")));
            block.Children.Add(actions);

            content.Children.Add(block);
        }

        if (instances.Length == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "尚未读取到该服务端实例。",
                Opacity = 0.7
            });
        }

        return CreatePanel(
            title,
            $"{instances.Length} 个实例 · 停止与重启会先确认",
            content,
            null);
    }

    private static Border CreatePanel(string title, string subtitle, UIElement content, string? footer)
    {
        var stack = new StackPanel { Spacing = 14 };
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            header.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }

        stack.Children.Add(header);
        stack.Children.Add(content);
        if (!string.IsNullOrWhiteSpace(footer))
        {
            stack.Children.Add(new TextBlock
            {
                Text = footer,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = CreateBorderBrush(),
            Child = stack
        };
    }

    private static Button CreateActionButton(string text, string glyph, Func<Task> onClick)
    {
        var button = new Button
        {
            Height = 40,
            Width = 200,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 16 },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };

        if (Application.Current.Resources.TryGetValue("PrimaryToolButtonStyle", out var style) &&
            style is Style buttonStyle)
        {
            button.Style = buttonStyle;
        }

        button.Click += async (_, _) => await onClick();
        return button;
    }

    private Border CreateInstanceRow(ServerInstanceViewModel instance)
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

    private async Task ShowInstanceLogAsync(ServerInstanceViewModel instance)
    {
        var result = await RunActionAsync(instance.Name, "logs", $"读取 {instance.DisplayName} 日志");
        if (result?.Data is not { } data)
        {
            return;
        }

        await ShowLogDialogAsync(
            $"{instance.DisplayName} 日志",
            ReadString(data, "path"),
            ReadLines(data, "lines"));
    }

    private async Task RunInstanceActionAsync(
        ServerInstanceViewModel instance,
        string action,
        string verb)
    {
        if (action is "stop" or "restart")
        {
            var confirmed = await ShowConfirmAsync(
                $"{verb} {instance.DisplayName}",
                $"将对 {instance.DisplayName}（{instance.EndpointText}）执行「{verb}」。确定继续吗？");
            if (!confirmed)
            {
                return;
            }
        }

        await RunActionAsync(instance.Name, action, $"{verb} {instance.DisplayName}");
        await RefreshAsync();
    }

    private async Task ShowWatchdogLogAsync()
    {
        var result = await RunActionAsync(string.Empty, "logs", "读取看门狗日志");
        if (result?.Data is not { } data)
        {
            return;
        }

        var lines = ReadLines(data, "lines");
        var path = ReadString(data, "path");
        await ShowLogDialogAsync("看门狗日志", path, lines);
    }

    private async Task ShowCleanupDialogAsync(
        string scanAction,
        string cleanAction,
        string title,
        string description)
    {
        var scan = await RunActionAsync(string.Empty, scanAction, $"扫描（{title}）");
        if (scan?.Data is not { } data)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine(description);
        report.AppendLine();
        long cleanableBytes = 0;
        if (data.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targets.EnumerateArray())
            {
                var id = ReadString(target, "id");
                var path = ReadString(target, "path");
                var exists = target.TryGetProperty("exists", out var existsNode) && existsNode.GetBoolean();
                var fileCount = ReadInt64(target, "fileCount");
                var cleanableCount = ReadInt64(target, "cleanableCount");
                var cleanable = ReadInt64(target, "cleanableBytes");
                cleanableBytes += cleanable;
                report.AppendLine($"{DescribeCacheTarget(id)}：{path}");
                report.AppendLine(exists
                    ? $"    共 {fileCount} 项，本次处理 {cleanableCount} 项（{FormatSize(cleanable)}）"
                    : "    目录不存在，跳过");
                report.AppendLine();
            }
        }

        report.AppendLine($"合计：{FormatSize(cleanableBytes)}");
        var confirmed = await ShowConfirmAsync(title, report.ToString(), "确认执行");
        if (!confirmed)
        {
            return;
        }

        var clean = await RunActionAsync(string.Empty, cleanAction, title);
        if (clean?.Data is not { } cleanData)
        {
            return;
        }

        var summary = new StringBuilder();
        long removedTotal = 0;
        if (cleanData.TryGetProperty("targets", out var cleaned) && cleaned.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in cleaned.EnumerateArray())
            {
                var id = ReadString(target, "id");
                var removedFiles = ReadInt64(target, "removedFiles");
                var removedBytes = ReadInt64(target, "removedBytes");
                removedTotal += removedBytes;
                var failures = target.TryGetProperty("failures", out var failureNode) &&
                    failureNode.ValueKind == JsonValueKind.Array
                        ? failureNode.GetArrayLength()
                        : 0;
                summary.AppendLine($"{DescribeCacheTarget(id)}：处理 {removedFiles} 项，释放 {FormatSize(removedBytes)}" +
                    (failures > 0 ? $"，{failures} 个失败" : string.Empty));
            }
        }

        summary.AppendLine();
        summary.AppendLine($"合计释放：{FormatSize(removedTotal)}");
        await ShowTextAsync($"{title} 完成", string.Empty, summary.ToString());
        await RefreshAsync();
    }

    private async Task ToggleWatchdogTaskAsync()
    {
        var state = await RunActionAsync(string.Empty, "watchdog-task-state", "读取监控状态");
        var running = state?.Data is { } data &&
            string.Equals(ReadString(data, "state"), "Running", StringComparison.OrdinalIgnoreCase);
        var action = running ? "watchdog-task-off" : "watchdog-task-on";
        var title = running ? "暂停看门狗监控" : "恢复看门狗监控";

        var confirmed = await ShowConfirmAsync(
            title,
            running
                ? "暂停后服务端崩溃将不再被自动拉起，直到恢复监控。确定继续吗？"
                : "恢复看门狗监控，服务端异常时会被自动拉起。");
        if (!confirmed)
        {
            return;
        }

        await RunActionAsync(string.Empty, action, title);
        await RefreshAsync();
    }

    private async Task<ServerActionResult?> RunActionAsync(
        string instanceName,
        string action,
        string title)
    {
        if (_isBusy || ViewModel is not { } viewModel)
        {
            return null;
        }

        _isBusy = true;
        SetBusy(true, $"正在执行：{title}…");
        try
        {
            var result = await viewModel.RunActionAsync(instanceName, action, CancellationToken.None);
            if (!result.Success)
            {
                viewModel.Log?.Write(LogKind.Error, $"{title}失败：{result.Message}");
                await ShowTextAsync($"{title} 失败", string.Empty, result.Message);
                return null;
            }

            viewModel.Log?.Write(LogKind.User, $"{title}已完成。");
            return result;
        }
        catch (Exception exception)
        {
            viewModel.Log?.Write(LogKind.Error, $"{title}失败。", exception);
            await ShowTextAsync($"{title} 失败", string.Empty, exception.Message);
            return null;
        }
        finally
        {
            _isBusy = false;
            SetBusy(false, string.Empty);
        }
    }

    private Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "继续") =>
        ShowDialogAsync(title, message, primaryText, withPrimary: true);

    /// <summary>服务器日志按 AX 输出日志的样式逐行展示：分级着色、可选中、点击单行复制。</summary>
    private async Task ShowLogDialogAsync(string title, string path, string[] lines)
    {
        var content = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(path))
        {
            content.Children.Add(new TextBlock
            {
                Text = path,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (lines.Length == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "日志为空。",
                Opacity = 0.7
            });
        }

        foreach (var line in lines)
        {
            content.Children.Add(CreateLogLineBlock(line));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"{title}（{lines.Length} 行）",
            MaxWidth = 960,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            },
            PrimaryButtonText = "复制全部",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await CopyTextAsync(string.Join(Environment.NewLine, lines), "全部日志");
        }
    }

    private Border CreateLogLineBlock(string line)
    {
        var upper = line.ToUpperInvariant();
        var isError = upper.Contains("[ERROR]") || upper.Contains("[FATAL]");
        var isWarning = upper.Contains("[WARN]") || upper.Contains("[WARNING]");
        var suffix = isError ? "Error" : isWarning ? "Warning" : "Default";

        var text = new TextBlock
        {
            Text = line,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        if (FindStyle($"Log{suffix}TextStyle") is { } textStyle)
        {
            text.Style = textStyle;
        }

        var border = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = text
        };
        if (FindStyle($"Log{suffix}BlockStyle") is { } blockStyle)
        {
            border.Style = blockStyle;
        }

        border.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await CopyTextAsync(line, "该行日志");
        };
        return border;
    }

    private async Task CopyTextAsync(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ViewModel?.Log?.Write(LogKind.User, $"已复制{label}。");
        await Task.CompletedTask;
    }

    private static Style? FindStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Style style
            ? style
            : null;

    private async Task ShowTextAsync(string title, string subtitle, string body)
    {
        var content = subtitle;
        if (!string.IsNullOrWhiteSpace(subtitle) || !string.IsNullOrWhiteSpace(body))
        {
            content = string.IsNullOrWhiteSpace(subtitle)
                ? body
                : $"{subtitle}{Environment.NewLine}{Environment.NewLine}{body}";
        }
        await ShowDialogAsync(title, content, string.Empty, withPrimary: false);
    }

    private async Task<bool> ShowDialogAsync(string title, string message, string primaryText, bool withPrimary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = withPrimary ? "取消" : "关闭",
            PrimaryButtonText = primaryText,
            DefaultButton = withPrimary ? ContentDialogButton.Close : ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static string DescribeCacheTarget(string id) => id switch
    {
        "watchdog-backups" => "看门狗脚本备份",
        "watchdog-logs" => "看门狗日志",
        "windows-temp" => "系统临时文件",
        "iis-logs" => "IIS 日志",
        "crossingvoid-crashes" => "零境交错崩溃目录",
        "narutobp-crashes" => "火影BP崩溃目录",
        "fantasy-crashes" => "幻杀崩溃目录",
        _ => id
    };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetInt64(out var value) ? value : 0;

    private static string[] ReadLines(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return node.EnumerateArray()
            .Select(line => line.GetString() ?? string.Empty)
            .ToArray();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };

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
