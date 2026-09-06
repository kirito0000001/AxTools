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
        if (args.Action is "Upload" or "Publish")
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认真实发布",
                Content = new TextBlock { Text = $"版本：{args.Version}\n通道：{args.Channel}\n目标：GitHub kirito0000001/AxTools 与 Gitee xiaojie578/AxTools\n\n该版本 Release 的全部旧附件会先被清空，再上传本次产物。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
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

        var outputRoot = Path.Combine(sourceRoot, "Artifacts", "ReleaseAssets");
        var script = args.Action switch
        {
            "Validate" => Path.Combine(sourceRoot, "Scripts", "Release", "Test-AxToolsPackage.ps1"),
            "Upload" or "Publish" => Path.Combine(sourceRoot, "Scripts", "Release", "Publish-AxToolsRelease.ps1"),
            _ => Path.Combine(sourceRoot, "Scripts", "Release", "Package-AxTools.ps1")
        };
        var arguments = args.Action switch
        {
            "Validate" => new[] { "-ReleaseAssetRoot", outputRoot, "-ExpectedVersion", args.Version },
            "Upload" => new[] { "-ProjectRoot", sourceRoot, "-Version", args.Version, "-Channel", args.Channel, "-OutputRoot", outputRoot, "-Target", "Both", "-ReleaseNotes", args.ReleaseNotes, "-SkipBuild" },
            "Publish" => new[] { "-ProjectRoot", sourceRoot, "-Version", args.Version, "-Channel", args.Channel, "-OutputRoot", outputRoot, "-Target", "Both", "-ReleaseNotes", args.ReleaseNotes },
            _ => new[] { "-ProjectRoot", sourceRoot, "-Version", args.Version, "-Channel", args.Channel, "-OutputRoot", outputRoot }
        };
        _viewModel.GlobalProgress.Start($"AxTools 发布：{args.Action}", $"{args.Version} / {args.Channel}");
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
            developer.Status = $"{args.Action} 已完成：{args.Version}";
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
}
