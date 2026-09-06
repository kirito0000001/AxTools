using AxTools.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AxTools;

public sealed partial class MainWindow
{
    private const double LogWheelStep = 150;
    private DispatcherQueueTimer? _logTipTimer;

    private void InitializeLogPanel()
    {
        _viewModel.LogService.EntryWritten += LogService_EntryWritten;
        _viewModel.LogService.FileWriteFailed += LogService_FileWriteFailed;
        _viewModel.Settings.SettingsSaveFailed += Settings_SettingsSaveFailed;
    }

    private void UninitializeLogPanel()
    {
        _viewModel.LogService.EntryWritten -= LogService_EntryWritten;
        _viewModel.LogService.FileWriteFailed -= LogService_FileWriteFailed;
        _viewModel.Settings.SettingsSaveFailed -= Settings_SettingsSaveFailed;
        _logTipTimer?.Stop();
    }

    private void LogService_EntryWritten(object? sender, LogEntry entry) =>
        DispatcherQueue.TryEnqueue(ScrollLogToBottom);

    private void LogService_FileWriteFailed(object? sender, Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
            ShowLogTip($"文件日志保存失败：{exception.Message}"));

    private void Settings_SettingsSaveFailed(object? sender, Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
            ShowLogTip($"设置即时保存失败：{exception.Message}"));

    private void ScrollLogToBottomButton_Click(object sender, RoutedEventArgs e) =>
        ScrollLogToBottom();

    private void CopyAllLogButton_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            _viewModel.Logs.Select(entry => entry.CopyText));
        if (!string.IsNullOrWhiteSpace(text))
        {
            CopyLogText(text, "已复制全部日志");
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Logs.Clear();
        _viewModel.LogService.Write(LogKind.User, "已清空输出日志。");
    }

    private void LogEntryBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { DataContext: LogEntry entry })
        {
            CopyLogText(entry.CopyText, "已复制日志");
            e.Handled = true;
        }
    }

    private void LogScrollViewer_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        var delta = e
            .GetCurrentPoint(LogScrollViewer)
            .Properties
            .MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var targetOffset = Math.Clamp(
            LogScrollViewer.VerticalOffset - Math.Sign(delta) * LogWheelStep,
            0,
            LogScrollViewer.ScrollableHeight);
        LogScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: targetOffset,
            zoomFactor: null,
            disableAnimation: false);
        e.Handled = true;
    }

    private void ScrollLogToBottom() =>
        LogScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: LogScrollViewer.ScrollableHeight,
            zoomFactor: null,
            disableAnimation: false);

    private void CopyLogText(string text, string confirmation)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        ShowLogTip(confirmation);
    }

    private void ShowLogTip(string title)
    {
        LogTeachingTip.Title = title;
        LogTeachingTip.IsOpen = true;
        _logTipTimer ??= CreateLogTipTimer();
        _logTipTimer.Stop();
        _logTipTimer.Start();
    }

    private DispatcherQueueTimer CreateLogTipTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => LogTeachingTip.IsOpen = false;
        return timer;
    }
}
