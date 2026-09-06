using System.Text;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AxTools;

public sealed partial class MainWindow
{
    private readonly ManagedProcessService _managedProcessService = new();

    private async void GlobalSettingsPage_StorageScanRequested(
        object? sender,
        EventArgs args)
    {
        await RunStorageOperationAsync(
            HostedTaskKind.StorageScan,
            "扫描存储空间",
            async (progress, cancellationToken) =>
            {
                await _viewModel.Settings.Storage.ScanAsync(progress, cancellationToken);
                return $"扫描完成，可管理空间 {_viewModel.Settings.Storage.TotalSizeText}。";
            });
    }

    private async void GlobalSettingsPage_StorageCleanupRequested(
        object? sender,
        EventArgs args)
    {
        var selectedItems = _viewModel.Settings.Storage.SelectedItems;
        if (selectedItems.Count == 0 ||
            !await EnsureNoStorageProcessConflictsAsync(selectedItems) ||
            !await ConfirmStorageCategoriesAsync(selectedItems) ||
            !await CloseProcessesForStorageItemsAsync(selectedItems))
        {
            return;
        }

        await RunStorageOperationAsync(
            HostedTaskKind.StorageCleanup,
            "清理所选存储项",
            async (progress, cancellationToken) =>
            {
                var executablePath = Environment.ProcessPath ??
                    Path.Combine(AppContext.BaseDirectory, "AxTools.exe");
                var result = await _viewModel.Settings.Storage.CleanupSelectedAsync(
                    executablePath,
                    progress,
                    cancellationToken);
                var summary = result.Failures.Count == 0
                    ? $"实际释放 {StorageItemViewModel.FormatBytes(result.ReleasedBytes)}。"
                    : $"释放 {StorageItemViewModel.FormatBytes(result.ReleasedBytes)}，失败 {result.Failures.Count} 项。";
                await _viewModel.Settings.Storage.ScanAsync(progress, cancellationToken);
                return summary;
            });
    }

    private async void GlobalSettingsPage_EnvironmentRescanRequested(
        object? sender,
        EventArgs args)
    {
        await RunStorageOperationAsync(
            HostedTaskKind.EnvironmentScan,
            "扫描开发环境",
            async (progress, cancellationToken) =>
                await _viewModel.Settings.RescanEnvironmentAsync(progress, cancellationToken));
    }

    private async Task RunStorageOperationAsync(
        HostedTaskKind taskKind,
        string title,
        Func<IProgress<ProgressUpdate>, CancellationToken, Task<string>> operation)
    {
        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，未启动存储操作。");
            return;
        }

