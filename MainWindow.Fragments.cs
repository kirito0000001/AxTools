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
/// 《生成碎片》：把打包产物切成玩家可以直接从网盘/群里拿的碎片。
///
/// 现在有两种形态：
///   · PC：按体积切片，每片一个独立的 <c>基础碎片NNN.zip</c>（默认 1 GiB 一片）；
///   · 安卓：仍是「分片散件 + 影片压缩包 + 其余文件压缩包」，等 OBB 拆解做完再切到切片。
///
/// 这条链路**不走 PowerShell**（不像其它工具动作那样交给 AxTaskRunner）：
/// 分组规则和落盘都在 Core 里、能单独测，打包只是薄薄一层起 bz.exe。
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

        // 《发布碎片》是另一条链路（PowerShell + sftp），接线和实现都在 MainWindow.FragmentPublish.cs。
        AttachFragmentManifestPublishHandler();
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
        var layout = viewModel.FragmentLayout;
        var archiveNamePrefix = viewModel.FragmentArchiveNamePrefix;
        var gameVersion = viewModel.FragmentGameVersion;
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

        // PC 碎片要能直接导入，必须带上发布脚本写在落位目录里的版本标记；
        // 打包根里本来没有这个文件（Publish-GameDownloadRelease.ps1:1453 才生成），这里补齐。
        CrossingVoidVersionMarkerResult? versionMarker = null;
        if (layout == CrossingVoidFragmentLayout.SizeSlices)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                await ShowMessageDialogAsync(
                    $"无法{actionText}",
                    "游戏包发布里还没有版本号。先到「服务器 → 零境交错」填好版本，碎片里的版本标记要用它。");
                return;
            }

            try
            {
                // 获取版本标记要走网络：挂到全局进度条上（滚动条 + 禁用按钮），
                // 不然这几秒没有任何反馈，用户会以为卡住而重复点。
                viewModel.BeginFragmentPreparation("正在获取版本标记…");
                _viewModel.GlobalProgress.Start(actionText, "正在获取版本标记...");
                _viewModel.GlobalProgress.SetCancellationMode(AxTaskCancellationMode.Locked);
                _viewModel.GlobalProgress.Report(
                    new ProgressUpdate("正在获取版本标记...", 0, $"版本 {gameVersion}", true));

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                versionMarker = await CrossingVoidVersionMarker.EnsureAsync(
                    gameDirectory,
                    gameVersion,
                    CrossingVoidVersionMarker.WindowsPlatform,
                    CrossingVoidVersionMarker.StableChannel,
                    httpClient,
                    CancellationToken.None);
                _viewModel.LogService.Write(
                    LogKind.Info,
                    versionMarker.Action switch
                    {
                        CrossingVoidVersionMarkerAction.Downloaded =>
                            $"已从下载站补齐版本标记：{versionMarker.Path}",
                        CrossingVoidVersionMarkerAction.Generated =>
                            $"已本地生成版本标记：{versionMarker.Path}",
                        _ => $"复用已有版本标记：{versionMarker.Path}",
                    });
                if (versionMarker.Warning is not null)
                {
                    _viewModel.LogService.Write(LogKind.Warning, versionMarker.Warning);
                }

                viewModel.EndFragmentPreparation("版本标记已就绪。");
                _viewModel.GlobalProgress.Hide();
            }
            catch (Exception exception)
            {
                viewModel.EndFragmentPreparation($"获取版本标记失败 · {exception.Message}");
                _viewModel.GlobalProgress.Hide();
                _viewModel.LogService.Write(LogKind.Error, "准备版本标记失败。", exception);
                await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
                return;
            }
        }

        // 扫描 + 分组只读目录、不落盘，很便宜；先把统计拿出来，确认弹窗里要显示。
        CrossingVoidFragmentPlan? groupPlan = null;
        CrossingVoidFragmentSizePlan? sizePlan = null;
        var totalCount = 0;
        try
        {
            if (layout == CrossingVoidFragmentLayout.SizeSlices)
            {
                sizePlan = CrossingVoidFragmentGenerator.BuildSizePlan(gameDirectory, archiveNamePrefix);
                totalCount = sizePlan.TotalCount;
            }
            else
            {
                groupPlan = CrossingVoidFragmentGenerator.BuildPlan(gameDirectory);
                totalCount = groupPlan.TotalCount;
            }
        }
        catch (Exception exception)
        {
            _viewModel.LogService.Write(LogKind.Error, "扫描游戏包目录失败。", exception);
            await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
            return;
        }

        if (totalCount == 0)
        {
            await ShowMessageDialogAsync(
                $"无法{actionText}",
                $"游戏包目录里没有可发出去的文件：{gameDirectory}");
            return;
        }

        // 切片模式会先清空输出目录：确认弹窗必须把"会删掉多少"写清楚，不能只说过滤规则。
        var outputStats = CrossingVoidFragmentOutputStats.Empty;
        if (layout == CrossingVoidFragmentLayout.SizeSlices)
        {
            // 先校验再统计：输出目录误设成盘根/用户目录时，Measure 会去枚举整盘。
            try
            {
                CrossingVoidFragmentOutputCleaner.Validate(outputDirectory, gameDirectory);
            }
            catch (InvalidOperationException exception)
            {
                await ShowMessageDialogAsync($"无法{actionText}", exception.Message);
                return;
            }

            outputStats = CrossingVoidFragmentOutputCleaner.Measure(outputDirectory);
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
        if (layout == CrossingVoidFragmentLayout.SizeSlices)
        {
            var slices = sizePlan!;
            content.Children.Add(CreateDialogLine(
                "基础碎片",
                $"{slices.Slices.Count:N0} 个 · 原始 {FormatBytes(slices.TotalSourceBytes)}"));
            if (versionMarker is not null)
            {
                content.Children.Add(CreateDialogLine(
                    "版本标记",
                    $"{CrossingVoidVersionMarker.FileName}（{versionMarker.Version}）"));
            }
            content.Children.Add(CreateDialogLine(
                "碎片清单",
                $"{CrossingVoidFragmentManifestWriter.FileName}（包名 / 大小 / 文件数）"));
            if (!outputStats.IsEmpty)
            {
                content.Children.Add(CreateDialogLine(
                    "清空输出目录",
                    $"{outputStats.FileCount:N0} 个文件 · {FormatBytes(outputStats.TotalBytes)}"));
            }
            if (slices.OversizedFiles.Count > 0)
            {
                content.Children.Add(CreateDialogLine(
                    "超限文件",
                    $"{slices.OversizedFiles.Count:N0} 个（单个超过 1 GiB，已各自单独成片）"));
            }

            content.Children.Add(new TextBlock
            {
                Text = "每片都是独立 zip，内部保持原目录树；全部解压到游戏安装目录即可。" +
                    "压缩开始后停止，会在当前这片压完之后生效。",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
            if (versionMarker?.Warning is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = versionMarker.Warning,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }
            if (!outputStats.IsEmpty)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "开始生成时会先清空输出目录：上面这些文件会被删除，无法恢复。",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }
        }
        else
        {
            var groups = groupPlan!;
            content.Children.Add(CreateDialogLine(
                "分片散件",
                $"{groups.Chunks.Count:N0} 个 · {FormatBytes(groups.ChunkBytes)}"));
            content.Children.Add(CreateDialogLine(
                "影片压缩包",
                $"{groups.Videos.Count:N0} 个 · {FormatBytes(groups.VideoBytes)}"));
            content.Children.Add(CreateDialogLine(
                "其余文件包",
                $"{groups.Remainder.Count:N0} 个 · {FormatBytes(groups.RemainderBytes)}"));
            content.Children.Add(new TextBlock
            {
                Text = "分片不压缩、影片仅存储、其余文件走标准档；输出目录里同名旧文件会被覆盖，" +
                    "其他文件不动。生成完自己发到网盘或群里就行。",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });
        }

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
                layout == CrossingVoidFragmentLayout.SizeSlices
                    ? "清空阶段可以立即停止；压缩阶段要等当前这片压完。已生成的碎片保留。"
                    : "拷贝和压缩阶段都可以停止；已经生成的部分会留在输出目录里。");
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
                    () =>
                    {
                        if (layout == CrossingVoidFragmentLayout.SizeSlices)
                        {
                            // 清空是破坏性操作，放在后台线程做；进度条先给一句提示。
                            DispatcherQueue.TryEnqueue(() => _viewModel.GlobalProgress.Report(
                                new ProgressUpdate("正在清空输出目录...", 0)));
                            CrossingVoidFragmentOutputCleaner.Clear(
                                outputDirectory,
                                gameDirectory,
                                cancellation.Token);
                        }

                        return generator.Generate(
                            gameDirectory,
                            outputDirectory,
                            layout,
                            archiveNamePrefix,
                            progress => DispatcherQueue.TryEnqueue(() =>
                            {
                                viewModel.ReportFragmentProgress(progress);
                                _viewModel.GlobalProgress.Report(new ProgressUpdate(
                                    progress.Message,
                                    progress.Percent));
                            }),
                            cancellation.Token);
                    },
                    cancellation.Token);
            }
            finally
            {
                StopTaskElapsedTimer();
            }

            var summary = DescribeFragmentResult(result);
            if (layout == CrossingVoidFragmentLayout.SizeSlices)
            {
                // 碎片清单：页面/启动器靠它提前知道"这一版有几个包、每个包多大、装了多少文件"。
                try
                {
                    var manifestPath = CrossingVoidFragmentManifestWriter.Write(
                        outputDirectory,
                        gameVersion,
                        result.Archives);
                    summary += $" · 清单 {Path.GetFileName(manifestPath)}";
                    _viewModel.LogService.Write(LogKind.Info, $"已写出碎片清单：{manifestPath}");
                }
                catch (Exception exception)
                {
                    _viewModel.LogService.Write(
                        LogKind.Error,
                        "碎片已生成，但碎片清单写入失败。",
                        exception);
                    summary += " · 清单写入失败";
                }
            }

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
                layout == CrossingVoidFragmentLayout.SizeSlices
                    ? "已停止。输出目录里已经生成的基础碎片保留着，再点一次会重新清空并生成。"
                    : "已停止。输出目录里已经生成的部分保留着，可以直接再点一次接着生成。");
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
        if (result.Layout == CrossingVoidFragmentLayout.SizeSlices)
        {
            var sourceBytes = result.Archives.Sum(archive => archive.SourceBytes);
            return $"{result.Archives.Count:N0} 个基础碎片" +
                $"（原始 {FormatBytes(sourceBytes)} → 输出 {FormatBytes(result.ArchiveBytes)}）";
        }

        var groups = result.Groups ?? new CrossingVoidFragmentPlan([], [], []);
        var parts = new List<string>
        {
            $"分片 {groups.Chunks.Count:N0} 个（{FormatBytes(groups.ChunkBytes)}）"
        };
        if (groups.Videos.Count > 0)
        {
            parts.Add($"影片包 {groups.Videos.Count:N0} 个（{FormatBytes(groups.VideoBytes)}）");
        }

        if (groups.Remainder.Count > 0)
        {
            parts.Add($"其余文件包 {groups.Remainder.Count:N0} 个（{FormatBytes(groups.RemainderBytes)}）");
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
