using System.Text;
using System.Text.Json;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AxTools.Views;

/// <summary>
/// 服务端动作执行器：原来挂在 ServerPage 这个页面上，现在独立出来。
///
/// 职责只有三件事：弹确认/结果、把进度写进全局进度条、调用
/// <see cref="ServerPageViewModel.RunActionAsync"/> 或 UpdateService 跑脚本。
/// 页面（ToolPage 的「服务器」页签）通过事件把动作转进来，不再依赖任何页面控件。
/// </summary>
internal sealed class ServerActionHost(
    ServerPageViewModel viewModel,
    XamlRoot xamlRoot,
    LogService? log,
    GlobalProgressViewModel progress)
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private bool _busy;
    private CancellationTokenSource? _cancellation;

    public bool TryCancelActive()
    {
        if (_cancellation is null || _cancellation.IsCancellationRequested)
        {
            return false;
        }

        _cancellation.Cancel();
        progress.SetCancellationMode(AxTaskCancellationMode.Locked, "正在取消，等待脚本退出…");
        return true;
    }

    // ---- 程序级动作（导入 / 推送 / 切换 / 回退 / 恢复 / 预览 / 清理） ----

    public async Task RunProgramActionAsync(string actionKey, ServerProgramProfile profile)
    {
        switch (actionKey)
        {
            case "import":
                await ImportBuildAsync(profile);
                break;
            case "push":
                await PushAsync(profile);
                break;
            case "apply":
                await ApplyAsync(profile);
                break;
            case "rollback":
                await RollbackAsync(profile);
                break;
            case "recover":
                await RecoverAsync(profile);
                break;
            case "preview":
                await PreviewAsync(profile);
                break;
            case "cleanup":
                await CleanupLocalRepoAsync(profile);
                break;
        }
    }

    private async Task ImportBuildAsync(ServerProgramProfile profile)
    {
        if (_busy)
        {
            return;
        }

        var source = viewModel.GetImportSource(profile.Key);
        var remember = false;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            source = await PickFolderAsync();
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            remember = true;
        }

        if (!IsValidServerBuildDirectory(source, profile))
        {
            await ShowTextAsync(
                $"{profile.DisplayName} · 源目录不像服务端构建",
                $"选中的目录：{source}{Environment.NewLine}{Environment.NewLine}" +
                $"这里看不到 {profile.ServerExecutableName} 或 Engine\\ 目录。请选择 WindowsServer 构建目录。");
            return;
        }

        if (remember)
        {
            await viewModel.RememberImportSourceAsync(profile.Key, source, CancellationToken.None);
        }

        var import = await RunBusyAsync(
            $"{profile.DisplayName} · 导入新构建",
            "正在导入新构建（只改本机仓库）…",
            ct => viewModel.UpdateService.ImportBuildAsync(profile, source, null, ct));
        if (import is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("导入完成。下面是准备提交到本机 Git 的变更；点「提交」后才会进入推送。");
        report.AppendLine($"源目录：{source}");
        var sync = import.Sync;
        report.AppendLine(
            $"镜像结果：新增 {sync?.AddedFiles ?? 0} 个 · 更新 {sync?.UpdatedFiles ?? 0} 个 · " +
            $"删除 {sync?.RemovedFiles ?? 0} 个（{FormatSize(sync?.RemovedBytes ?? 0)}）");
        var changes = import.Changes ?? [];
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

        log?.Write(LogKind.Info, $"{profile.DisplayName} · 导入新构建 差异清单：{Environment.NewLine}{report}");
        var message = await ShowCommitDialogAsync(
            $"{profile.DisplayName} · 导入完成，确认提交",
            report.ToString(),
            profile.DefaultCommitSubject(DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        if (string.IsNullOrWhiteSpace(message))
        {
            log?.Write(LogKind.Info, $"{profile.DisplayName} 已镜像到本机仓库，但未提交。");
            return;
        }

        var commit = await RunBusyAsync(
            $"{profile.DisplayName} · 提交",
            "正在提交到本机仓库…",
            ct => viewModel.UpdateService.CommitAsync(profile, message, null, ct));
        if (commit is null)
        {
            return;
        }

        if (!commit.Committed)
        {
            await ShowTextAsync($"{profile.DisplayName} · 没有新变更", commit.Detail);
            return;
        }

        if (await ShowConfirmAsync(
                "立即推送？",
                $"已提交：{message}{Environment.NewLine}{Environment.NewLine}现在推送到服务器吗？推送过程中服务端照常运行。",
                "推送"))
        {
            await PushAsync(profile);
        }
    }

    private async Task PushAsync(ServerProgramProfile profile)
    {
        var status = await RunBusyAsync(
            $"{profile.DisplayName} · 推送新版本",
            "正在推送到服务器；服务端仍在运行，完成后还需要切换最新版本…",
            ct => viewModel.UpdateService.PushAsync(profile, null, ct));
        if (status is null)
        {
            return;
        }

        await ShowTextAsync(
            $"{profile.DisplayName} · 推送完成，下一步：切换最新版本",
            "服务器已收到新版本，服务端仍在运行。确认无误后点「切换最新版本」。" +
            Environment.NewLine + $"本机领先 {status.Ahead} 个提交。");
    }

    private async Task ApplyAsync(ServerProgramProfile profile)
    {
        if (_busy)
        {
            return;
        }

        if (!await ShowConfirmAsync(
                $"{profile.DisplayName} · 切换最新版本",
                "将执行：暂挂看门狗 → 停止服务端 → 切换到最新版本 → 启动 → 健康检查最长 420 秒 → 恢复看门狗。" +
                Environment.NewLine + Environment.NewLine +
                $"目标版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Main?.Describe() ?? "最新版本"}" +
                Environment.NewLine +
                $"受影响实例：{string.Join("、", profile.InstanceNames)}" +
                Environment.NewLine + "切换期间玩家会掉线。确定继续吗？",
                "开始切换"))
        {
            return;
        }

        var report = await RunBusyAsync(
            $"{profile.DisplayName} · 切换最新版本",
            "正在切换版本并重启；健康检查最长 7 分钟，请不要重复操作…",
            ct => viewModel.UpdateService.ApplyAsync(profile, ct),
            allowCancel: false);
        if (report is not null)
        {
            await ShowTextAsync($"{profile.DisplayName} · 切换结果", report.Describe());
        }
    }

    private async Task RollbackAsync(ServerProgramProfile profile)
    {
        if (_busy)
        {
            return;
        }

        if (!await ShowConfirmAsync(
                $"{profile.DisplayName} · 回退上个版本",
                "将把服务器切回上一个版本，流程与更新相同：暂挂看门狗 → 停服 → 切换 → 启动 → 健康检查。" +
                Environment.NewLine + Environment.NewLine +
                $"目标版本：{viewModel.GetUpdateState(profile.Key)?.Remote?.Previous?.Describe() ?? "上一个版本"}" +
                Environment.NewLine + "最新版本仍保留在服务器上，随时可以再切回去。确定回退吗？",
                "回退"))
        {
            return;
        }

        var report = await RunBusyAsync(
            $"{profile.DisplayName} · 回退上个版本",
            "正在回退版本并重启；健康检查最长 7 分钟，请不要重复操作…",
            ct => viewModel.UpdateService.RollbackAsync(profile, ct),
            allowCancel: false);
        if (report is not null)
        {
            await ShowTextAsync($"{profile.DisplayName} · 回退结果", report.Describe());
        }
    }

    private async Task RecoverAsync(ServerProgramProfile profile)
    {
        if (_busy)
        {
            return;
        }

        var state = viewModel.GetUpdateState(profile.Key);
        if (!await ShowConfirmAsync(
                $"{profile.DisplayName} · 恢复服务端",
                "用于切换失败、服务端停在维护模式或进程没起来的情况。" +
                Environment.NewLine + Environment.NewLine +
                "将强制对齐到当前 deployed 版本，重新启动并做健康检查，成功后解除维护模式；不会切到最新版本。" +
                Environment.NewLine +
                $"目标版本：{state?.Remote?.Deployed?.Describe() ?? "当前部署版本"}" +
                Environment.NewLine + "恢复期间玩家会掉线。确定继续吗？",
                "开始恢复"))
        {
            return;
        }

        var report = await RunBusyAsync(
            $"{profile.DisplayName} · 恢复服务端",
            "正在恢复服务端；对齐部署版本并等待健康检查，最长 7 分钟…",
            ct => viewModel.UpdateService.RecoverAsync(profile, ct),
            allowCancel: false);
        if (report is not null)
        {
            await ShowTextAsync($"{profile.DisplayName} · 恢复结果", report.Describe());
        }
    }

    private async Task PreviewAsync(ServerProgramProfile profile)
    {
        var preview = await RunBusyAsync(
            $"{profile.DisplayName} · 查看待更新内容",
            "正在读取本机仓库的提交与差异…",
            ct => viewModel.UpdateService.PreviewAsync(profile, null, ct));
        if (preview is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"本机仓库：{viewModel.LocalRepoRoot}");
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

        await ShowTextAsync($"{profile.DisplayName} · 待更新内容", report.ToString());
    }

    private async Task CleanupLocalRepoAsync(ServerProgramProfile profile)
    {
        if (!await ShowConfirmAsync(
                $"{profile.DisplayName} · 清理本地仓库",
                "将删除本机仓库里未推送的临时对象与历史，保留已推送的提交。确定继续吗？",
                "清理"))
        {
            return;
        }

        var result = await RunBusyAsync(
            $"{profile.DisplayName} · 清理本地仓库",
            "正在清理本机仓库…",
            ct => viewModel.UpdateService.CleanupLocalRepoAsync(profile, null, ct));
        if (result is not null)
        {
            await ShowTextAsync(
                $"{profile.DisplayName} · 清理完成",
                $"仓库体积：{FormatSize(result.GitBeforeBytes)} → {FormatSize(result.GitAfterBytes)}");
        }
    }

    // ---- 服务器级动作（日志 / 清理 / 监控） ----

    public async Task RunServerLevelActionAsync(string actionKey)
    {
        switch (actionKey)
        {
            case "watchdog-log":
                await ShowLogAsync("logs");
                break;
            case "old-logs":
                await ShowCleanupAsync("scan-old-logs", "clean-old-logs", "清除过期日志",
                    "将删除看门狗日志与 IIS 日志中超过 7 天的文件，正在写入的 watchdog.log 不受影响。");
                break;
            case "all-logs":
                await ShowCleanupAsync("scan-all-logs", "clean-all-logs", "清除全部日志",
                    "将删除看门狗日志与 IIS 日志的全部文件，正在写入的 watchdog.log 会被清空，历史内容无法恢复。");
                break;
            case "caches":
                await ShowCleanupAsync("scan-caches", "clean-caches", "清理缓存",
                    "将清空系统临时文件中超过 1 天的文件，并保留最新 5 份看门狗脚本备份。");
                break;
            case "crashes":
                await ShowCleanupAsync("scan-crashes", "clean-crashes", "清理崩溃文件",
                    "将删除各服务端 Saved\\Crashes 下较旧的目录，每个项目保留最近 20 个。");
                break;
            case "watchdog-task":
                await ToggleWatchdogAsync();
                break;
        }
    }

    // ---- 实例级动作 ----

    public async Task RunInstanceCommandAsync(string instanceName, string command)
    {
        var instance = viewModel.Instances.FirstOrDefault(
            item => string.Equals(item.Name, instanceName, StringComparison.Ordinal));
        if (instance is null)
        {
            return;
        }

        switch (command)
        {
            case "log":
                await ShowLogAsync("logs", instance.Name, $"{instance.DisplayName} 日志");
                break;
            case "start":
                await RunInstanceAsync(instance, "start", "启动");
                break;
            case "stop":
                await RunInstanceAsync(instance, "stop", "停止");
                break;
            case "restart":
                await RunInstanceAsync(instance, "restart", "重启");
                break;
        }
    }

    private async Task RunInstanceAsync(ServerInstanceViewModel instance, string action, string verb)
    {
        if (_busy)
        {
            return;
        }

        if (action is "stop" or "restart" &&
            !await ShowConfirmAsync(
                $"{verb} {instance.DisplayName}",
                $"将对 {instance.DisplayName}（{instance.EndpointText}）执行「{verb}」。确定继续吗？"))
        {
            return;
        }

        await RunActionAsync(instance.Name, action, $"{verb} {instance.DisplayName}");
        await viewModel.RefreshAsync(CancellationToken.None);
    }

    private async Task ToggleWatchdogAsync()
    {
        var state = await RunActionAsync(string.Empty, "watchdog-task-state", "读取监控状态");
        if (state?.Data is not { } data)
        {
            return;
        }

        var running = string.Equals(
            TryReadString(data, "state"), "Running", StringComparison.OrdinalIgnoreCase);
        var title = running ? "暂停看门狗监控" : "恢复看门狗监控";
        if (!await ShowConfirmAsync(
                title,
                running
                    ? "暂停后服务端崩溃将不再被自动拉起，直到恢复监控。确定继续吗？"
                    : "恢复看门狗监控，服务端异常时会被自动拉起。"))
        {
            return;
        }

        await RunActionAsync(string.Empty, running ? "watchdog-task-off" : "watchdog-task-on", title);
        await viewModel.RefreshAsync(CancellationToken.None);
    }

    private async Task ShowCleanupAsync(
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
                var id = TryReadString(target, "id");
                var path = TryReadString(target, "path");
                var exists = target.TryGetProperty("exists", out var existsNode) && existsNode.GetBoolean();
                var cleanable = ReadInt64(target, "cleanableBytes");
                var cleanableCount = ReadInt64(target, "cleanableCount");
                cleanableBytes += cleanable;
                report.AppendLine($"{DescribeCacheTarget(id)}：{path}");
                report.AppendLine(exists
                    ? $"    本次处理 {cleanableCount} 项（{FormatSize(cleanable)}）"
                    : "    目录不存在，跳过");
            }
        }

        report.AppendLine($"合计：{FormatSize(cleanableBytes)}");
        if (!await ShowConfirmAsync(title, report.ToString(), "确认执行"))
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
                var removedFiles = ReadInt64(target, "removedFiles");
                var removedBytes = ReadInt64(target, "removedBytes");
                removedTotal += removedBytes;
                summary.AppendLine(
                    $"{DescribeCacheTarget(TryReadString(target, "id"))}：处理 {removedFiles} 项，释放 {FormatSize(removedBytes)}");
            }
        }

        summary.AppendLine($"合计释放：{FormatSize(removedTotal)}");
        await ShowTextAsync($"{title} 完成", summary.ToString());
        await viewModel.RefreshAsync(CancellationToken.None);
    }

    private async Task ShowLogAsync(string action, string instanceName = "", string title = "看门狗日志")
    {
        var result = await RunActionAsync(instanceName, action, $"读取{title}");
        if (result?.Data is not { } data)
        {
            return;
        }

        var lines = ReadLines(data, "lines");
        var path = TryReadString(data, "path");
        var content = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 720,
            MaxHeight = 460
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = path, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    content
                }
            },
            CloseButtonText = "关闭"
        };
        await ShowDialogAsync(dialog);
    }

    private async Task<ServerActionResult?> RunActionAsync(string instanceName, string action, string title)
    {
        if (_busy)
        {
            return null;
        }

        StartProgress(title, $"正在执行：{title}…", allowCancel: false);
        try
        {
            var result = await viewModel.RunActionAsync(instanceName, action, CancellationToken.None);
            if (!result.Success)
            {
                log?.Write(LogKind.Error, $"{title}失败：{result.Message}");
                progress.Complete($"{title} 失败", result.Message);
                await ShowTextAsync($"{title} 失败", result.Message);
                return null;
            }

            log?.Write(LogKind.User, $"{title}已完成。");
            progress.Complete($"{title} 完成", result.Message);
            return result;
        }
        catch (Exception exception)
        {
            log?.Write(LogKind.Error, $"{title}失败。", exception);
            progress.Complete($"{title} 失败", exception.Message);
            await ShowTextAsync($"{title} 失败", exception.Message);
            return null;
        }
        finally
        {
            EndProgress();
        }
    }

    private async Task<T?> RunBusyAsync<T>(
        string title,
        string message,
        Func<CancellationToken, Task<T>> operation,
        bool allowCancel = true)
        where T : class
    {
        if (_busy)
        {
            return null;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = allowCancel ? cancellation : null;
        StartProgress(title, message, allowCancel);

        try
        {
            var service = viewModel.HasUpdateService ? viewModel.UpdateService : null;
            if (service is not null)
            {
                service.EventSink = ForwardServerGitEvent;
            }

            var result = await operation(cancellation.Token);
            log?.Write(LogKind.User, $"{title}已完成。");
            progress.Complete($"{title} 完成", "已刷新服务端状态。");
            return result;
        }
        catch (OperationCanceledException)
        {
            log?.Write(LogKind.User, $"{title}已取消。");
            progress.Complete($"{title} 已取消", "未完成的步骤已停止。");
            return null;
        }
        catch (Exception exception)
        {
            log?.Write(LogKind.Error, $"{title}失败。", exception);
            progress.Complete($"{title} 失败", exception.Message);
            return null;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            if (viewModel.HasUpdateService)
            {
                viewModel.UpdateService.EventSink = null;
            }

            EndProgress();
        }
    }

    /// <summary>脚本用 ::axtools 报阶段和百分比，这里转到全局进度条。</summary>
    private void ForwardServerGitEvent(AxTaskEvent taskEvent)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!_busy)
            {
                return;
            }

            switch (taskEvent.Type)
            {
                case AxTaskEventType.Progress:
                    progress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "正在执行…",
                        taskEvent.Percent ?? progress.Percent,
                        taskEvent.Detail));
                    break;
                case AxTaskEventType.Stage:
                    progress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "正在执行…",
                        taskEvent.Percent ?? progress.Percent,
                        taskEvent.Detail,
                        IsIndeterminate: taskEvent.Percent is null));
                    break;
                case AxTaskEventType.Warning:
                    log?.Write(LogKind.Warning, taskEvent.Message ?? taskEvent.Code ?? "脚本警告");
                    break;
            }
        });
    }

    private void StartProgress(string title, string message, bool allowCancel)
    {
        _busy = true;
        progress.Start(title, message);
        progress.SetCancellationMode(
            allowCancel ? AxTaskCancellationMode.Cancel : AxTaskCancellationMode.Locked,
            allowCancel ? "可以取消；已完成的步骤会保留。" : "执行期间请勿关闭窗口。");
    }

    private void EndProgress()
    {
        _busy = false;
        _ = HideProgressLaterAsync();
    }

    private async Task HideProgressLaterAsync()
    {
        await Task.Delay(1500);
        progress.Hide();
    }

    // ---- 对话框 ----

    private async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "继续")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = null,
                MaxHeight = 380,
                MinWidth = 480
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowTextAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBox
            {
                Text = body,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = null,
                MaxHeight = 380,
                MinWidth = 480
            },
            CloseButtonText = "关闭"
        };
        await ShowDialogAsync(dialog);
    }

    private async Task<string?> ShowCommitDialogAsync(string title, string body, string defaultMessage)
    {
        var messageBox = new TextBox { Text = defaultMessage, MinWidth = 460 };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBox
                    {
                        Text = body,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        BorderThickness = new Thickness(0),
                        Background = null,
                        MaxHeight = 300
                    },
                    new TextBlock { Text = "提交说明", Opacity = 0.7, FontSize = 12 },
                    messageBox
                }
            },
            PrimaryButtonText = "提交",
            CloseButtonText = "暂不提交",
            DefaultButton = ContentDialogButton.Primary
        };

        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary
            ? messageBox.Text.Trim()
            : null;
    }

    private static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog) =>
        await DialogGate.TryShowAsync(dialog) ?? ContentDialogResult.None;

    // ---- 辅助 ----

    private static async Task<string?> PickFolderAsync()
    {
        if (App.ShellWindow is null)
        {
            return null;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.ShellWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static bool IsValidServerBuildDirectory(string source, ServerProgramProfile profile) =>
        File.Exists(Path.Combine(source, profile.ServerExecutableName)) ||
        Directory.Exists(Path.Combine(source, "Engine"));

    private static string DescribeCacheTarget(string id) => id switch
    {
        "watchdog-log" => "看门狗日志",
        "iis-log" => "IIS 日志",
        "temp" => "系统临时文件",
        "watchdog-backup" => "看门狗脚本备份",
        "crashes" => "崩溃目录",
        _ => id
    };

    private static string TryReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetInt64()
            : 0;

    private static string[] ReadLines(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Array
            ? node.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };
}
