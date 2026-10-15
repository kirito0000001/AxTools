using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AxTaskRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_RealPowerShellSuccess_PreservesUtf8AndSucceeds()
    {
        var (runner, definition) = CreateScenario("success");

        var result = await runner.RunAsync(definition, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Output, line => line.Contains("中文 UTF-8", StringComparison.Ordinal));
        Assert.Contains(result.Events, taskEvent =>
            taskEvent.Type == AxTaskEventType.Result &&
            taskEvent.ResultStatus == AxTaskResultStatus.Succeeded);
    }

    [Fact]
    public async Task RunAsync_RealPowerShellFailure_UsesReportedExitCode()
    {
        var (runner, definition) = CreateScenario("failure");

        var result = await runner.RunAsync(definition, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AxTaskStatus.Failed, result.Status);
        Assert.Equal(41, result.ExitCode);
        Assert.Contains(result.Events, taskEvent =>
            taskEvent.Type == AxTaskEventType.Result &&
            taskEvent.ResultStatus == AxTaskResultStatus.Failed);
    }

    [Fact]
    public async Task RunAsync_RealPowerShellInvalidProtocol_IsolatedFromSuccessResult()
    {
        var (runner, definition) = CreateScenario("invalid");

        var result = await runner.RunAsync(definition, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
        Assert.Contains("1 条无效协议事件", result.Summary);
        Assert.Contains("ANSI 输出已生成，运行器应清理颜色。", result.Output);
        Assert.DoesNotContain(result.Output, line => line.Contains('\u001b'));
    }

    [Fact]
    public async Task TryRequestStop_RealPowerShell_WritesCancellationSignal()
    {
        var (runner, definition) = CreateScenario("stop");
        var stopModePublished = NewSignal();
        runner.EventReceived += taskEvent =>
        {
            if (taskEvent.CancellationMode == AxTaskCancellationMode.Stop)
            {
                stopModePublished.TrySetResult();
            }
        };

        var runningTask = runner.RunAsync(definition, CancellationToken.None);
        await stopModePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runner.TryRequestStop());
        var result = await runningTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AxTaskStatus.Stopped, result.Status);
        Assert.Equal(130, result.ExitCode);
        Assert.Contains(result.Events, taskEvent =>
            taskEvent.Type == AxTaskEventType.Result &&
            taskEvent.ResultStatus == AxTaskResultStatus.Stopped);
    }

    [Fact]
    public async Task TryRequestStop_RealPowerShellLockedStage_IsRejected()
    {
        var (runner, definition) = CreateScenario("locked");
        var lockedModePublished = NewSignal();
        runner.EventReceived += taskEvent =>
        {
            if (taskEvent.CancellationMode == AxTaskCancellationMode.Locked)
            {
                lockedModePublished.TrySetResult();
            }
        };

        var runningTask = runner.RunAsync(definition, CancellationToken.None);
        await lockedModePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(runner.TryRequestStop());
        var result = await runningTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    private static (AxTaskRunner Runner, AxTaskDefinition Definition) CreateScenario(
        string scenario)
    {
        // 从测试程序集往上找仓库根：以前写死往上 5 层，bin 布局一变就指向 D:\UnrealMap\Scripts，
        // 于是五个用例集体报"任务脚本不存在"（2026-10-15 实测）。
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "Scripts",
            "Diagnostics",
            "Invoke-RunnerSelfTest.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"找不到自检脚本：{scriptPath}", scriptPath);
        }

        var host = new PowerShellProcessHost(new PowerShellLocator().FindPowerShell());
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var definition = new AxTaskDefinition(
            $"integration-{scenario}",
            $"集成测试：{scenario}",
            scriptPath,
            new[] { "-Scenario", scenario });

        return (runner, definition);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AxTools.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根（AxTools.sln）。");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
