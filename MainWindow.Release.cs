using System.Diagnostics;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Views;
using Microsoft.UI.Xaml.Controls;

namespace AxTools;

public sealed partial class MainWindow
{
    private void GlobalSettingsPage_GiteeTokenSaveRequested(object? sender, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _viewModel.Settings.DeveloperRelease.Status = "Token 为空，未修改用户环境变量。";
            return;
        }

        Environment.SetEnvironmentVariable("AXTOOLS_GITEE_TOKEN", token.Trim(), EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("AXTOOLS_GITEE_TOKEN", token.Trim(), EnvironmentVariableTarget.Process);
        _viewModel.Settings.DeveloperRelease.RefreshTokenStatus();
        _viewModel.Settings.DeveloperRelease.Status = "Gitee Token 已保存到当前用户环境变量。";
    }

    private async void GlobalSettingsPage_ReleaseActionRequested(object? sender, ReleaseActionRequestedEventArgs args)
    {
        var targetVersion = (args.Version ?? string.Empty).Trim().TrimStart('v');
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            _viewModel.Settings.DeveloperRelease.Status = "请先填写要打包或发布的版本号。";
            return;
        }

        if (args.Action is "Upload" or "Publish")
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认真实发布",
                Content = new TextBlock { Text = $"版本：{targetVersion}\n通道：{args.Channel}\n目标：Gitee xiaojie578/AxTools 与 GitHub kirito0000001/AxTools\n\n先传包体与 sha256.txt，复查通过后才传更新清单（客户端读到的最后一步）。Gitee 只保留最近 3 个带附件的 Release；GitHub 从不删除旧版本。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "确认发布",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await TryShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        }

        if (!_hostedTaskCoordinator.TryBegin()) return;
        _activeHostedTaskKind = HostedTaskKind.Release;
        var developer = _viewModel.Settings.DeveloperRelease;
        var sourceRoot = _viewModel.Tools[0].SourceRoot;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !File.Exists(Path.Combine(sourceRoot, "AxTools.csproj")))
        {
            developer.Status = "AxTools 源码目录无效，无法打开发布中心动作。";
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
            return;
        }

        // 每一版一个产物目录：zip / sha256 / 清单 / 台账 / 文件索引 / 程序目录都在它下面，
        // 下一版算增量时要回来读上一版的 file-index.json。
        var staging = Path.Combine(sourceRoot, "Artifacts", "ReleaseAssets", "v" + targetVersion);
        var script = args.Action switch
        {
            "Validate" => Path.Combine(sourceRoot, "Scripts", "Release", "Test-AxToolsPackage.ps1"),
            "Upload" or "Publish" => Path.Combine(sourceRoot, "Scripts", "Release", "Publish-AxToolsRelease.ps1"),
            _ => Path.Combine(sourceRoot, "Scripts", "Release", "Build-AxToolsUpdatePackage.ps1")
        };
        var arguments = args.Action switch
        {
            "Validate" => new[] { "-ReleaseAssetRoot", staging, "-ExpectedVersion", targetVersion },
            // 单独上传：产物已经在，出包那一步跳过，只走校验 + 上传。
            "Upload" => AppendNotes(
                new[] { "-Version", targetVersion, "-ProjectRoot", sourceRoot, "-StagingDirectory", staging, "-SkipBuild" },
                args.ReleaseNotes),
            _ => AppendNotes(
                new[] { "-Version", targetVersion, "-ProjectRoot", sourceRoot, "-StagingDirectory", staging },
                args.ReleaseNotes)
        };
        _viewModel.GlobalProgress.Start($"AxTools 发布：{args.Action}", $"{targetVersion} / {args.Channel}");
        _viewModel.GlobalProgress.SetCancellationMode(args.Action is "Upload" or "Publish" ? AxTaskCancellationMode.Locked : AxTaskCancellationMode.Cancel);
        StartTaskElapsedTimer();
        try
        {
            var host = new PowerShellProcessHost(new PowerShellLocator().FindPowerShell());
            var definition = new AxTaskDefinition($"release-{Guid.NewGuid():N}", $"AxTools {args.Action}", script, arguments);
            var result = await host.RunAsync(definition, (stream, line) =>
            {
                DispatcherQueue.TryEnqueue(() => _viewModel.LogService.Write(
                    stream == AxTaskOutputStream.StandardError ? LogKind.Warning : LogKind.Info,
                    line));
                return ValueTask.CompletedTask;
            }, _windowTaskCancellation.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"发布脚本退出码：{result.ExitCode}");
            developer.Status = $"{args.Action} 已完成：{targetVersion}";
            _viewModel.GlobalProgress.Complete("发布动作完成", developer.Status);
        }
        catch (Exception exception)
        {
            developer.Status = $"{args.Action} 失败：{exception.Message}";
            _viewModel.GlobalProgress.Complete("发布动作失败", developer.Status);
            _viewModel.LogService.Write(LogKind.Error, developer.Status, exception);
        }
        finally
        {
            StopTaskElapsedTimer();
            await CompleteAndHideGlobalProgressAsync();
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    // 空公告不拼进参数数组：让脚本用它自己的默认介绍，别传一个空串过去。
    private static string[] AppendNotes(string[] arguments, string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? arguments : [.. arguments, "-Notes", notes.Trim()];
}
