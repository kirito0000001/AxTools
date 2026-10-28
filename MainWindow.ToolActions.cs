using System.Diagnostics;
using System.Text.Json;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using AxTools.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AxTools;

public sealed partial class MainWindow
{
    private readonly ProcessConflictDetector _processConflictDetector = new();

    private async void ToolPage_ToolActionRequested(
        object? sender,
        ToolActionRequestedEventArgs args)
    {
        var runner = _viewModel.TaskRunner;
        if (runner is null || !args.Action.IsEnabled)
        {
            return;
        }

        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，未启动新的工具动作。");
            return;
        }

        try
        {
            // 版本门禁、请求组装、确认内容都下沉到 Core：这里只负责显示与放行。
            var preflight = await args.ViewModel.PrepareActionAsync(
                args.Action.Action,
                _windowTaskCancellation.Token);
            if (!preflight.Allowed || preflight.Request is null)
            {
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"{args.ViewModel.DisplayName} · {args.Action.DisplayName} 未通过预检：{preflight.BlockedMessage}");
                await ShowMessageDialogAsync(preflight.BlockedTitle, preflight.BlockedMessage);
                return;
            }

            var request = preflight.Request;
            if (args.Action.RequiresConfirmation &&
                preflight.Confirmation is { } confirmation &&
                !await ShowActionConfirmationAsync(confirmation))
            {
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"用户取消真实发布：{args.ViewModel.DisplayName} · {args.Action.DisplayName}");
                return;
            }

            IReadOnlyList<ProcessSnapshot> DetectConflicts() =>
                args.ViewModel.Descriptor.Key == ManagedToolKey.AxTools
                    ? _processConflictDetector.FindConflicts(
                        AxToolsBuildProtectionPolicy.GetConflictExecutablePaths(
                            args.Action.Action,
                            args.ViewModel.SourceRoot,
                            args.ViewModel.DevelopmentExecutable,
                            Environment.ProcessPath))
                    : _processConflictDetector.FindConflictsForAction(
                        args.Action.Action,
                        args.ViewModel.DevelopmentExecutable,
                        args.ViewModel.ReleaseExecutable);

            var conflicts = DetectConflicts();
            if (conflicts.Count > 0)
            {
                if (CanUseExternalSelfRebuild(
                    args.ViewModel,
                    args.Action.Action,
                    conflicts))
                {
                    await TryStartExternalSelfRebuildAsync(args.ViewModel, args.Action);
                    return;
                }

                if (!await ShowProcessConflictDialogAsync(args.ViewModel, conflicts))
                {
                    _viewModel.LogService.Write(
                        LogKind.Warning,
                        $"检测到正在运行的目标 EXE，已阻止 {args.ViewModel.DisplayName} · {args.Action.DisplayName}。");
                    return;
                }

                // 用户选了"强制关闭并继续"：结束这些进程**以及它们所在的开发会话**，然后重新检查一遍。
                var survivors = await ForceCloseTargetsAsync(conflicts, _windowTaskCancellation.Token);
                var closedCount = conflicts.Count - survivors.Count;
                foreach (var conflict in conflicts)
                {
                    var killed = !survivors.Any(item => item.ProcessId == conflict.ProcessId);
                    _viewModel.LogService.Write(
                        killed ? LogKind.User : LogKind.Warning,
                        killed
                            ? $"已强制结束：{conflict.ProcessName} (PID {conflict.ProcessId})"
                            : $"强制结束失败：{conflict.ProcessName} (PID {conflict.ProcessId})");
                }

                if (survivors.Count > 0)
                {
                    await ShowMessageDialogAsync(
                        "强制关闭失败",
                        $"有 {survivors.Count} 个进程没能结束（通常是权限问题），已取消本次操作：{Environment.NewLine}{Environment.NewLine}" +
                        string.Join(
                            Environment.NewLine,
                            survivors.Select(item => $"{item.ProcessName} (PID {item.ProcessId})")));
                    return;
                }

                // 有些程序会立刻自我重启，所以再查一次。
                conflicts = DetectConflicts();
                if (conflicts.Count > 0)
                {
                    await ShowMessageDialogAsync(
                        "仍有目标程序在运行",
                        $"刚结束的进程又重新起来了，已取消本次操作：{Environment.NewLine}{Environment.NewLine}" +
                        string.Join(
                            Environment.NewLine,
                            conflicts.Select(item => $"{item.ProcessName} (PID {item.ProcessId})")));
                    return;
                }

                _viewModel.LogService.Write(
                    LogKind.User,
                    $"已强制关闭 {closedCount} 个目标进程（含子进程），继续执行 {args.ViewModel.DisplayName} · {args.Action.DisplayName}。");
            }

            AxTaskDefinition definition;
            try
            {
                definition = args.ViewModel.CreateTask(request);
            }
            catch (Exception exception)
            {
                _viewModel.LogService.Write(
                    LogKind.Error,
                    $"{args.ViewModel.DisplayName} 任务创建失败。",
                    exception);
                await ShowMessageDialogAsync("任务创建失败", exception.Message);
                return;
            }

            _activeHostedTaskKind = HostedTaskKind.ToolAction;
            _activeHostedTaskId = definition.Id;
            await RunToolActionAsync(
                runner,
                args.ViewModel,
                args.Action,
                definition);
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(
                LogKind.Error,
                $"{args.ViewModel.DisplayName} 动作处理发生未处理错误。",
                exception);
            await ShowMessageDialogAsync("动作处理失败", exception.Message);
        }
        finally
        {
            _activeHostedTaskId = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private static bool CanUseExternalSelfRebuild(
        ToolPageViewModel tool,
        ManagedToolAction action,
        IReadOnlyList<ProcessSnapshot> conflicts) =>
        tool.Descriptor.Key == ManagedToolKey.AxTools &&
        action is (ManagedToolAction.Build or
            ManagedToolAction.BuildAndRun or
            ManagedToolAction.ForceBuildAndRun) &&
        conflicts.Count == 1 &&
        conflicts[0].ProcessId == Environment.ProcessId;

    private async Task<bool> TryStartExternalSelfRebuildAsync(
        ToolPageViewModel tool,
        ToolActionItemViewModel action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "外部自重建 AxTools",
            Content = new TextBlock
            {
                Text = "当前 AxTools 正在使用 Debug 输出。继续后将启动外部构建助手并关闭本窗口；助手会备份旧版、编译并校验新版本，然后自动启动。构建失败时将恢复并重启旧版。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "关闭并重新编译",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await TryShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        await _viewModel.Settings.SaveAsync(CancellationToken.None);
        var rebuildRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxTools",
            "SelfRebuild");
        Directory.CreateDirectory(rebuildRoot);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var request = new SelfRebuildLaunchRequest(
            tool.SourceRoot,
            tool.DevelopmentExecutable,
            Environment.ProcessId,
            Path.Combine(rebuildRoot, $"ready-{Guid.NewGuid():N}.signal"),
            Path.Combine(rebuildRoot, $"rebuild-{timestamp}.log"),
            SelfRebuildResultService.GetDefaultResultPath());
        var scriptPath = Path.Combine(
            tool.SourceRoot,
            "Scripts",
            "Development",
            "Rebuild-AxTools.ps1");
        var launcher = new AxToolsSelfRebuildLauncher(
            new PowerShellLocator().FindPowerShell(),
            scriptPath);
        using var helper = await launcher.LaunchAsync(
            request,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        _viewModel.LogService.Write(
            LogKind.User,
            $"外部自重建助手已就绪，正在关闭 AxTools。动作：{action.DisplayName}；日志：{request.LogPath}");
        BeginCloseWhenHostedTaskStops();
        return true;
    }

    private async Task RunToolActionAsync(
        AxTaskRunner runner,
        ToolPageViewModel tool,
        ToolActionItemViewModel action,
        AxTaskDefinition definition)
    {
        tool.BeginAction(action);
        _viewModel.GlobalProgress.Start(
            definition.Title,
            "正在启动 PowerShell 7 标准脚本...");
        _viewModel.GlobalProgress.SetCancellationMode(
            AxTaskCancellationMode.Stop,
            "任务进入全局重任务串行队列。");
        _viewModel.LogService.Write(
            LogKind.User,
            $"开始执行：{definition.Title}");
        StartTaskElapsedTimer();

        AxTaskResult result;
        try
        {
            result = await runner.RunAsync(
                definition,
                _windowTaskCancellation.Token);
        }
        catch (Exception exception)
        {
            result = CreateFailedTaskResult(definition, exception);
            _viewModel.LogService.Write(
                LogKind.Error,
                $"{definition.Title} 发生未处理错误。",
                exception);
        }
        finally
        {
            StopTaskElapsedTimer();
        }

        await SaveTaskHistoryAsync(result);
        tool.CompleteAction(result);
        var terminalTitle = result.Status switch
        {
            AxTaskStatus.Succeeded => $"{action.DisplayName}成功",
            AxTaskStatus.Stopped => $"{action.DisplayName}已停止",
            _ => $"{action.DisplayName}失败"
        };
        _viewModel.GlobalProgress.Complete(terminalTitle, result.Summary);
        _viewModel.LogService.Write(
            result.Status switch
            {
                AxTaskStatus.Failed => LogKind.Error,
                AxTaskStatus.Stopped => LogKind.Warning,
                _ => LogKind.Info
            },
            $"{definition.Title}：{result.CreateDiagnosticLogText()}");
        await CompleteAndHideGlobalProgressAsync();
    }

    /// <summary>
    /// 按 Core 给的确认内容渲染弹窗：行由 <see cref="ToolActionConfirmation"/> 决定，
    /// 这里不再判断动作类型，也不再拼文案。
    /// </summary>
    private async Task<bool> ShowActionConfirmationAsync(ToolActionConfirmation confirmation)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var line in confirmation.Lines)
        {
            content.Children.Add(CreateDialogLine(line.Label, line.Value));
        }

        if (!string.IsNullOrWhiteSpace(confirmation.Note))
        {
            content.Children.Add(new TextBlock
            {
                Text = confirmation.Note,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = confirmation.Title,
            Content = content,
            PrimaryButtonText = confirmation.PrimaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await TryShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 强制结束目标进程**以及它们所在的开发会话**。
    ///
    /// 为什么不能只杀目标：Tauri dev / Vite 的 watcher 会在应用被杀掉后立刻把它重新拉起来
    /// （2026-09-20 实测：杀完 1 秒，同样的可执行文件又出现），所以必须沿父进程链找到会话根
    /// （cmd → node → cargo → 应用），再 taskkill /T 结束整棵树。
    /// 真正干活的是 Scripts\Common\Stop-TargetProcessTree.ps1 —— C# 看不到父进程，PowerShell 能。
    ///
    /// 返回结束后仍然活着的进程（空 = 干净）。
    /// </summary>
    private async Task<IReadOnlyList<ProcessSnapshot>> ForceCloseTargetsAsync(
        IReadOnlyList<ProcessSnapshot> conflicts,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Scripts",
            "Common",
            "Stop-TargetProcessTree.ps1");
        if (!File.Exists(scriptPath))
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                $"找不到 {scriptPath}，这次只能结束目标进程本身（可能被 watcher 重新拉起）。");
            return ProcessConflictDetector.ForceClose(conflicts);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = new PowerShellLocator().FindPowerShell(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-TargetProcessId");
        startInfo.ArgumentList.Add(string.Join(',', conflicts.Select(item => item.ProcessId)));

        var survivors = new List<ProcessSnapshot>();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ProcessConflictDetector.ForceClose(conflicts);
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            foreach (var line in standardOutput.Split('\n'))
            {
                var text = line.Trim();
                if (!text.StartsWith('{'))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("Chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chain in chains.EnumerateArray())
                    {
                        var parts = chain.TryGetProperty("Chain", out var chainValue) && chainValue.ValueKind == JsonValueKind.Array
                            ? string.Join(" ← ", chainValue.EnumerateArray().Select(item => item.GetString()))
                            : string.Empty;
                        _viewModel.LogService.Write(LogKind.Info, $"目标进程来源链：{parts}");
                    }
                }

                if (root.TryGetProperty("Killed", out var killed) && killed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in killed.EnumerateArray())
                    {
                        _viewModel.LogService.Write(
                            LogKind.User,
                            $"已强制结束：{item.GetProperty("Name").GetString()} (PID {item.GetProperty("Id").GetInt32()})　{item.GetProperty("Result").GetString()}");
                    }
                }

                if (root.TryGetProperty("Survivors", out var survivorNodes) && survivorNodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in survivorNodes.EnumerateArray())
                    {
                        survivors.Add(new ProcessSnapshot(
                            item.GetProperty("Id").GetInt32(),
                            item.TryGetProperty("Name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                            item.TryGetProperty("Path", out var path) ? path.GetString() ?? string.Empty : string.Empty));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(standardError))
            {
                _viewModel.LogService.Write(LogKind.Warning, $"强制结束脚本输出：{standardError.Trim()}");
            }
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(LogKind.Warning, "强制结束目标进程时出错，已退回只结束目标本身。", exception);
            return ProcessConflictDetector.ForceClose(conflicts);
        }

        return survivors;
    }

    private async Task<bool> ShowProcessConflictDialogAsync(
        ToolPageViewModel tool,
        IReadOnlyList<ProcessSnapshot> conflicts)
    {
        var details = string.Join(
            Environment.NewLine,
            conflicts.Select(conflict =>
                $"{conflict.ProcessName} (PID {conflict.ProcessId}){Environment.NewLine}{conflict.ExecutablePath}"));
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "目标程序正在运行",
                Content = new TextBlock
                {
                    Text =
                        $"{tool.DisplayName} 的开发版或正式版 EXE 正在运行。为避免覆盖运行中的程序，默认会取消本次操作。" +
                        $"{Environment.NewLine}{Environment.NewLine}" +
                        "要强制关闭它们并继续吗？（进程会被结束，未保存的进度会丢失）" +
                        $"{Environment.NewLine}{Environment.NewLine}{details}",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "强制关闭并继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await TryShowDialogAsync(dialog) == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close
            };
            await TryShowDialogAsync(dialog);
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(
                LogKind.Error,
                "创建操作提示对话框失败。",
                exception);
        }
    }

    private async Task<ContentDialogResult?> TryShowDialogAsync(ContentDialog dialog)
    {
        // 闸门与异常兜底都在 DialogGate 里：抢不到就返回 null，调用方按"没确认/没成功"处理。
        // 页面自己弹的窗（ServerPage / ToolPage / SettingsPage）也走同一道闸门，
        // 否则"主窗口的提示 + 页面的确认框"撞在一起照样崩（同一个 XamlRoot，一个线程只能有一个弹窗）。
        dialog.RequestedTheme = RootGrid.ActualTheme;
        return await DialogGate.TryShowAsync(dialog, _viewModel.LogService);
    }

    private static TextBlock CreateDialogLine(string label, string value) =>
        new()
        {
            Text = $"{label}：{value}",
            TextWrapping = TextWrapping.Wrap
        };

}
