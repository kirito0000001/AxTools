using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using AxTools.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AxTools;

/// <summary>
/// 《生成碎片》：把打包产物拆成「分片散件 + 影片压缩包 + 其余文件压缩包」，给玩家从网盘或群里拿。
///
/// 这条链路**不走 PowerShell**（不像其它工具动作那样交给 AxTaskRunner）：
/// 分类规则和落盘都在 Core 里、能单独测，打包只是薄薄一层起 bz.exe。
/// 所以这里直接用后台线程 + 全局进度条，取消走 <c>_activeOperationCancellation</c>
/// 那条本机操作的路（和存储扫描、环境扫描一样）。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 《生成碎片》的事件接线。**故意集中在这个文件里**，构造器那边只留一行调用 ——
    /// 以后加游戏（幻杀、火影各自的碎片入口）或者挪按钮，只动这里和
    /// <see cref="ToolPageViewModel.HasFragmentGeneration"/>，不用再翻 MainWindow.xaml.cs。
    /// </summary>
    private void AttachFragmentGenerationHandlers()
    {
        CrossingVoidPcPage.FragmentGenerationRequested += ToolPage_FragmentGenerationRequested;
        CrossingVoidAndroidPage.FragmentGenerationRequested += ToolPage_FragmentGenerationRequested;
        CrossingVoidPcPage.FragmentPathSelectionRequested += ToolPage_FragmentPathSelectionRequested;
        CrossingVoidAndroidPage.FragmentPathSelectionRequested += ToolPage_FragmentPathSelectionRequested;
    }

    private async void ToolPage_FragmentPathSelectionRequested(
        object? sender,
        FragmentPathRequestedEventArgs args)
    {
        if (sender is not ToolPage page || page.DataContext is not ToolPageViewModel viewModel)
        {
            return;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        if (args.PathKind == "OutputDirectory")
        {
            viewModel.FragmentOutputDirectory = folder.Path;
        }

        await _viewModel.Settings.SaveAsync(CancellationToken.None);
    }

    private async void ToolPage_FragmentGenerationRequested(object? sender, EventArgs args)
    {
        if (sender is not ToolPage page || page.DataContext is not ToolPageViewModel viewModel)
        {
            return;
        }

        var actionText = viewModel.FragmentGenerationActionText;
        var gameDirectory = viewModel.FragmentGameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            await ShowMessageDialogAsync(
                $"无法{actionText}",
                "还没有可用的游戏包目录。先到「服务器 → 零境交错」的游戏包发布里选一次，这里就跟着用了。");
            return;
        }

        var outputDirectory = viewModel.FragmentOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await ShowMessageDialogAsync($"无法{actionText}", "请先选一个碎片输出目录。");
            return;
        }

        // 扫描 + 分类只读目录、不落盘，很便宜；先把统计拿出来，确认弹窗里要显示。
        CrossingVoidFragmentPlan plan;
        try
        {
            plan = CrossingVoidFragmentGenerator.BuildPlan(gameDirectory);
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(LogKind.Error, "扫描游戏包目录失败。", exception);
            await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
            return;
        }

        if (plan.TotalCount == 0)
        {
            await ShowMessageDialogAsync(
                $"无法{actionText}",
                $"游戏包目录里没有可发出去的文件：{gameDirectory}");
            return;
        }

        string bandizip;
        try
        {
            bandizip = BandizipCrossingVoidArchiver.ResolveExecutable(
                _viewModel.CrossingVoidPackage.BandizipExecutable);
        }
        catch (Exception exception)
        {
            await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
            return;
        }

        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(CreateDialogLine("游戏包", gameDirectory));
        content.Children.Add(CreateDialogLine("输出到", outputDirectory));
        content.Children.Add(CreateDialogLine(
            "分片散件",
            $"{plan.Chunks.Count:N0} 个 · {FormatBytes(plan.ChunkBytes)}"));
        content.Children.Add(CreateDialogLine(
            "影片压缩包",
            $"{plan.Videos.Count:N0} 个 · {FormatBytes(plan.VideoBytes)}"));
        content.Children.Add(CreateDialogLine(
            "其余文件包",
            $"{plan.Remainder.Count:N0} 个 · {FormatBytes(plan.RemainderBytes)}"));
        content.Children.Add(new TextBlock
        {
            Text = "分片不压缩、影片仅存储、其余文件走标准档；输出目录里同名旧文件会被覆盖，" +
                "其他文件不动。生成完自己发到网盘或群里就行。",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"确认{actionText}",
            Content = content,
            PrimaryButtonText = "开始生成",
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
                $"已有托管任务正在运行，未启动{actionText}。");
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _windowTaskCancellation.Token);
        _activeOperationCancellation = cancellation;
        _activeHostedTaskKind = HostedTaskKind.GameFragments;
        try
        {
            await _viewModel.Settings.SaveAsync(CancellationToken.None);
            viewModel.BeginFragmentGeneration();
            _viewModel.GlobalProgress.Start(actionText, "正在扫描游戏包目录...");
            _viewModel.GlobalProgress.SetCancellationMode(
                AxTaskCancellationMode.Stop,
                "拷贝和压缩阶段都可以停止；已经生成的部分会留在输出目录里。");
            _viewModel.LogService.Write(
                LogKind.User,
                $"开始{actionText}：{gameDirectory} → {outputDirectory}");
            StartTaskElapsedTimer();

            var generator = new CrossingVoidFragmentGenerator(
                new BandizipCrossingVoidArchiver(bandizip));
            CrossingVoidFragmentResult result;
            try
            {
                result = await Task.Run(
                    () => generator.Generate(
                        gameDirectory,
                        outputDirectory,
                        progress => DispatcherQueue.TryEnqueue(() =>
                        {
                            viewModel.ReportFragmentProgress(progress);
                            _viewModel.GlobalProgress.Report(new ProgressUpdate(
                                progress.Message,
                                progress.Percent));
                        }),
                        cancellation.Token),
                    cancellation.Token);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            var summary = DescribeFragmentResult(result);
            viewModel.CompleteFragmentGeneration($"生成成功 · {summary}");
            _viewModel.GlobalProgress.Complete($"{actionText}完成", summary);
            _viewModel.LogService.Write(
                LogKind.Info,
                $"{actionText}完成：{outputDirectory}（{summary}）");
            await CompleteAndHideGlobalProgressAsync();
        }
        catch (OperationCanceledException)
        {
            viewModel.CompleteFragmentGeneration("已停止。");
            _viewModel.GlobalProgress.Complete(
                $"{actionText}已停止",
                "已停止。输出目录里已经生成的部分保留着，可以直接再点一次接着生成。");
            _viewModel.LogService.Write(LogKind.Warning, $"{actionText}已被用户停止。");
            await CompleteAndHideGlobalProgressAsync();
        }
        catch (Exception exception)
        {
            viewModel.CompleteFragmentGeneration($"生成失败 · {exception.Message}");
            _viewModel.GlobalProgress.Complete($"{actionText}失败", exception.Message);
            _viewModel.LogService.Write(LogKind.Error, $"{actionText}失败。", exception);
            await CompleteAndHideGlobalProgressAsync();
            await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
        }
        finally
        {
            _activeOperationCancellation = null;
            _activeHostedTaskKind = HostedTaskKind.None;
            _hostedTaskCoordinator.End();
        }
    }

    private static string DescribeFragmentResult(CrossingVoidFragmentResult result)
    {
        var parts = new List<string>
        {
            $"分片 {result.ChunkFiles:N0} 个（{FormatBytes(result.ChunkBytes)}）"
        };
        if (result.VideoArchives > 0)
        {
            parts.Add($"影片包 {result.VideoArchives:N0} 个（{FormatBytes(result.VideoBytes)}）");
        }

        if (result.RemainderArchivePath is not null)
        {
            parts.Add($"其余文件包 {result.RemainderFiles:N0} 个（{FormatBytes(result.RemainderBytes)}）");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
