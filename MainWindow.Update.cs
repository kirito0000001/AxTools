using System.Reflection;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace AxTools;

public sealed partial class MainWindow
{
    private async void GlobalSettingsPage_UpdateCheckRequested(object? sender, EventArgs args) => await CheckForUpdateAsync(true);

    private async Task CheckForUpdateAsync(bool showDialog)
    {
        if (!_hostedTaskCoordinator.TryBegin()) return;
        _activeHostedTaskKind = HostedTaskKind.Update;
        _viewModel.GlobalProgress.Start("检查 AxTools 更新", $"来源：{_viewModel.Settings.Update.Source}");
        _viewModel.GlobalProgress.SetCancellationMode(AxTaskCancellationMode.Cancel);
        StartTaskElapsedTimer();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_viewModel.Settings.Update.ConnectionTimeoutSeconds) };
            var service = new UpdateService(client);
            var result = await service.CheckAsync(
                _viewModel.Settings.Update.Source,
                _viewModel.Settings.Update.Channel,
                GetInformationalVersion().Split('+')[0],
                _windowTaskCancellation.Token);
            _viewModel.Settings.Update.ApplyCheckResult(result);
            await _viewModel.Settings.SaveAsync(CancellationToken.None);
            _viewModel.GlobalProgress.Complete("更新检查完成", result.Status);
            if (showDialog) await ShowMessageDialogAsync("更新检查", result.Status);
        }
        catch (Exception exception)
        {
            var message = $"更新检查失败：{exception.Message}";
            _viewModel.Settings.Update.SetFailure(message);
            _viewModel.GlobalProgress.Complete("更新检查失败", message);
            _viewModel.LogService.Write(LogKind.Error, message, exception);
            if (showDialog) await ShowMessageDialogAsync("更新检查失败", message);
        }
        finally
        {
            StopTaskElapsedTimer();
            await CompleteAndHideGlobalProgressAsync();
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private async void GlobalSettingsPage_UpdateDownloadRequested(object? sender, EventArgs args)
    {
        var release = _viewModel.Settings.Update.AvailableRelease;
        if (release is null || !_hostedTaskCoordinator.TryBegin()) return;
        _activeHostedTaskKind = HostedTaskKind.Update;
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxTools", "Updates", release.Version);
        var packagePath = Path.Combine(updateRoot, release.Asset.FileName);
        _viewModel.GlobalProgress.Start("下载 AxTools 更新", release.Asset.FileName);
        _viewModel.GlobalProgress.SetCancellationMode(AxTaskCancellationMode.Cancel);
        StartTaskElapsedTimer();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_viewModel.Settings.Update.ConnectionTimeoutSeconds) };
            await new UpdateService(client).DownloadAsync(release.Asset, packagePath, _windowTaskCancellation.Token);
            _viewModel.GlobalProgress.Report(new ProgressUpdate("正在校验更新包...", 85, release.Asset.Sha256));
            await new UpdatePackageValidator().ValidateAsync(packagePath, release.Version, release.Asset, _windowTaskCancellation.Token);
            _viewModel.Settings.Update.SetDownloaded(packagePath);
            _viewModel.GlobalProgress.Complete("更新包已就绪", packagePath);
        }
        catch (Exception exception)
        {
            _viewModel.Settings.Update.SetFailure($"更新包下载或校验失败：{exception.Message}");
            _viewModel.GlobalProgress.Complete("更新包不可用", exception.Message);
            _viewModel.LogService.Write(LogKind.Error, "更新包下载或校验失败。", exception);
        }
        finally
        {
            StopTaskElapsedTimer();
            await CompleteAndHideGlobalProgressAsync();
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private async void GlobalSettingsPage_UpdateInstallRequested(object? sender, EventArgs args)
    {
        var update = _viewModel.Settings.Update;
        var release = update.AvailableRelease;
        if (release is null || !update.CanInstall) return;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"安装 AxTools {release.Version}",
            Content = new TextBlock { Text = "外部更新器准备完成后将关闭当前 AxTools，覆盖程序目录并重新启动。项目设置、日志和任务历史不会被覆盖。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            PrimaryButtonText = "安装并重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await TryShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            await _viewModel.Settings.SaveAsync(CancellationToken.None);
            var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxTools", "Updater");
            Directory.CreateDirectory(updateRoot);
            var signal = Path.Combine(updateRoot, $"ready-{Guid.NewGuid():N}.signal");
            var log = Path.Combine(updateRoot, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "Release", "Update-AxTools.ps1");
            var launcher = new UpdaterLauncher(new PowerShellLocator().FindPowerShell(), script);
            using var process = await launcher.LaunchAsync(new UpdaterLaunchRequest(
                update.DownloadedPackagePath,
                AppContext.BaseDirectory,
                Environment.ProcessId,
                release.Asset.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? release.Asset.FileName : "AxTools.exe",
                release.Version,
                signal,
                log), TimeSpan.FromSeconds(20), CancellationToken.None);
            Close();
        }
        catch (Exception exception)
        {
            update.SetFailure($"未能启动外部更新器：{exception.Message}");
            await ShowMessageDialogAsync("安装未开始", update.Status);
        }
    }
}
