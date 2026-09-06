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
    private bool _isDialogOpen;

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
            var versionValidation = await args.ViewModel.ValidatePublishingVersionAsync(
                args.Action.Action,
                _windowTaskCancellation.Token);
            if (!versionValidation.IsAllowed)
            {
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"已阻止倒序打包或发布：{args.ViewModel.DisplayName} · {versionValidation.Message}");
                await ShowMessageDialogAsync("版本检查未通过", versionValidation.Message);
                return;
            }

            ManagedToolActionRequest request;
            try
            {
                request = args.ViewModel.CreateRequest(args.Action.Action);
            }
            catch (Exception exception)
            {
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"{args.ViewModel.DisplayName} 动作参数无效。",
                    exception);
                await ShowMessageDialogAsync("无法创建任务", exception.Message);
                return;
            }

            if (args.Action.RequiresConfirmation &&
                !await ConfirmRealPublishAsync(args.ViewModel, request))
            {
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"用户取消真实发布：{args.ViewModel.DisplayName} · {args.Action.DisplayName}");
                return;
            }

            var conflicts = args.ViewModel.Descriptor.Key == ManagedToolKey.AxTools
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

                await ShowProcessConflictDialogAsync(args.ViewModel, conflicts);
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"检测到正在运行的目标 EXE，已阻止 {args.ViewModel.DisplayName} · {args.Action.DisplayName}。");
                return;
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

    private async Task<bool> ConfirmRealPublishAsync(
        ToolPageViewModel tool,
        ManagedToolActionRequest request)
    {
        if (request.Action == ManagedToolAction.ReplaceRelease)
        {
            var pendingService = new PendingPackageService();
            if (!pendingService.TryGetValidSummary(
                tool.StableKey,
                tool.OutputRoot,
                out var pending,
                out var validationMessage))
            {
                await ShowMessageDialogAsync("无法替换正式版", validationMessage);
                return false;
            }

            var replacementContent = new StackPanel { Spacing = 8 };
            replacementContent.Children.Add(CreateDialogLine("工具", tool.DisplayName));
            replacementContent.Children.Add(CreateDialogLine("版本", pending!.Version));
            replacementContent.Children.Add(CreateDialogLine("已验证临时包", pending.PackageRoot));
            replacementContent.Children.Add(CreateDialogLine("正式版目标", pending.TargetRoot));
            replacementContent.Children.Add(new TextBlock
            {
                Text = "替换期间旧目录会被临时改名；新目录就位后立即删除临时备份。若操作失败，AxTools 会恢复旧正式版。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            var replacementDialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认替换正式版",
                Content = replacementContent,
                PrimaryButtonText = "替换正式版",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await TryShowDialogAsync(replacementDialog) == ContentDialogResult.Primary;
        }

        var isGamePublishingAction = request.Action is
            ManagedToolAction.BuildGameChunks or
            ManagedToolAction.UploadGameChunks or
            ManagedToolAction.PublishGamePackage;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(CreateDialogLine("工具", tool.DisplayName));
        content.Children.Add(CreateDialogLine(
            isGamePublishingAction ? "游戏版本" : "启动器版本",
            string.IsNullOrWhiteSpace(request.Version) ? "未填写" : request.Version));
        content.Children.Add(CreateDialogLine("通道", request.Channel));
        content.Children.Add(CreateDialogLine(
            "输出目录",
            string.IsNullOrWhiteSpace(tool.OutputRoot) ? "未配置" : tool.OutputRoot));
        if (isGamePublishingAction)
        {
            content.Children.Add(CreateDialogLine("游戏目录", tool.GamePackageRoot));
        }
        content.Children.Add(CreateDialogLine("发布目标", GetPublishTarget(tool.Descriptor.Key)));
        content.Children.Add(new TextBlock
        {
            Text = isGamePublishingAction
                ? "这是实际上传操作。对应版本 Release 的全部旧附件会先被清空，再上传本次分片；请确认版本和目录无误。"
                : "这是实际上传操作，将写入远端发布目标。请确认以上信息无误。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "确认真实上传",
            Content = content,
            PrimaryButtonText = "确认真实上传",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await TryShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowProcessConflictDialogAsync(
        ToolPageViewModel tool,
        IReadOnlyList<ProcessSnapshot> conflicts)
    {
        var details = string.Join(
            Environment.NewLine,
            conflicts.Select(conflict =>
                $"{conflict.ProcessName} (PID {conflict.ProcessId}){Environment.NewLine}{conflict.ExecutablePath}"));
        await ShowMessageDialogAsync(
            "目标程序正在运行",
            $"{tool.DisplayName} 的开发版或正式版 EXE 正在运行。为避免覆盖运行中的程序，本次操作已取消；AxTools 不会关闭或结束这些进程。{Environment.NewLine}{Environment.NewLine}{details}");
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
        if (_isDialogOpen)
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有对话框正在显示，已忽略重复提示。");
            return null;
        }

        _isDialogOpen = true;
        try
        {
            dialog.RequestedTheme = RootGrid.ActualTheme;
            return await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(
                LogKind.Error,
                "显示操作提示对话框失败。",
                exception);
            return null;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private static TextBlock CreateDialogLine(string label, string value) =>
        new()
        {
            Text = $"{label}：{value}",
            TextWrapping = TextWrapping.Wrap
        };

    private static string GetPublishTarget(ManagedToolKey key) => key switch
    {
        ManagedToolKey.FantasyTools => "FantasyTools 的 GitHub / Gitee 发布资产",
        ManagedToolKey.CrossingVoidPc => "零境启动器 PC 的 Gitee 发布资产",
        ManagedToolKey.CrossingVoidAndroid => "零境启动器 Android 的 Gitee 发布资产",
        _ => "该工具配置的远端发布目标"
    };
}
