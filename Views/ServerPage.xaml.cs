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
using IOPath = System.IO.Path;

namespace AxTools.Views;

public sealed partial class ServerPage : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private DispatcherQueueTimer? _timer;
    private bool _isRefreshing;
    private bool _isBusy;
    private CancellationTokenSource? _updateCancellation;

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

    /// <summary>
    /// 游戏页签里那块「客户端发布（下载站）」用的页面，由 MainWindow 注入（键 = 产品键，
    /// 和 ServerProgramCatalog 一致：crossingvoid / narutobp / fantasyproject）。
    /// 这几个页面本来是左侧导航里的独立工具页，2026-09-20 起搬进服务器页签；
    /// 执行逻辑一个字节都没改，这里只负责把它们挂到对应的页签卡片里。
    /// </summary>
    private readonly Dictionary<string, ToolPage> _gamePublishPanels = new(StringComparer.Ordinal);

    /// <summary>每个产品键已经建好的发布卡片（只建一次，之后只切换显示，不再重挂）。</summary>
    private readonly Dictionary<string, Border> _gamePublishCards = new(StringComparer.Ordinal);

    /// <summary>当前正挂在发布宿主上的产品键；没换页签就不动宿主，避免打断正在输入的内容。</summary>
    private string? _visibleGamePublishKey;

    internal void AttachGamePublishPanel(string programKey, ToolPage page)
    {
        page.HostAsGamePublishPanel();
        _gamePublishPanels[programKey] = page;

        // MainWindow 调用前已经用 PageHost.Children.Remove 把它从内容网格里摘出来了
        // （不能用 FrameworkElement.Parent 判断：WinUI 里它不保证指向那个 Grid）。
        // 这里再按可视树兜一次，万一以后页面被挂到别的容器里也不会两处同时显示。
        if (VisualTreeHelper.GetParent(page) is Panel leftoverParent)
        {
            leftoverParent.Children.Remove(page);
        }
    }

    private ServerPageViewModel? ViewModel => DataContext as ServerPageViewModel;

    /// <summary>耗时操作期间显示进度并锁住动作区，避免连点造成并发操作。</summary>
    private void SetBusy(bool busy, string message, bool canCancel = false)
    {
        BusyRing.IsActive = busy;
        BusyText.Text = message;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // 动作区盖一层不可点击的遮罩：只设 IsHitTestVisible 时按钮看起来仍可点，
        // 用户会以为没响应而反复点击。遮罩 + 进度条让“正在处理”一眼可见。
        ActionBusyRing.IsActive = busy;
        ActionBusyText.Text = message;
        ActionBusyProgressBar.IsIndeterminate = true;
        ActionBusyProgressBar.Value = 0;
        ActionBusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActionPanelHost.IsHitTestVisible = !busy;
        ActionPanelHost.Opacity = busy ? 0.55 : 1;
        TargetSelector.IsEnabled = !busy;
        GamePublishPanelHost.IsHitTestVisible = !busy;
        GamePublishPanelHost.Opacity = busy ? 0.55 : 1;
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
            // 切换/回退/恢复会动服务端进程，不能中途杀脚本；只有本机导入等操作才给取消。
            progress.SetCancellationMode(
                canCancel ? AxTaskCancellationMode.Cancel : AxTaskCancellationMode.Locked,
                message);
        }
        else
        {
            progress.Complete("服务器", string.IsNullOrWhiteSpace(message) ? "已完成" : message);
            progress.Hide();
        }
    }

    /// <summary>任务主体结束后仍在忙碌周期里，只更新提示文字/进度，不解除按钮锁。</summary>
    private void UpdateBusyStatus(string message)
    {
        BusyText.Text = message;
        ActionBusyText.Text = message;
        ActionBusyProgressBar.IsIndeterminate = true;
        if (ViewModel?.GlobalProgress is { } progress)
        {
            progress.SetCancellationMode(AxTaskCancellationMode.Locked, message);
            progress.Report(new ProgressUpdate(
                message,
                progress.Percent,
                "正在读取服务器最新状态…",
                IsIndeterminate: true));
        }
    }

    /// <summary>全局进度条的取消按钮会调到这里；只在当前操作明确可取消时生效。</summary>
    internal bool TryCancelActiveUpdate()
    {
        if (_updateCancellation is null || _updateCancellation.IsCancellationRequested)
        {
            return false;
        }

        var cancellation = _updateCancellation;
        cancellation.Cancel();
        BusyText.Text = "正在取消…";
        ActionBusyText.Text = "正在取消，等待脚本退出…";
        ActionBusyProgressBar.IsIndeterminate = true;
        return true;
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

        UpdateGamePublishPanel();
    }

    /// <summary>
    /// 游戏包发布卡片（原来挂在左侧导航那三个游戏页里，2026-09-20 挪进服务器页签）。
    ///
    /// 它挂在固定的 GamePublishPanelHost 上，不跟着 15 秒轮询重建：只有真的换了页签才动一次，
    /// 否则正在填版本号 / 选产物目录时会被刷新打断。卡片本身也只建一次，之后只切显示。
    /// </summary>
    private void UpdateGamePublishPanel()
    {
        var programKey = CurrentProgramProfile()?.Key;
        if (string.Equals(programKey, _visibleGamePublishKey, StringComparison.Ordinal))
        {
            return;
        }

        _visibleGamePublishKey = programKey;
        if (programKey is null || !_gamePublishPanels.TryGetValue(programKey, out var publishPage))
        {
            GamePublishPanelHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_gamePublishCards.TryGetValue(programKey, out var card))
        {
            card = CreatePanel(
                "客户端发布（下载站）",
                "按文件清单增量推到 dl.crossingvoid.top；没变化的文件不会重复上传。",
                publishPage,
                "PC 与安卓各记一个产物目录；发布时自动排除 pdb、Saved 日志、符号文件、AFS 调试包等（详见对接文档）。");
            _gamePublishCards[programKey] = card;
            GamePublishPanelHost.Children.Add(card);
        }

        foreach (var entry in _gamePublishCards)
        {
            entry.Value.Visibility = string.Equals(entry.Key, programKey, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        GamePublishPanelHost.Visibility = Visibility.Visible;

        if (publishPage.DataContext is ToolPageViewModel gameViewModel)
        {
            _ = CheckGameVersionAsync(gameViewModel);
        }
    }

    /// <summary>
    /// 进这个页签就自动查一次线上版本：以前是独立页面时靠模式选择器触发，嵌进来以后没人触发，
    /// 会一直停在"尚未检查"。三个游戏统一走下载站清单目录（games/&lt;产品段&gt;[-android]/manifests/）。
    /// 结果同时写进日志，方便回看"发布那一刻线上是多少"。
    /// </summary>
    private async Task CheckGameVersionAsync(ToolPageViewModel gameViewModel)
    {
        await gameViewModel.CheckGamePublishedVersionAsync(CancellationToken.None);
        ViewModel?.Log?.Write(
            LogKind.Info,
            $"{gameViewModel.DisplayName} · 线上版本：{gameViewModel.GamePublishedVersionText}");
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

    /// <summary>
    /// 供其它页面调用：按 key 执行服务端更新动作。
    /// 弹窗、忙碌遮罩与进度都走服务器页这套现成实现，避免两处逻辑分叉。
    /// 目前由游戏页「打包与发布 → 服务端状态」的按钮调用。
    /// </summary>
    internal async Task RunProgramActionAsync(
        string actionKey,
        ServerPageViewModel viewModel,
        ServerProgramProfile profile)
    {
        switch (actionKey)
        {
            case "import":
                await ImportBuildAsync(viewModel, profile);
                break;
            case "push":
                await PushNewVersionAsync(viewModel, profile);
                break;
            case "apply":
                await ApplyAsync(viewModel, profile);
                break;
            case "rollback":
                await RollbackAsync(viewModel, profile);
                break;
            case "recover":
                await RecoverAsync(viewModel, profile);
                break;
            case "preview":
                await PreviewAsync(viewModel, profile);
                break;
            case "cleanup":
                await CleanupLocalRepoAsync(viewModel, profile);
                break;
        }
    }

    /// <summary>
    /// 服务器级动作（看门狗日志、日志/缓存/崩溃清理、暂停监控），供其它页面调用。
    /// 与 RunProgramActionAsync 一样复用现有实现，不重写。
    /// </summary>
    internal async Task RunServerLevelActionAsync(string actionKey, ServerPageViewModel viewModel)
    {
        switch (actionKey)
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
        buttons.Children.Add(CreateActionButton(
            "恢复服务端",
            "\uE777",
            () => RecoverAsync(viewModel, profile),
            isEnabled: state.Remote is { Initialized: true }));
        buttons.Children.Add(CreateActionButton("查看待更新内容", "\uE8A5", () => PreviewAsync(viewModel, profile)));
        buttons.Children.Add(CreateActionButton(
            "清理本地仓库",
            "\uE74D",
            () => CleanupLocalRepoAsync(viewModel, profile),
            isEnabled: state.Local is not null));
        host.Children.Add(buttons);

        host.Children.Add(CreateHintText($"本机仓库：{state.LocalRepoPath}"));
        if (!string.IsNullOrWhiteSpace(state.WorkTreePath))
        {
            host.Children.Add(CreateHintText($"服务器运行目录：{state.WorkTreePath}"));
        }

        host.Children.Add(CreateImportSourceLink(viewModel, profile));
    }

    /// <summary>
    /// 「导入目录」那一行：显示按程序记住的《导入新构建》源目录，点一下换一个。
    /// 做成可点的而不是纯提示文字 —— 记住之后总得有个出口，否则只能去删设置文件。
    /// </summary>
    private Button CreateImportSourceLink(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var remembered = viewModel.GetImportSource(profile.Key);
        var text = string.IsNullOrWhiteSpace(remembered)
            ? "导入目录：还没选过（点这里选）"
            : Directory.Exists(remembered)
                ? $"导入目录：{remembered}（点这里更换）"
                : $"导入目录：{remembered}（目录已不在，点这里重选）";

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            }
        };

        button.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync();
            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            await viewModel.RememberImportSourceAsync(profile.Key, picked, CancellationToken.None);
            await RefreshUpdateSectionAsync(viewModel, profile);
        };

        return button;
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

    private Button CreateActionButton(
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

        // async void：从这里漏出去的异常会把进程直接带走（stowed exception 0xc000027b），
        // 所以按钮动作的失败只写日志，不再往外抛。
        button.Click += async (_, _) =>
        {
            try
            {
                await onClick();
            }
            catch (Exception exception)
            {
                ViewModel?.Log?.Write(LogKind.Error, $"「{text}」执行失败。", exception);
            }
        };
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
        if (_isBusy)
        {
            return;
        }

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
        if (_isBusy)
        {
            return;
        }

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
        if (_isBusy)
        {
            return;
        }

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

    /// <summary>导入前先确认是服务端构建，避免把客户端目录当作服务端镜像进 Git。</summary>
    private static bool IsValidServerBuildDirectory(string source, ServerProgramProfile profile)
    {
        try
        {
            var root = IOPath.GetFullPath(source).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            var localRepo = IOPath.GetFullPath(ServerProgramCatalog.GetLocalRepoPath(profile))
                .TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            if (string.Equals(root, localRepo, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Directory.Exists(IOPath.Combine(root, "Engine")) &&
                File.Exists(IOPath.Combine(root, profile.ServerExecutableName));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>耗时动作统一走这里：显示进度、锁住动作区、写日志、失败弹详情。</summary>
    private async Task<T?> RunUpdateOperationAsync<T>(
        ServerPageViewModel viewModel,
        string title,
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool>? succeeded = null,
        Func<T, string>? outcome = null,
        string? busyMessage = null,
        bool canCancel = false,
        Func<Task>? afterOperation = null)
        where T : class
    {
        if (_isBusy)
        {
            return null;
        }

        using var cancellation = new CancellationTokenSource();
        _updateCancellation = canCancel ? cancellation : null;
        _isBusy = true;
        SetBusy(
            true,
            string.IsNullOrWhiteSpace(busyMessage) ? $"正在执行：{title}…" : busyMessage,
            canCancel);
        // 脚本会用 ::axtools 报阶段和百分比；不接这条线，进度条就只能停在"正在准备…"。
        var updateService = viewModel.HasUpdateService ? viewModel.UpdateService : null;
        if (updateService is not null)
        {
            updateService.EventSink = ForwardServerGitEvent;
        }

        try
        {
            var result = await operation(cancellation.Token);
            if (succeeded is null || succeeded(result))
            {
                viewModel.Log?.Write(LogKind.User, $"{title}已完成。");
            }
            else
            {
                // "脚本跑完了"不等于"这件事成了"：切换版本失败时脚本照样正常退出，
                // 真实成败在返回体里（report.success），不能一律写"已完成"。
                viewModel.Log?.Write(
                    LogKind.Error,
                    $"{title}失败：{outcome?.Invoke(result) ?? "脚本返回了失败结果"}");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            viewModel.Log?.Write(LogKind.User, $"{title}已取消。");
            return null;
        }
        catch (Exception exception)
        {
            // 失败只写日志，不弹窗 —— 输出日志区本来就有这条（带完整堆栈），
            // 弹窗还会把界面挡住、得手动关掉。
            viewModel.Log?.Write(LogKind.Error, $"{title}失败。", exception);
            return null;
        }
        finally
        {
            if (ReferenceEquals(_updateCancellation, cancellation))
            {
                _updateCancellation = null;
            }

            if (updateService is not null)
            {
                updateService.EventSink = null;
            }

            // “脚本已完成”不等于“界面已经拿到最新状态”。这里如果把遮罩先撤掉，
            // 后面那次 SSH 刷新会留下十几秒空窗，用户会以为可以再点一次。
            // 因此把完成后刷新也关在同一个忙碌周期里，刷新完才解锁。
            if (afterOperation is not null)
            {
                try
                {
                    UpdateBusyStatus("正在刷新服务器状态…");
                    await afterOperation();
                }
                catch (Exception exception)
                {
                    viewModel.Log?.Write(
                        LogKind.Warning,
                        $"{title}完成后刷新服务器状态失败。",
                        exception);
                }
            }

            _isBusy = false;
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>
    /// 脚本事件来自后台读取线程，必须切回 UI 线程再改 ViewModel。
    /// 有百分比的（复制/统计文件）走确定进度；只有阶段的走"不确定"，让圆环转起来。
    /// </summary>
    private void ForwardServerGitEvent(AxTaskEvent taskEvent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 操作结束后可能还有排队的脚本事件；不挡掉会把已经 Hidden 的进度条重新点亮。
            if (!_isBusy || ViewModel?.GlobalProgress is not { } progress)
            {
                return;
            }

            switch (taskEvent.Type)
            {
                case AxTaskEventType.Progress:
                    ApplyBusyProgress(taskEvent);
                    progress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "正在执行…",
                        taskEvent.Percent ?? progress.Percent,
                        taskEvent.Detail));
                    break;
                case AxTaskEventType.Stage:
                    ApplyBusyProgress(taskEvent);
                    progress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "正在执行…",
                        taskEvent.Percent ?? progress.Percent,
                        taskEvent.Detail,
                        IsIndeterminate: taskEvent.Percent is null));
                    break;
                case AxTaskEventType.Warning:
                    ViewModel?.Log?.Write(
                        LogKind.Warning,
                        taskEvent.Message ?? taskEvent.Code ?? "脚本警告");
                    break;
            }
        });
    }

    /// <summary>把脚本阶段同步到服务器页里的遮罩进度条和文字。</summary>
    private void ApplyBusyProgress(AxTaskEvent taskEvent)
    {
        if (!string.IsNullOrWhiteSpace(taskEvent.Message))
        {
            BusyText.Text = taskEvent.Message;
            ActionBusyText.Text = taskEvent.Message;
        }

        if (taskEvent.Percent is { } percent)
        {
            ActionBusyProgressBar.IsIndeterminate = false;
            ActionBusyProgressBar.Value = Math.Clamp(percent, 0, 100);
        }
        else
        {
            ActionBusyProgressBar.IsIndeterminate = true;
        }
    }

    private async Task ImportBuildAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        if (_isBusy)
        {
            return;
        }

        // 记住过、而且目录还在 → 直接用（省一次翻目录）；否则弹文件夹选择。
        // 想换目录：点卡片上那行「导入目录：…（点这里更换）」。
        var source = viewModel.GetImportSource(profile.Key);
        var rememberSource = false;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            source = await PickFolderAsync();
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            rememberSource = true;
        }

        if (!IsValidServerBuildDirectory(source, profile))
        {
            await ShowTextAsync(
                $"{profile.DisplayName} · 源目录不像服务端构建",
                string.Empty,
                $"选中的目录：{source}{Environment.NewLine}{Environment.NewLine}" +
                $"这里看不到 {profile.ServerExecutableName} 或 Engine\\ 目录。" +
                $"{Environment.NewLine}请选择 WindowsServer 构建目录，而不是客户端或空目录。");
            return;
        }

        if (rememberSource)
        {
            await viewModel.RememberImportSourceAsync(profile.Key, source, CancellationToken.None);
        }

        var import = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 导入新构建",
            ct => viewModel.UpdateService.ImportBuildAsync(profile, source, null, ct),
            busyMessage: "正在导入新构建（只改本机仓库）；完成后会弹出提交确认…",
            canCancel: true,
            afterOperation: () => RefreshUpdateSectionAsync(viewModel, profile));
        if (import is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("导入完成。下面是准备提交到本机 Git 的变更；点「提交」后才会进入推送。");
        report.AppendLine();
        report.AppendLine($"源目录：{source}");
        report.AppendLine();
        var sync = import.Sync;
        var changes = import.Changes ?? [];
        report.AppendLine(
            $"镜像结果：新增 {sync?.AddedFiles ?? 0} 个 · 更新 {sync?.UpdatedFiles ?? 0} 个 · " +
            $"删除 {sync?.RemovedFiles ?? 0} 个（{FormatSize(sync?.RemovedBytes ?? 0)}）");
        report.AppendLine();
        if (changes.Count == 0)
        {
            report.AppendLine("没有检测到任何差异：服务器上跑的就是这份构建。");
        }
        else
        {
            report.AppendLine($"变更 {changes.Count} 项：");
            foreach (var change in changes.Take(60))
            {
                var size = change.Kind == "removed" ? "-" : FormatSize(change.Bytes);
                report.AppendLine($"[{change.Kind,-8}] {size,10}  {change.Path}");
            }

            if (changes.Count > 60)
            {
                report.AppendLine($"… 其余 {changes.Count - 60} 项省略");
            }
        }

        var defaultMessage = profile.DefaultCommitSubject(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        // 差异清单同时写进输出日志：弹窗可以随手关掉，日志留着能回看、能复制
        viewModel.Log?.Write(LogKind.Info, $"{profile.DisplayName} · 导入新构建 差异清单：{Environment.NewLine}{report}");
        var message = await ShowCommitDialogAsync(
            $"{profile.DisplayName} · 导入完成，确认提交",
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
            ct => viewModel.UpdateService.CommitAsync(profile, message, null, ct),
            busyMessage: "正在提交到本机仓库…",
            canCancel: true,
            afterOperation: () => RefreshUpdateSectionAsync(viewModel, profile));
        if (commit is null)
        {
            return;
        }

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
            ct => viewModel.UpdateService.PushAsync(profile, null, ct),
            busyMessage: "正在推送到服务器；服务端仍在运行，完成后还需要切换最新版本…",
            canCancel: true,
            afterOperation: () => RefreshUpdateSectionAsync(viewModel, profile));
        if (status is null)
        {
            return;
        }

        await ShowTextAsync(
            $"{profile.DisplayName} · 推送完成，下一步：切换最新版本",
            string.Empty,
            "服务器已收到新版本，服务端仍在运行。确认无误后点「切换最新版本」。" + Environment.NewLine +
            $"本机领先 {status.Ahead} 个提交。");
    }

    private async Task ApplyAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        // 连点两次就是两道确认弹窗：WinUI 一个线程只能有一个 ContentDialog，
        // 第二个会抛 COMException(0x80000019) 并且直接崩进程（2026-10-11 实测）。
        if (_isBusy)
        {
            return;
        }

        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 切换最新版本",
            "将执行：暂挂看门狗 → 停止服务端 → 切换到最新版本 → 启动 → 健康检查最长 420 秒（7 分钟） → 恢复看门狗。" +
            Environment.NewLine + Environment.NewLine +
            $"目标版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Main?.Describe() ?? "最新版本"}" +
            Environment.NewLine +
            $"当前版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Deployed?.Describe() ?? "未知"}" +
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
            ct => viewModel.UpdateService.ApplyAsync(profile, ct),
            succeeded: result => result.Success,
            outcome: result => result.Describe(),
            busyMessage: "正在切换版本并重启；健康检查最长 7 分钟，请不要重复操作…",
            canCancel: false,
            afterOperation: async () =>
            {
                await RefreshUpdateSectionAsync(viewModel, profile);
                await RefreshAsync();
            });
        if (report is null)
        {
            return;
        }

        await ShowTextAsync($"{profile.DisplayName} · 切换结果", string.Empty, report.Describe());
    }

    private async Task RollbackAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        if (_isBusy)
        {
            return;
        }

        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 回退上个版本",
            "将把服务器切回上一个版本，流程与更新相同：暂挂看门狗 → 停服 → 切换 → 启动 → 健康检查。" +
            Environment.NewLine + Environment.NewLine +
            $"目标版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Previous?.Describe() ?? "上一个版本"}" +
            Environment.NewLine +
            $"当前版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Deployed?.Describe() ?? "未知"}" +
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
            ct => viewModel.UpdateService.RollbackAsync(profile, ct),
            succeeded: result => result.Success,
            outcome: result => result.Describe(),
            busyMessage: "正在回退版本并重启；健康检查最长 7 分钟，请不要重复操作…",
            canCancel: false,
            afterOperation: async () =>
            {
                await RefreshUpdateSectionAsync(viewModel, profile);
                await RefreshAsync();
            });
        if (report is null)
        {
            return;
        }

        await ShowTextAsync($"{profile.DisplayName} · 回退结果", string.Empty, report.Describe());
    }

    private async Task RecoverAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        if (_isBusy)
        {
            return;
        }

        var state = viewModel.GetUpdateState(profile.Key);
        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 恢复服务端",
            "用于切换失败、服务端停在维护模式或进程没起来的情况。" +
            Environment.NewLine + Environment.NewLine +
            "将强制对齐到当前 deployed 版本，重新启动并做健康检查，成功后解除维护模式；不会切到最新版本。" +
            Environment.NewLine + Environment.NewLine +
            $"目标版本：{state?.Remote?.Deployed?.Describe() ?? "当前部署版本"}" +
            Environment.NewLine +
            $"受影响实例：{string.Join("、", profile.InstanceNames)}" +
            Environment.NewLine +
            "恢复期间玩家会掉线。确定继续吗？",
            "开始恢复");
        if (!confirmed)
        {
            return;
        }

        var report = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 恢复服务端",
            ct => viewModel.UpdateService.RecoverAsync(profile, ct),
            succeeded: result => result.Success,
            outcome: result => result.Describe(),
            busyMessage: "正在恢复服务端；对齐部署版本并等待健康检查，最长 7 分钟…",
            canCancel: false,
            afterOperation: async () =>
            {
                await RefreshUpdateSectionAsync(viewModel, profile);
                await RefreshAsync();
            });
        if (report is null)
        {
            return;
        }

        await ShowTextAsync($"{profile.DisplayName} · 恢复结果", string.Empty, report.Describe());
    }

    private async Task CleanupLocalRepoAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        if (_isBusy)
        {
            return;
        }

        var confirmed = await ShowConfirmAsync(
            $"{profile.DisplayName} · 清理本地仓库",
            "只清理本地仓库的 .git：执行 reflog 回收和 git gc。" +
            Environment.NewLine + Environment.NewLine +
            "不会动工作树、不会动 Saved，也不会删除 main / deployed / previous 引用。" +
            Environment.NewLine +
            "清理期间可能占用几分钟，确定继续吗？",
            "开始清理");
        if (!confirmed)
        {
            return;
        }

        var result = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 清理本地仓库",
            ct => viewModel.UpdateService.CleanupLocalRepoAsync(profile, null, ct),
            busyMessage: "正在清理本地 .git（reflog + gc），不动工作树和 Saved…",
            canCancel: true,
            afterOperation: () => RefreshUpdateSectionAsync(viewModel, profile));
        if (result is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"本地 .git：{FormatSize(result.GitBeforeBytes)} → {FormatSize(result.GitAfterBytes)}");
        report.AppendLine($"工作树：{FormatSize(result.WorkTreeBytes)}（未改动）");
        report.AppendLine($"分支：{result.Status?.Branch ?? "未知"}");
        if (result.Status?.Head is { } head)
        {
            report.AppendLine($"HEAD：{head.Describe()}");
        }
        await ShowTextAsync(
            $"{profile.DisplayName} · 本地仓库清理结果",
            string.Empty,
            report.ToString());
    }

    private async Task PreviewAsync(ServerPageViewModel viewModel, ServerProgramProfile profile)
    {
        var preview = await RunUpdateOperationAsync(
            viewModel,
            $"{profile.DisplayName} · 查看待更新内容",
            ct => viewModel.UpdateService.PreviewAsync(profile, null, ct),
            busyMessage: "正在读取本机仓库的提交与差异…",
            canCancel: true);
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

        var result = await ShowDialogCoreAsync(dialog);
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

        var result = await ShowDialogCoreAsync(dialog);
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

    /// <summary>
    /// 本页所有弹窗统一从这里出去：<see cref="DialogGate"/> 保证同一时刻只有一个 ContentDialog
    /// （两个撞在一起会抛 COMException 0x80000019，进而把进程带走），抢不到闸门或 ShowAsync
    /// 抛异常都返回 null，调用方按"用户取消"处理。
    /// </summary>
    private Task<ContentDialogResult?> ShowDialogCoreAsync(ContentDialog dialog) =>
        DialogGate.TryShowAsync(dialog, ViewModel?.Log);

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

        var result = await ShowDialogCoreAsync(dialog);
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
