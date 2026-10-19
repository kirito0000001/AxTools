using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using AxTools.Views;

namespace AxTools;

/// <summary>
/// 《发布碎片清单》：把 fragments.json 传到下载站，并更新 latest.json。
/// 碎片 zip 本身**不上传**：它走网盘/QQ 群分发，服务器上只放描述它们的清单。
///
/// **故意和《生成碎片》分成两个文件**：
///   · 生成是本机操作（后台线程 + 文件系统 + 全局进度条）；
///   · 发布是托管任务（PowerShell + ssh/sftp + TaskRunner 事件）；
/// 两条链路的取消方式、失败处理、进度来源都不一样，放一起只会越写越难拆。
/// </summary>
public sealed partial class MainWindow
{
    private void AttachFragmentManifestPublishHandler()
    {
        // 所有支持碎片的页面共用同一个脚本，靠 -ProductSegment / -GameShortName 区分零境/幻杀、PC/安卓。
        CrossingVoidPcPage.FragmentManifestPublishRequested += ToolPage_FragmentManifestPublishRequested;
        CrossingVoidAndroidPage.FragmentManifestPublishRequested += ToolPage_FragmentManifestPublishRequested;
        FantasyAndroidPage.FragmentManifestPublishRequested += ToolPage_FragmentManifestPublishRequested;
    }

    private async void ToolPage_FragmentManifestPublishRequested(object? sender, EventArgs args)
    {
        if (sender is not ToolPage page || page.DataContext is not ToolPageViewModel viewModel)
        {
            return;
        }

        var runner = _viewModel.TaskRunner;
        if (runner is null)
        {
            _viewModel.LogService.Write(LogKind.Warning, "任务运行器不可用，未启动碎片清单发布。");
            return;
        }

        var outputDirectory = viewModel.FragmentOutputDirectory;
        var gameVersion = viewModel.FragmentGameVersion;
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            await ShowMessageDialogAsync("无法发布碎片清单", "碎片输出目录不存在。先生成一次碎片。");
            return;
        }

        var manifestPath = Path.Combine(
            outputDirectory,
            CrossingVoidFragmentManifestWriter.FileName);
        if (!File.Exists(manifestPath))
        {
            // 清单缺失时发布脚本会按现有 zip 重建；这里只确认目录里确实有这一版的包。
            var pattern = $"{viewModel.FragmentArchiveNamePrefix}基础碎片*.zip";
            if (!Directory.EnumerateFiles(outputDirectory, pattern).Any())
            {
                await ShowMessageDialogAsync(
                    "无法发布碎片清单",
                    "输出目录里既没有 fragments.json，也没有可识别的碎片包。");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            await ShowMessageDialogAsync("无法发布碎片清单", "游戏包发布里还没有版本号。");
            return;
        }

        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，未启动碎片清单发布。");
            return;
        }

        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Scripts",
            "Publishing",
            "Games",
            "Publish-GameFragmentManifest.ps1");
        var taskTitle = $"发布{viewModel.FragmentGameShortName}{viewModel.FragmentPlatformLabel}碎片清单";
        var definition = new AxTaskDefinition(
            $"game-fragment-manifest-publish-{Guid.NewGuid():N}",
            taskTitle,
            scriptPath,
            new[]
            {
                "-FragmentDirectory", outputDirectory,
                "-ReleaseVersion", gameVersion,
                "-ProductSegment", viewModel.FragmentProductSegment,
                "-GameShortName", viewModel.FragmentGameShortName
            });

        _activeHostedTaskId = definition.Id;
        _activeHostedTaskKind = HostedTaskKind.GameFragmentManifestPublish;
        try
        {
            _viewModel.LogService.Write(
                LogKind.User,
                $"开始发布碎片清单：{outputDirectory}（版本 {gameVersion} · 产品段 {viewModel.FragmentProductSegment}）");
            viewModel.BeginFragmentManifestPublishing();
            _viewModel.GlobalProgress.Start(taskTitle, "正在检查碎片清单...");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Stop,
                "可以停止；清单是原子写入的，停止不会留下半截文件。");
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
                    "碎片清单发布发生未处理错误。",
                    exception);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            await SaveTaskHistoryAsync(result);
            var title = result.Status switch
            {
                AxTaskStatus.Succeeded => "发布成功",
                AxTaskStatus.Stopped => "发布已停止",
                _ => "发布失败"
            };
            viewModel.CompleteFragmentManifestPublishing($"{title} · {result.Summary}");
            _viewModel.GlobalProgress.Complete(
                result.Status == AxTaskStatus.Succeeded ? "碎片清单发布完成" : "碎片清单发布结束",
                result.Summary);
            _viewModel.LogService.Write(
                result.Status == AxTaskStatus.Succeeded ? LogKind.Info : LogKind.Error,
                $"发布碎片清单结束：{result.Summary}");
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
