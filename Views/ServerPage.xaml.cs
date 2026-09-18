using System.Text;
using System.Text.Json;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AxTools.Views;

public sealed partial class ServerPage : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private DispatcherQueueTimer? _timer;
    private bool _isRefreshing;
    private bool _isBusy;

    /// <summary>当前页签里「服务端更新」小节的宿主，异步回填时用来判断是否仍然是同一份。</summary>
    private StackPanel? _updateHost;

    public ServerPage()
    {
        InitializeComponent();
    }

    /// <summary>进入服务器分区时开始轮询；离开时停止，避免后台空转。</summary>
    public void OnNavigatedTo()
    {
        _timer ??= CreateTimer();
        _timer.Start();
        _ = RefreshCurrentAsync();
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

        // 耗时动作同时驱动 AxTools 底部那条全局进度条。
        if (ViewModel?.GlobalProgress is not { } progress)
        {
            return;
        }

        if (busy)
        {
            progress.Start("服务器", message);
            progress.IsIndeterminate = true;
            progress.SetCancellationMode(AxTaskCancellationMode.Locked, message);
        }
        else
        {
            progress.Complete("服务器", string.IsNullOrWhiteSpace(message) ? "已完成" : message);
            progress.Hide();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshCurrentAsync();

    private async Task RefreshCurrentAsync()
    {
        await RefreshAsync();
        if (ViewModel is { } viewModel && CurrentProgramProfile() is { } profile && viewModel.HasUpdateService)
        {
            await FetchProgramUpdateAsync(viewModel, profile.Key);
        }
    }

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

    private async void TargetSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        RenderActionPanel(viewModel);
        if (CurrentProgramProfile() is { } profile && viewModel.HasUpdateService)
        {
            await FetchProgramUpdateAsync(viewModel, profile.Key);
        }
    }

    /// <summary>当前页签对应的服务端程序；阿里云服务器页签返回 null。</summary>
    private ServerProgramProfile? CurrentProgramProfile()
    {
        var index = TargetSelector.SelectedItem is { } selected
            ? TargetSelector.Items.IndexOf(selected)
            : 0;
        return index switch
        {
            1 => ServerProgramCatalog.Get("crossingvoid"),
            2 => ServerProgramCatalog.Get("narutobp"),
            3 => ServerProgramCatalog.Get("fantasyproject"),
            _ => null
        };
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

        var updateHost = new StackPanel { Spacing = 10 };
        _updateHost = updateHost;
        var profile = ServerProgramCatalog.Get(programKey);
        RenderUpdateFromCache(updateHost, viewModel, profile);
        content.Children.Add(CreatePanel(
            "服务端更新",
            $"Git 增量更新 · 只保留最近两版 · {profile.UpdateNote}",
            updateHost,
            profile.UpdateNote.Contains("Saved", StringComparison.Ordinal)
                ? null
                : "Saved\\ 是玩家数据，更新与回退都不会改动它。"));

        return CreatePanel(
            title,
            $"{instances.Length} 个实例 · 停止与重启会先确认",
            content,
            null);
    }

    /// <summary>先用缓存渲染，避免看门狗轮询把 SSH 也一起拉起来。</summary>
    private void RenderUpdateFromCache(
        StackPanel host,
        ServerPageViewModel viewModel,
        ServerProgramProfile profile)
    {
        host.Children.Clear();
        if (viewModel.GetUpdateState(profile.Key) is { } cached)
        {
            RenderUpdateSection(host, viewModel, profile, cached);
            return;
        }

        host.Children.Add(CreateHintText(
            viewModel.HasUpdateService
                ? "尚未读取状态。点「立即刷新」或重新进入该页签即可读取。"
                : "服务端更新需要任务运行器，当前不可用。"));
    }

    private async Task FetchProgramUpdateAsync(ServerPageViewModel viewModel, string programKey)
    {
        var host = _updateHost;
        if (host is null || !viewModel.HasUpdateService)
        {
            if (host is not null)
            {
                host.Children.Clear();
                host.Children.Add(CreateHintText("服务端更新需要任务运行器，当前不可用。"));
            }

            return;
        }

        var profile = ServerProgramCatalog.Get(programKey);
        ServerProgramUpdateState state;
        try
        {
            state = await viewModel.RefreshUpdateStateAsync(profile, CancellationToken.None);
        }
        catch (Exception exception)
        {
            viewModel.Log?.Write(LogKind.Error, $"读取 {profile.DisplayName} 服务端更新状态失败。", exception);
            if (!ReferenceEquals(host, _updateHost))
            {
                return;
            }

            host.Children.Clear();
            host.Children.Add(CreateHintText($"读取失败：{exception.Message}"));
            return;
        }

        if (!ReferenceEquals(host, _updateHost))
        {
            return;
        }

        RenderUpdateSection(host, viewModel, profile, state);
    }

    private void RenderUpdateSection(
        StackPanel host,
        ServerPageViewModel viewModel,
        ServerProgramProfile profile,
        ServerProgramUpdateState state)
    {
        host.Children.Clear();

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            host.Children.Add(CreateHintText($"状态读取不完整：{state.Error}"));
        }

        host.Children.Add(CreateKeyValueLine("部署中", state.DeployedText));
        host.Children.Add(CreateKeyValueLine("上一版", state.PreviousText));
        host.Children.Add(CreateKeyValueLine("本机仓库", state.LocalAheadText));
        host.Children.Add(CreateKeyValueLine("服务器", state.PendingText));
        host.Children.Add(CreateKeyValueLine("实例", state.InstancesText));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(CreateActionButton("导入新构建", "\uE8E5", () => ImportBuildAsync(viewModel, profile)));
        buttons.Children.Add(CreateActionButton("推送新版本", "\uE72A", () => PushNewVersionAsync(viewModel, profile)));
        buttons.Children.Add(CreateActionButton(
            "切换最新版本",
            "\uE72C",
            () => ApplyAsync(viewModel, profile),
            isEnabled: state.CanSwitchLatest));
        buttons.Children.Add(CreateActionButton(
            "回退上个版本",
            "\uE7A7",
            () => RollbackAsync(viewModel, profile),
            isEnabled: state.CanRollback));
        buttons.Children.Add(CreateActionButton("查看待更新内容", "\uE8A5", () => PreviewAsync(viewModel, profile)));
        host.Children.Add(buttons);

        host.Children.Add(CreateHintText($"本机仓库：{state.LocalRepoPath}"));
        if (!string.IsNullOrWhiteSpace(state.WorkTreePath))
        {
            host.Children.Add(CreateHintText($"服务器运行目录：{state.WorkTreePath}"));
        }
    }

    private static TextBlock CreateHintText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.72,
        TextWrapping = TextWrapping.Wrap
    };

    private static Grid CreateKeyValueLine(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock { Text = label, FontSize = 13, Opacity = 0.7 };
        var content = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(caption);
        grid.Children.Add(content);
        return grid;
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

    private static Button CreateActionButton(
        string text,
        string glyph,
        Func<Task> onClick,
        bool isEnabled = true)
    {
        var button = new Button
        {
            Height = 40,
            Width = 200,
            IsEnabled = isEnabled,
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

    // ---- 服务端 Git 更新 ----------------------------------------------------

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        if (App.ShellWindow is { } window)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        }

        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>耗时动作统一走这里：显示进度、锁住动作区、写日志、失败弹详情。</summary>
    private async Task<T?> RunUpdateOperationAsync<T>(
        ServerPageViewModel viewModel,
        string title,
        Func<Task<T>> operation)
        where T : class
    {
        if (_isBusy)
        {
            return null;
        }

        _isBusy = true;
        SetBusy(true, $"正在执行：{title}…");
        try
        {
            var result = await operation();
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

    private async Task ImportBuildAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        if (_isBusy)
        {
            return;
        }

        var source = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var import = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 导入新构建",
            () => viewModel.UpdateService.ImportBuildAsync(profile, source, null, CancellationToken.None));
        if (import is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"源目录：{source}");
        report.AppendLine();
        var sync = import.Sync;
        report.AppendLine(
            $"镜像结果：新增 {sync?.AddedFiles ?? 0} 个 · 更新 {sync?.UpdatedFiles ?? 0} 个 · " +
            $"删除 {sync?.RemovedFiles ?? 0} 个（{FormatSize(sync?.RemovedBytes ?? 0)}）");
        report.AppendLine();
        if (import.Changes.Count == 0)
        {
            report.AppendLine("没有检测到任何差异：服务器上跑的就是这份构建。");
        }
        else
        {
            report.AppendLine($"变更 {import.Changes.Count} 项：");
            foreach (var change in import.Changes.Take(60))
            {
                var size = change.Kind == "removed" ? "-" : FormatSize(change.Bytes);
                report.AppendLine($"[{change.Kind,-8}] {size,10}  {change.Path}");
            }

            if (import.Changes.Count > 60)
            {
                report.AppendLine($"… 其余 {import.Changes.Count - 60} 项省略");
            }
        }

        var defaultMessage = profile.DefaultCommitSubject(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        var message = await ShowCommitDialogAsync(
            $"{profile.DisplayName} · 确认导入",
            report.ToString(),
            defaultMessage);
        if (string.IsNullOrWhiteSpace(message))
        {
            viewModel.Log?.Write(LogKind.Info, $"{profile.DisplayName} 已镜像到本机仓库，但未提交。");
            await RefreshUpdateSectionAsync(viewModel, profile);
            return;
        }

        var commit = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 提交",
            () => viewModel.UpdateService.CommitAsync(profile, message, null, CancellationToken.None));
        if (commit is null)
        {
            return;
        }

        await RefreshUpdateSectionAsync(viewModel, profile);

        if (commit.Committed)
        {
            var pushNow = await ShowConfirmAsync(
                "立即推送？",
                $"已提交：{message}{Environment.NewLine}{Environment.NewLine}" +
                "现在推送到服务器吗？推送过程中服务端照常运行，不会重启。",
                "推送");
            if (pushNow)
            {
                await PushNewVersionAsync(viewModel, profile);
            }
        }
        else
        {
            await ShowTextAsync($"{profile.DisplayName} · 没有新变更", string.Empty, commit.Detail);
        }
    }

    private async Task PushNewVersionAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var status = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 推送新版本",
            () => viewModel.UpdateService.PushAsync(profile, null, CancellationToken.None));
        if (status is null)
        {
            return;
        }

        await RefreshUpdateSectionAsync(viewModel, profile);
        await ShowTextAsync(
            $"{profile.DisplayName} · 推送完成",
            string.Empty,
            "服务器已收到新版本，服务端仍在运行。确认无误后点「应用并重启」。" + Environment.NewLine +
            $"本机领先 {status.Ahead} 个提交。");
    }

    private async Task ApplyAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 切换最新版本",
            "将执行：暂挂看门狗 → 停止服务端 → 切换到最新版本 → 启动 → 健康检查 180 秒 → 恢复看门狗。" +
            Environment.NewLine + Environment.NewLine +
            $"受影响实例：{string.Join("、", profile.InstanceNames)}" +
            Environment.NewLine +
            "切换期间玩家会掉线（玩家创建的房间也会一起结束）。" +
            Environment.NewLine +
            "切过去之后，当前这一版会变成「上个版本」，随时可以退回来。确定继续吗？",
            "开始切换");
        if (!confirmed)
        {
            return;
        }

        var report = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 切换最新版本",
            () => viewModel.UpdateService.ApplyAsync(profile, CancellationToken.None));
        if (report is null)
        {
            return;
        }

        await RefreshUpdateSectionAsync(viewModel, profile);
        await RefreshAsync();
        await ShowTextAsync($"{profile.DisplayName} · 切换结果", string.Empty, report.Describe());
    }

    private async Task RollbackAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 回退上个版本",
            "将把服务器切回上一个版本，流程与更新相同：暂挂看门狗 → 停服 → 切换 → 启动 → 健康检查。" +
            Environment.NewLine + Environment.NewLine +
            $"受影响实例：{string.Join("、", profile.InstanceNames)}" +
            Environment.NewLine +
            "最新版本不会被删除，仍然留在服务器上，随时可以再切回去。" +
            Environment.NewLine +
            "确定回退吗？",
            "回退");
        if (!confirmed)
        {
            return;
        }

        var report = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 回退上个版本",
            () => viewModel.UpdateService.RollbackAsync(profile, CancellationToken.None));
        if (report is null)
        {
            return;
        }

        await RefreshUpdateSectionAsync(viewModel, profile);
        await RefreshAsync();
        await ShowTextAsync($"{profile.DisplayName} · 回退结果", string.Empty, report.Describe());
    }

    private async Task PreviewAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var preview = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 查看待更新内容",
            () => viewModel.UpdateService.PreviewAsync(profile, null, CancellationToken.None));
        if (preview is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"本机仓库：{ServerProgramCatalog.GetLocalRepoPath(profile)}");
        report.AppendLine();
        report.AppendLine("最近提交：");
        foreach (var commit in preview.Commits)
        {
            report.AppendLine($"  {commit.Short}  {commit.Date?.LocalDateTime:yyyy-MM-dd HH:mm}  {commit.Subject}");
        }

        report.AppendLine();
        if (preview.DiffStat.Count == 0)
        {
            report.AppendLine("与服务器一致的提交之间没有文件差异。");
        }
        else
        {
            report.AppendLine("与服务器相比的文件差异：");
            foreach (var line in preview.DiffStat)
            {
                report.AppendLine("  " + line);
            }
        }

        await ShowTextAsync($"{profile.DisplayName} · 待更新内容", string.Empty, report.ToString());
    }

    private async Task RefreshUpdateSectionAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        // 只有当前页签仍然停在这个程序上时才重绘。
        var index = TargetSelector.SelectedItem is { } selected
            ? TargetSelector.Items.IndexOf(selected)
            : 0;
        var expected = profile.Key switch
        {
            "crossingvoid" => 1,
            "narutobp" => 2,
            "fantasyproject" => 3,
            _ => 0
        };
        if (index != expected)
        {
            return;
        }

        await FetchProgramUpdateAsync(viewModel, profile.Key);
    }

    private async Task<string?> ShowCommitDialogAsync(string title, string body, string defaultMessage)
    {
        var textBox = new TextBox
        {
            Text = defaultMessage,
            Header = "提交说明",
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        panel.Children.Add(textBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            MaxWidth = ResolveDialogContentWidth() + 120,
            Content = new ScrollViewer
            {
                Width = ResolveDialogContentWidth(),
                MaxHeight = 460,
                Content = panel
            },
            PrimaryButtonText = "提交",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        ApplyDialogWidth(dialog);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? textBox.Text.Trim() : null;
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

        // 用 XamlRoot 的实际尺寸算宽度：不再给 ContentDialog 设 MaxWidth
        // （设了会让它偏到左边），改成约束内容宽度，弹窗自然居中。
        var availableHeight = XamlRoot?.Size.Height ?? 800;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"{title}（{lines.Length} 行）",
            MaxWidth = ResolveDialogContentWidth() + 120,
            Content = new ScrollViewer
            {
                Width = ResolveDialogContentWidth(),
                MaxHeight = Math.Max(320, availableHeight - 300),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            },
            PrimaryButtonText = "复制全部",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        ApplyDialogWidth(dialog);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await CopyTextAsync(string.Join(Environment.NewLine, lines), "全部日志");
        }
    }

    private Border CreateLogLineBlock(string line)
    {
        var suffix = ClassifyLogLine(line);

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

    /// <summary>
    /// UE 的日志分级写在类别后面：`[时间][帧]LogNet: Error: 消息`。
    /// 只认方括号写法会把所有 UE 报错都当默认白字，所以两种写法都要覆盖。
    /// </summary>
    private static string ClassifyLogLine(string line)
    {
        if (line.Contains(": Fatal:", StringComparison.Ordinal) ||
            line.Contains(": Error:", StringComparison.Ordinal) ||
            line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Critical error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Assertion failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception:", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        if (line.Contains(": Warning:", StringComparison.Ordinal) ||
            line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }

        return "Default";
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

    /// <summary>
    /// 弹窗内容宽度。跟随窗口宽度，最宽 1360。
    /// XamlRoot.Size 在部分场景拿不到真实窗口宽度（会返回 0 或很小的值），
    /// 那样会被压到下限，所以这里带兜底：拿到明显不合理的小值时按 1780 估算。
    /// </summary>
    private double ResolveDialogContentWidth()
    {
        var available = XamlRoot?.Size.Width ?? 0;
        if (available < 900)
        {
            available = 1780;
        }

        return Math.Clamp(available - 320, 900, 1360);
    }

    /// <summary>
    /// ContentDialog 的宽度由模板里的主题资源 ContentDialogMaxWidth 决定（默认约 548），
    /// 直接设控件上的 MaxWidth 不起作用，必须把这个资源覆盖掉，弹窗才会真的变宽。
    /// </summary>
    private void ApplyDialogWidth(ContentDialog dialog)
    {
        var maxWidth = ResolveDialogContentWidth() + 120;
        dialog.Resources["ContentDialogMaxWidth"] = maxWidth;
        dialog.MaxWidth = maxWidth;
    }

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
            MaxWidth = ResolveDialogContentWidth() + 120,
            Content = new ScrollViewer
            {
                Width = ResolveDialogContentWidth(),
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
        ApplyDialogWidth(dialog);

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
