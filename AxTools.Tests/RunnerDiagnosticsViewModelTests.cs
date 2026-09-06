using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class RunnerDiagnosticsViewModelTests
{
    [Fact]
    public void Constructor_WithPowerShellPathIsReadyToStart()
    {
        var viewModel = new RunnerDiagnosticsViewModel(@"C:\Program Files\PowerShell\7\pwsh.exe");

        Assert.True(viewModel.IsPowerShellAvailable);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.IsRunning);
        Assert.Contains("PowerShell 7", viewModel.StatusTitle);
    }

    [Fact]
    public void Constructor_WithoutPowerShellPathShowsEnvironmentError()
    {
        var viewModel = new RunnerDiagnosticsViewModel(null, "未找到 PowerShell 7");

        Assert.False(viewModel.IsPowerShellAvailable);
        Assert.False(viewModel.CanStart);
        Assert.Contains("未找到 PowerShell 7", viewModel.StatusDetail);
    }

    [Fact]
    public void BeginAndReportEvent_UpdatesRunningAndCancellationState()
    {
        var viewModel = new RunnerDiagnosticsViewModel("pwsh.exe");

        viewModel.Begin("stop", "停止测试");
        viewModel.ReportEvent(new AxTaskEvent(
            AxTaskEventType.Progress,
            Stage: "build",
            Message: "正在编译",
            Detail: "第 2 步",
            Percent: 50));
        viewModel.ReportEvent(new AxTaskEvent(
            AxTaskEventType.Cancellation,
            Message: "正在提交",
            CancellationMode: AxTaskCancellationMode.Locked));

        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanStop);
        Assert.Equal("正在提交", viewModel.StatusDetail);
    }

    [Theory]
    [InlineData(AxTaskStatus.Succeeded, "成功")]
    [InlineData(AxTaskStatus.Failed, "失败")]
    [InlineData(AxTaskStatus.Stopped, "停止")]
    public void Complete_MapsTerminalStateToReadableSummary(
        AxTaskStatus status,
        string expectedText)
    {
        var viewModel = new RunnerDiagnosticsViewModel("pwsh.exe");
        viewModel.Begin("test", "测试");
        var started = DateTimeOffset.Now;
        var result = new AxTaskResult(
            "test",
            status,
            status == AxTaskStatus.Succeeded ? 0 : 1,
            started,
            started.AddSeconds(1),
            "summary",
            Array.Empty<string>(),
            Array.Empty<AxTaskEvent>());

        viewModel.Complete(result);

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.CanStop);
        Assert.Contains(expectedText, viewModel.LastResultText);
    }

    [Fact]
    public void GlobalProgress_CancellationModeControlsStopEntry()
    {
        var viewModel = new GlobalProgressViewModel();
        viewModel.Start("任务", "准备");

        viewModel.SetCancellationMode(AxTaskCancellationMode.Locked, "正在提交");
        Assert.False(viewModel.CanCancel);
        Assert.Equal("当前阶段不可停止", viewModel.CancelLabel);

        viewModel.SetCancellationMode(AxTaskCancellationMode.Stop, "可以停止");
        Assert.True(viewModel.CanCancel);
        Assert.Equal("停止当前操作", viewModel.CancelLabel);

        viewModel.MarkStopRequested();
        Assert.False(viewModel.CanCancel);
        Assert.Contains("停止信号", viewModel.Detail);
    }

    [Fact]
    public void ToolPage_RemainsDisabledUntilSourceRootIsConfigured()
    {
        var descriptor = ManagedToolCatalog.All[0];
        var viewModel = new ToolPageViewModel(
            descriptor,
            new ManagedToolPaths(),
            new AxToolsAdapter(Path.Combine(AppContext.BaseDirectory, "Scripts")));

        Assert.False(viewModel.IsExecutionAvailable);
        Assert.Contains("尚未配置源码目录", viewModel.ExecutionAvailabilityText);
    }
}