        _activeHostedTaskKind = taskKind;
        _activeOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _windowTaskCancellation.Token);
        _viewModel.GlobalProgress.Start(title, "正在准备...");
        _viewModel.GlobalProgress.SetCancellationMode(AxTaskCancellationMode.Cancel);
        StartTaskElapsedTimer();
        var progress = new Progress<ProgressUpdate>(update =>
            _viewModel.GlobalProgress.Report(update));
        try
        {
            var summary = await operation(
                progress,
                _activeOperationCancellation.Token);
            _viewModel.GlobalProgress.Complete(title + "完成", summary);
            _viewModel.LogService.Write(LogKind.Info, summary);
        }
        catch (OperationCanceledException)
        {
            _viewModel.GlobalProgress.Complete(title + "已取消", "没有继续删除其他项目。");
            _viewModel.LogService.Write(
                LogKind.Warning,
                $"用户取消{title}；没有继续删除其他项目。");
        }
        catch (Exception exception)
        {
            _viewModel.GlobalProgress.Complete(title + "失败", exception.Message);
            _viewModel.LogService.Write(LogKind.Error, title + "失败。", exception);
        }
        finally
        {
            StopTaskElapsedTimer();
            await CompleteAndHideGlobalProgressAsync();
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private async Task<bool> ConfirmStorageCategoriesAsync(
        IReadOnlyList<StorageItemViewModel> selectedItems)
    {
        var expected = StorageItemViewModel.FormatBytes(
            selectedItems.Sum(item => item.Item.SizeBytes));
        var projects = string.Join("、", selectedItems
            .Select(item => item.ToolStableKey)
            .Distinct(StringComparer.Ordinal));
        var impacts = string.Join(Environment.NewLine, selectedItems
            .Select(item => $"- {item.DisplayName}：{item.Impact}")
            .Distinct(StringComparer.Ordinal));
        var warning = selectedItems.Any(item => item.Category == StorageCategory.ReleaseArtifact)
            ? "\n\n包含发布或暂存产物，请确认这些文件不再需要上传。"
            : selectedItems.Any(item => item.Category == StorageCategory.HighCostCache)
                ? "\n\n包含高成本缓存，下次构建可能需要重新下载依赖或重新编译。"
                : string.Empty;
        return await ShowConfirmationAsync(
            "清理预演",
            $"预计释放：{expected}\n受影响项目：{projects}\n\n重建代价：\n{impacts}{warning}\n\n执行前仍会重新校验固定白名单和扫描快照。",
            "清理所选");
    }

    private async Task<bool> EnsureNoStorageProcessConflictsAsync(
        IReadOnlyList<StorageItemViewModel> selectedItems)
    {
        var conflicts = new StorageProcessGuardService().FindConflicts(
            selectedItems.Select(item => item.Item).ToArray());
        if (conflicts.Count == 0)
        {
            return true;
        }

        var detail = string.Join(Environment.NewLine, conflicts.Select(conflict =>
            $"PID {conflict.Process.ProcessId}  {conflict.Process.ProcessName}  [{conflict.Ecosystem}]\n{conflict.Reason}"));
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "清理已被运行进程阻止",
            Content = detail + "\n\n请先正常结束相关构建、编辑器或设备任务，再重新扫描。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
        return false;
    }

    private async Task<bool> CloseProcessesForStorageItemsAsync(
        IReadOnlyList<StorageItemViewModel> selectedItems)
    {
        var matches = new Dictionary<int, ProcessSnapshot>();
        foreach (var item in selectedItems)
        {
            var tool = _viewModel.Tools.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Descriptor.StableKey,
                    item.ToolStableKey,
                    StringComparison.Ordinal));
            if (tool is null)
            {
                continue;
            }

            foreach (var executable in new[]
            {
                tool.DevelopmentExecutable,
                tool.ReleaseExecutable
            })
            {
                if (!IsPathInsideDirectory(executable, item.Path))
                {
                    continue;
                }

                foreach (var process in _managedProcessService.FindMatches(executable))
                {
                    if (process.ProcessId == Environment.ProcessId)
                    {
                        await ShowConfirmationAsync(
                            "当前程序目录受保护",
                            $"不能在 AxTools 运行时清理包含当前程序的目录：\n{item.Path}",
                            "知道了");
                        return false;
                    }

                    matches[process.ProcessId] = process;
                }
            }
        }

        if (matches.Count == 0)
        {
            return true;
        }

        var detail = new StringBuilder();
        foreach (var process in matches.Values)
        {
            detail.AppendLine($"PID {process.ProcessId}  {process.ExecutablePath}");
        }

        if (!await ShowConfirmationAsync(
            "需要关闭正在运行的程序",
            detail.ToString().TrimEnd(),
            "正常关闭"))
        {
            return false;
        }

        var forceRequired = new List<ProcessSnapshot>();
        foreach (var process in matches.Values)
        {
            var result = await _managedProcessService.RequestGracefulCloseAsync(
                process,
                _windowTaskCancellation.Token);
            if (result.Status == ManagedProcessCloseStatus.ForceRequired)
            {
                forceRequired.Add(process);
            }
            else if (result.Status is ManagedProcessCloseStatus.TargetChanged or ManagedProcessCloseStatus.Failed)
            {
                _viewModel.LogService.Write(LogKind.Warning, result.Message);
                return false;
            }
        }

        if (forceRequired.Count == 0)
        {
            return true;
        }

        if (!await ShowConfirmationAsync(
            "程序未正常退出",
            "强制结束可能导致尚未保存的数据丢失。是否强制结束这些程序？",
            "强制结束"))
        {
            return false;
        }

        foreach (var process in forceRequired)
        {
            var result = await _managedProcessService.ForceTerminateAsync(
                process,
                _windowTaskCancellation.Token);
            if (result.Status != ManagedProcessCloseStatus.Exited)
            {
                _viewModel.LogService.Write(LogKind.Warning, result.Message);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ShowConfirmationAsync(
        string title,
        string content,
        string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static bool IsPathInsideDirectory(string? candidate, string directory)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        var relative = Path.GetRelativePath(directory, Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
