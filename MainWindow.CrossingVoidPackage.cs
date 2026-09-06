using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Views;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AxTools;

public sealed partial class MainWindow
{
    private async void CrossingVoidPackagePage_PathSelectionRequested(
        object? sender,
        CrossingVoidPackagePathRequestedEventArgs args)
    {
        var viewModel = _viewModel.CrossingVoidPackage;
        if (args.PathKind == "BandizipExecutable")
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                viewModel.BandizipExecutable = file.Path;
                await _viewModel.Settings.SaveAsync(CancellationToken.None);
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

        if (args.PathKind == "GameDirectory")
        {
            viewModel.GameDirectory = folder.Path;
        }
        else if (args.PathKind == "OutputDirectory")
        {
            viewModel.OutputDirectory = folder.Path;
        }
        viewModel.RefreshInspection();
        await _viewModel.Settings.SaveAsync(CancellationToken.None);
    }

    private async void CrossingVoidPackagePage_InspectionRequested(
        object? sender,
        EventArgs args)
    {
        _viewModel.CrossingVoidPackage.RefreshInspection();
        await _viewModel.Settings.SaveAsync(CancellationToken.None);
    }

    private async void CrossingVoidPackagePage_GenerationRequested(
        object? sender,
        EventArgs args)
    {
        var runner = _viewModel.TaskRunner;
        var package = _viewModel.CrossingVoidPackage;
        package.RefreshInspection();
        if (runner is null || !package.CanGenerate)
        {
            await ShowMessageDialogAsync("无法生成分片包", package.InspectionStatus);
            return;
        }

        var conflicts = FindCrossingVoidPackageConflicts(package.OutputDirectory);
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(CreateDialogLine("游戏包", package.GameDirectory));
        content.Children.Add(CreateDialogLine("输出目录", package.OutputDirectory));
        content.Children.Add(CreateDialogLine("版本", package.ResolvedVersion));
        content.Children.Add(CreateDialogLine("打包内容", package.IncludedSummary));
        content.Children.Add(CreateDialogLine("排除内容", package.ExcludedSummary));
        content.Children.Add(CreateDialogLine("分片", package.EstimatedChunkSummary));
        content.Children.Add(new TextBlock
        {
            Text = "固定排除：.pdb、Saved/Logs、Saved/Crashes、_download、.git",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });
        if (conflicts.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"将覆盖 {conflicts.Count} 个同名旧结果；不会清理输出目录中的其他文件。",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "确认生成游戏分片包",
            Content = content,
            PrimaryButtonText = conflicts.Count > 0 ? "生成并覆盖" : "开始生成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await TryShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (!_hostedTaskCoordinator.TryBegin())
        {
            _viewModel.LogService.Write(
                LogKind.Warning,
                "已有托管任务正在运行，未启动游戏分片任务。");
            return;
        }

        AxTaskDefinition? definition = null;
        try
        {
            await _viewModel.Settings.SaveAsync(CancellationToken.None);
            definition = package.CreateTaskDefinition(overwrite: conflicts.Count > 0);
            _activeHostedTaskKind = HostedTaskKind.CrossingVoidPackage;
            _activeHostedTaskId = definition.Id;
            package.BeginGeneration();
            _viewModel.GlobalProgress.Start(definition.Title, "正在启动 PowerShell 7 游戏分片脚本...");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Stop,
                "扫描、压缩和分片阶段可以停止。 ");
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
                _viewModel.LogService.Write(
                    LogKind.Error,
                    "游戏分片任务发生未处理错误。",
                    exception);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            await SaveTaskHistoryAsync(result);
            package.CompleteGeneration(result);
            var title = result.Status switch
            {
                AxTaskStatus.Succeeded => "游戏分片包生成成功",
                AxTaskStatus.Stopped => "游戏分片任务已停止",
                _ => "游戏分片包生成失败"
            };
            _viewModel.GlobalProgress.Complete(title, result.Summary);
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
        catch (Exception exception)
        {
            if (definition is not null)
            {
                package.CompleteGeneration(CreateFailedTaskResult(definition, exception));
            }
            _viewModel.LogService.Write(LogKind.Error, "无法启动游戏分片任务。", exception);
            await ShowMessageDialogAsync("无法启动游戏分片任务", exception.Message);
        }
        finally
        {
            _activeHostedTaskId = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private static IReadOnlyList<string> FindCrossingVoidPackageConflicts(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return [];
        }

        var conflicts = new List<string>();
        foreach (var fixedName in new[] { "update.json", "CrossingVoid.zip" })
        {
            var path = Path.Combine(outputDirectory, fixedName);
            if (File.Exists(path))
            {
                conflicts.Add(path);
            }
        }
        conflicts.AddRange(Directory.GetFiles(outputDirectory, "CrossingVoid.zip.part*"));
        return conflicts;
    }
}
