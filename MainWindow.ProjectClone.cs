using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using AxTools.Views;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AxTools;

public sealed partial class MainWindow
{
    private async void ToolPage_DownloadLinkRequested(
        object? sender,
        ToolDownloadRequestedEventArgs args)
    {
        var url = args.ViewModel.GiteeReleasesUrl;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            _viewModel.LogService.Write(
                LogKind.Info,
                $"{args.ViewModel.DisplayName} · Gitee 下载链接已复制：{url}");
            await ShowMessageDialogAsync("下载链接已复制", $"已复制 Gitee 正式版页面链接：{Environment.NewLine}{url}");
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(LogKind.Error, "复制 Gitee 下载链接失败。", exception);
            await ShowMessageDialogAsync("复制链接失败", exception.Message);
        }
    }

    private async void ToolPage_ProjectDownloadRequested(
        object? sender,
        ToolDownloadRequestedEventArgs args)
    {
        var tool = args.ViewModel;
        var runner = _viewModel.TaskRunner;
        if (runner is null || !_hostedTaskCoordinator.TryBegin())
        {
            return;
        }

        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { return; }

            var cloneService = new ManagedProjectCloneService(Path.Combine(
                AppContext.BaseDirectory,
                "Scripts"));
            ManagedProjectClonePlan plan;
            try
            {
                plan = cloneService.CreatePlan(tool.Descriptor.Key, folder.Path);
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("无法下载项目", exception.Message);
                return;
            }

            var confirmation = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认下载项目",
                Content = new TextBlock
                {
                    Text = $"工具：{tool.DisplayName}{Environment.NewLine}" +
                           $"GitHub：{plan.RepositoryUrl}{Environment.NewLine}" +
                           $"目标目录：{plan.TargetDirectory}",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "下载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await TryShowDialogAsync(confirmation) != ContentDialogResult.Primary) { return; }

            var definition = cloneService.CreateTask(plan);
            tool.BeginAction(args.Action);
            _activeHostedTaskKind = HostedTaskKind.ProjectClone;
            _activeHostedTaskId = definition.Id;
            _viewModel.GlobalProgress.Start(definition.Title, "正在启动 GitHub 下载任务...");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Stop,
                "可以停止项目下载。");
            _viewModel.LogService.Write(LogKind.User, $"开始执行：{definition.Title}");
            StartTaskElapsedTimer();

            AxTaskResult result;
            try
            {
                result = await runner.RunAsync(definition, _windowTaskCancellation.Token);
            }
            catch (Exception exception)
            {
                result = CreateFailedTaskResult(definition, exception);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            await SaveTaskHistoryAsync(result);
            if (result.Status == AxTaskStatus.Succeeded)
            {
                tool.SourceRoot = plan.TargetDirectory;
            }
            tool.CompleteAction(result);
            _viewModel.GlobalProgress.Complete(
                result.Status == AxTaskStatus.Succeeded ? "项目下载成功" : "项目下载失败",
                result.Summary);
            _viewModel.LogService.Write(
                result.Status == AxTaskStatus.Succeeded ? LogKind.Info : LogKind.Error,
                $"{definition.Title}：{result.CreateDiagnosticLogText()}");
            await CompleteAndHideGlobalProgressAsync();
        }
        finally
        {
            _activeHostedTaskId = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private async void ToolPage_ReleaseDownloadRequested(
        object? sender,
        ToolDownloadRequestedEventArgs args)
    {
        var runner = _viewModel.TaskRunner;
        if (runner is null || !_hostedTaskCoordinator.TryBegin()) { return; }
        var tool = args.ViewModel;
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { return; }

            var service = new ManagedReleaseDownloadService(Path.Combine(AppContext.BaseDirectory, "Scripts"));
            ManagedReleaseDownloadPlan plan;
            try { plan = service.CreatePlan(tool.Descriptor.Key, folder.Path); }
            catch (Exception exception) { await ShowMessageDialogAsync("无法下载正式版", exception.Message); return; }

            var definition = service.CreateTask(plan);
            tool.BeginAction(args.Action);
            _activeHostedTaskKind = HostedTaskKind.ProjectClone;
            _activeHostedTaskId = definition.Id;
            _viewModel.GlobalProgress.Start(definition.Title, "正在读取 GitHub 正式版...");
            _viewModel.GlobalProgress.SetCancellationMode(AxTaskCancellationMode.Stop, "可以停止正式版下载。");
            _viewModel.LogService.Write(LogKind.User, $"开始执行：{definition.Title}");
            StartTaskElapsedTimer();
            AxTaskResult result;
            try { result = await runner.RunAsync(definition, _windowTaskCancellation.Token); }
            catch (Exception exception) { result = CreateFailedTaskResult(definition, exception); }
            finally { StopTaskElapsedTimer(); }
            await SaveTaskHistoryAsync(result);
            tool.CompleteAction(result);
            _viewModel.GlobalProgress.Complete(
                result.Status == AxTaskStatus.Succeeded ? "正式版下载成功" : "正式版下载失败",
                result.Summary);
            _viewModel.LogService.Write(
                result.Status == AxTaskStatus.Succeeded ? LogKind.Info : LogKind.Error,
                $"{definition.Title}：{result.CreateDiagnosticLogText()}");
            await CompleteAndHideGlobalProgressAsync();
        }
        finally
        {
            _activeHostedTaskId = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }
}
