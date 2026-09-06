using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AxTools;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _windowTaskCancellation = new();
    private readonly HostedTaskCoordinator _hostedTaskCoordinator = new();
    private DispatcherQueueTimer? _taskElapsedTimer;
    private DateTimeOffset _taskStartedAt;
    private HostedTaskKind _activeHostedTaskKind;
    private string? _activeHostedTaskId;
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _closeAfterHostedTaskStops;
    private bool _isWaitingToClose;

    private async void GlobalSettingsPage_RunnerSelfTestRequested(
        object? sender,
        RunnerSelfTestRequestedEventArgs args)
    {
        var runner = _viewModel.TaskRunner;
        if (runner is null || !_viewModel.RunnerDiagnostics.CanStart)
        {
            return;
        }

        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，未启动任务运行器自检。");
            return;
        }

        _activeHostedTaskKind = HostedTaskKind.RunnerSelfTest;
        try
        {
            var scriptPath = Path.Combine(
                AppContext.BaseDirectory,
                "Scripts",
                "Diagnostics",
                "Invoke-RunnerSelfTest.ps1");
            var definition = new AxTaskDefinition(
                $"diagnostics-{args.Scenario}",
                args.DisplayName,
                scriptPath,
                new[] { "-Scenario", args.Scenario });

            _activeHostedTaskId = definition.Id;
            _viewModel.RunnerDiagnostics.Begin(args.Scenario, args.DisplayName);
            _viewModel.GlobalProgress.Start(args.DisplayName, "正在启动 PowerShell 7 标准脚本...");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Stop,
                "任务进入全局重任务串行队列。");
            StartTaskElapsedTimer();

            AxTaskResult result;
            try
            {
                result = await runner.RunAsync(definition, _windowTaskCancellation.Token);
            }
            catch (Exception exception)
            {
                result = CreateFailedTaskResult(definition, exception);
                _viewModel.LogService.Write(
                    LogKind.Error,
                    "任务运行器自检发生未处理错误。",
                    exception);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            await SaveTaskHistoryAsync(result);
            _viewModel.RunnerDiagnostics.Complete(result);
            var terminalTitle = result.Status switch
            {
                AxTaskStatus.Succeeded => "自检成功",
                AxTaskStatus.Stopped => "自检已停止",
                _ => "自检失败"
            };
            _viewModel.GlobalProgress.Complete(terminalTitle, result.Summary);
            await CompleteAndHideGlobalProgressAsync();
        }
        finally
        {
            _activeHostedTaskId = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private void TaskRunner_EventReceived(AxTaskEvent taskEvent)
    {
        var taskId = _viewModel.TaskRunner?.CurrentTask?.Id;
        var taskKind = _activeHostedTaskKind;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.Equals(taskId, _activeHostedTaskId, StringComparison.Ordinal))
            {
                return;
            }

            if (taskKind == HostedTaskKind.RunnerSelfTest)
            {
                _viewModel.RunnerDiagnostics.ReportEvent(taskEvent);
            }

            switch (taskEvent.Type)
            {
                case AxTaskEventType.Progress:
                    _viewModel.GlobalProgress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "任务正在运行...",
                        taskEvent.Percent ?? _viewModel.GlobalProgress.Percent,
                        taskEvent.Detail));
                    break;
                case AxTaskEventType.Stage:
                    _viewModel.GlobalProgress.Report(new ProgressUpdate(
                        taskEvent.Message ?? "正在切换任务阶段...",
                        _viewModel.GlobalProgress.Percent,
                        taskEvent.Detail));
                    break;
                case AxTaskEventType.Cancellation when taskEvent.CancellationMode is { } mode:
                    _viewModel.GlobalProgress.SetCancellationMode(mode, taskEvent.Message);
                    break;
                case AxTaskEventType.Warning:
                    _viewModel.LogService.Write(
                        LogKind.Warning,
                        taskEvent.Message ?? taskEvent.Code ?? "脚本警告");
                    break;
                case AxTaskEventType.Artifact:
                    _viewModel.LogService.Write(
                        LogKind.Info,
                        $"任务产物：{taskEvent.Path}");
                    break;
            }
        });
    }

    private void TaskRunner_OutputReceived(AxTaskParsedLine output)
    {
        var taskId = _viewModel.TaskRunner?.CurrentTask?.Id;
        var taskKind = _activeHostedTaskKind;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.Equals(taskId, _activeHostedTaskId, StringComparison.Ordinal))
            {
                return;
            }

            if (output.ProtocolError is not null)
            {
                if (taskKind == HostedTaskKind.RunnerSelfTest)
                {
                    _viewModel.RunnerDiagnostics.ReportProtocolWarning(output.ProtocolError);
                }

                _viewModel.LogService.Write(
                    LogKind.Warning,
                    $"已忽略无效脚本事件：{output.ProtocolError}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(output.Text))
            {
                if (IsPowerShellProgressSerializationNoise(output.Text))
                {
                    return;
                }
                _viewModel.LogService.Write(LogKind.Info, output.Text);
            }
        });
    }

    private static bool IsPowerShellProgressSerializationNoise(string text)
    {
        var trimmed = text.TrimStart();
        return (trimmed.StartsWith("#< CLIXML", StringComparison.Ordinal) ||
                trimmed.StartsWith("<Objs Version=", StringComparison.Ordinal)) &&
               trimmed.Contains("S=\"progress\"", StringComparison.Ordinal);
    }

    private void CancelGlobalTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeOperationCancellation is not null &&
            _activeHostedTaskKind is (
                HostedTaskKind.ProjectRootMigration or
                HostedTaskKind.StorageScan or
                HostedTaskKind.StorageCleanup or
                HostedTaskKind.EnvironmentScan))
        {
            _activeOperationCancellation.Cancel();
            _viewModel.GlobalProgress.MarkStopRequested();
            _viewModel.LogService.Write(
                LogKind.User,
                "用户请求取消当前本机操作。");
            return;
        }

        if (_viewModel.TaskRunner?.TryRequestStop() == true)
        {
            if (_activeHostedTaskKind == HostedTaskKind.RunnerSelfTest)
            {
                _viewModel.RunnerDiagnostics.MarkStopRequested();
            }

            _viewModel.GlobalProgress.MarkStopRequested();
            _viewModel.LogService.Write(LogKind.User, "用户请求停止当前任务。");
            return;
        }

        _viewModel.LogService.Write(
            LogKind.Warning,
            "当前任务阶段不可停止，未发送结束进程信号。");
    }

    private void BeginCloseWhenHostedTaskStops()
    {
        if (_isWaitingToClose)
        {
            return;
        }

        _isWaitingToClose = true;
        _ = CloseWhenHostedTaskStopsAsync();
    }

    private async Task CloseWhenHostedTaskStopsAsync()
    {
        await _hostedTaskCoordinator.WaitForIdleAsync(CancellationToken.None);
        _closeAfterHostedTaskStops = true;
        _isWaitingToClose = false;
        DispatcherQueue.TryEnqueue(Close);
    }

    private void StartTaskElapsedTimer()
    {
        _taskStartedAt = DateTimeOffset.Now;
        _taskElapsedTimer?.Stop();
        _taskElapsedTimer = DispatcherQueue.CreateTimer();
        _taskElapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _taskElapsedTimer.Tick += TaskElapsedTimer_Tick;
        _taskElapsedTimer.Start();
        TaskElapsedTimer_Tick(_taskElapsedTimer, EventArgs.Empty);
    }

    private void StopTaskElapsedTimer()
    {
        if (_taskElapsedTimer is null)
        {
            return;
        }

        _taskElapsedTimer.Stop();
        _taskElapsedTimer.Tick -= TaskElapsedTimer_Tick;
        _taskElapsedTimer = null;
    }

    private void TaskElapsedTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = DateTimeOffset.Now - _taskStartedAt;
        _viewModel.GlobalProgress.ElapsedText = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    private AxTaskResult CreateFailedTaskResult(
        AxTaskDefinition definition,
        Exception exception)
    {
        var now = DateTimeOffset.Now;
        return new AxTaskResult(
            definition.Id,
            AxTaskStatus.Failed,
            -1,
            _taskStartedAt,
            now,
            exception.Message,
            new[] { exception.ToString() },
            Array.Empty<AxTaskEvent>());
    }

    private async Task SaveTaskHistoryAsync(AxTaskResult result)
    {
        if (_viewModel.TaskHistoryService is null)
        {
            return;
        }

        try
        {
            var history = await _viewModel.TaskHistoryService.SaveAsync(
                result,
                CancellationToken.None);
            _viewModel.LogService.Write(
                LogKind.Info,
                $"任务历史已保存：{history.SummaryPath}");
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "任务已完成，但任务历史保存失败。",
                exception);
        }
    }

    private enum HostedTaskKind
    {
        None,
        RunnerSelfTest,
        ToolAction,
        ProjectRootMigration,
        StorageScan,
        StorageCleanup
        ,EnvironmentScan
        ,CrossingVoidPackage
        ,Update
        ,Release
        ,ProjectClone
    }
}
