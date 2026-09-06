using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Views;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AxTools;

public sealed partial class MainWindow
{
    private async void ToolPage_GamePackageRootSelectionRequested(
        object? sender,
        EventArgs args)
    {
        if (sender is not ToolPage { DataContext: Core.ViewModels.ToolPageViewModel tool })
        {
            return;
        }

        var folderPicker = new FolderPicker();
        InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(this));
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is not null)
        {
            tool.GamePackageRoot = folder.Path;
        }
    }

    private async void GlobalSettingsPage_ProjectRootSelectionRequested(
        object? sender,
        EventArgs args)
    {
        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，不能迁移整体项目目录。");
            return;
        }

        try
        {
            var folderPicker = new FolderPicker();
            InitializeWithWindow.Initialize(
                folderPicker,
                WindowNative.GetWindowHandle(this));
            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var targetRoot = Path.GetFullPath(Path.Combine(
                folder.Path,
                "Ax工具箱项目"));
            _activeHostedTaskKind = HostedTaskKind.ProjectRootMigration;
            _activeOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _windowTaskCancellation.Token);
            _viewModel.GlobalProgress.Start(
                "迁移整体项目目录",
                $"{_viewModel.Settings.ProjectRootPath} -> {targetRoot}");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Cancel,
                "复制和校验阶段可以安全取消。");
            StartTaskElapsedTimer();
            var progress = new Progress<ProgressUpdate>(update =>
                _viewModel.GlobalProgress.Report(update));

            try
            {
                var result = await _viewModel.Settings.ChangeProjectRootAsync(
                    targetRoot,
                    progress,
                    _activeOperationCancellation.Token);
                var detail = result.OldDirectoryDeleted
                    ? $"已迁移 {result.FileCount} 个文件，旧目录已删除。"
                    : $"已迁移 {result.FileCount} 个文件；旧目录待清理：{result.CleanupError}";
                _viewModel.GlobalProgress.Complete(
                    result.OldDirectoryDeleted
                        ? "整体项目目录迁移完成"
                        : "迁移完成，旧目录待清理",
                    detail);
                _viewModel.LogService.Write(
                    result.OldDirectoryDeleted
                        ? LogKind.Info
                        : LogKind.Warning,
                    detail);
            }
            catch (OperationCanceledException)
            {
                _viewModel.GlobalProgress.Complete(
                    "整体项目目录迁移已取消",
                    "原目录和设置已保留，旧目录未删除。");
                _viewModel.LogService.Write(
                    LogKind.Warning,
                    "用户取消整体项目目录迁移；原目录和设置已保留。");
            }
            catch (Exception exception)
            {
                _viewModel.GlobalProgress.Complete(
                    "整体项目目录迁移失败",
                    "原目录和设置已保留，旧目录未删除。");
                _viewModel.LogService.Write(
                    LogKind.Error,
                    "整体项目目录迁移失败。",
                    exception);
            }
            finally
            {
                StopTaskElapsedTimer();
                await CompleteAndHideGlobalProgressAsync();
            }
        }
        finally
        {
            _activeOperationCancellation?.Dispose();
            _activeOperationCancellation = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private void GlobalSettingsPage_ThemeChanged(object? sender, ThemeMode themeMode)
    {
        ApplyTheme(themeMode);
    }

    private async void GlobalSettingsPage_PathSelectionRequested(
        object? sender,
        PathSelectionRequestedEventArgs args)
    {
        if (args.PathKind is "DevelopmentExecutable" or "ReleaseExecutable")
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            if (args.PathKind == "DevelopmentExecutable")
            {
                args.Tool.DevelopmentExecutable = file.Path;
            }
            else
            {
                args.Tool.ReleaseExecutable = file.Path;
            }

            return;
        }

        var folderPicker = new FolderPicker();
        InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(this));
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        if (args.PathKind == "SourceRoot")
        {
            args.Tool.SourceRoot = folder.Path;
        }
    }

    private void ApplyTheme(ThemeMode themeMode)
    {
        RootGrid.RequestedTheme = themeMode switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }
}
