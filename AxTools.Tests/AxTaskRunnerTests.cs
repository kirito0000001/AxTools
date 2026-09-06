using System.Collections.Concurrent;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AxTaskRunnerTests
{
    [Fact]
    public async Task RunAsync_HeavyTasksNeverOverlap()
    {
        var host = new ConcurrencyHost();
        var runner = new AxTaskRunner(host, new AxTaskEventParser());

        var first = runner.RunAsync(CreateTask("first"), CancellationToken.None);
        var second = runner.RunAsync(CreateTask("second"), CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, host.MaximumConcurrency);
        Assert.Equal(new[] { "first", "second" }, host.StartOrder);
    }

    [Fact]
    public async Task RunAsync_RequiresSuccessEventAndZeroExitCode()
    {
        var host = new ScriptedHost(
            new[] { "::axtools {\"type\":\"result\",\"status\":\"success\",\"exitCode\":0}" },
            exitCode: 0);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());

        var result = await runner.RunAsync(CreateTask("success"), CancellationToken.None);

        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Events);
    }

    [Theory]
    [InlineData(1, "success")]
    [InlineData(0, "failed")]
    public async Task RunAsync_FailsWhenExitCodeOrResultEventFails(
        int exitCode,
        string resultStatus)
    {
        var host = new ScriptedHost(
            new[] { $"::axtools {{\"type\":\"result\",\"status\":\"{resultStatus}\",\"exitCode\":{exitCode}}}" },
            exitCode);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());

        var result = await runner.RunAsync(CreateTask("failure"), CancellationToken.None);

        Assert.Equal(AxTaskStatus.Failed, result.Status);
    }

    [Fact]
    public async Task RunAsync_FailureSummaryIncludesReportedReasonAndRecentPlainOutput()
    {
        var host = new ScriptedHost(
            new[]
            {
                "Compiling launcher",
                "error: failed to run custom build command",
                "caused by: WebView2 loader was not found",
                "::axtools {\"type\":\"result\",\"status\":\"failed\",\"exitCode\":1,\"message\":\"Tauri 开发启动失败。\"}"
            },
            exitCode: 1);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());

        var result = await runner.RunAsync(CreateTask("detailed-failure"), CancellationToken.None);
        var diagnostic = result.CreateDiagnosticLogText();

        Assert.Contains("任务执行失败，退出码 1。", diagnostic);
        Assert.Contains("脚本报告：Tauri 开发启动失败。", diagnostic);
        Assert.Contains("任务输出：", diagnostic);
        Assert.Contains("error: failed to run custom build command", diagnostic);
        Assert.Contains("caused by: WebView2 loader was not found", diagnostic);
        Assert.DoesNotContain("::axtools", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FailureDiagnosticPreservesAllPlainOutputWithoutLineTrimming()
    {
        var longLine = "long-output:" + new string('X', 700);
        var plainLines = Enumerable.Range(1, 16)
            .Select(index => $"ordinary-output-{index:00}")
            .Append(longLine)
            .ToArray();
        var host = new ScriptedHost(
            plainLines
                .Append("::axtools {\"type\":\"result\",\"status\":\"failed\",\"exitCode\":1,\"message\":\"完整失败原因。\"}")
                .ToArray(),
            exitCode: 1);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());

        var result = await runner.RunAsync(CreateTask("complete-diagnostic"), CancellationToken.None);
        var diagnostic = result.CreateDiagnosticLogText();

        Assert.All(plainLines, line => Assert.Contains(line, diagnostic, StringComparison.Ordinal));
        Assert.True(
            diagnostic.IndexOf(plainLines[0], StringComparison.Ordinal) <
            diagnostic.IndexOf(plainLines[^1], StringComparison.Ordinal));
        Assert.DoesNotContain("已省略较早", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("该行已裁剪", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("::axtools", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryRequestStop_CancelableTaskReturnsStoppedResult()
    {
        var host = new StopAwareHost(AxTaskCancellationMode.Stop);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var task = runner.RunAsync(CreateTask("stop"), CancellationToken.None);
        await host.ModePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(runner.TryRequestStop());
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AxTaskStatus.Stopped, result.Status);
        Assert.NotEqual(AxTaskStatus.Failed, result.Status);
    }

    [Fact]
    public async Task TryRequestStop_LockedTaskIsRejectedWithoutCancellingHost()
    {
        var host = new StopAwareHost(AxTaskCancellationMode.Locked);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var task = runner.RunAsync(CreateTask("locked"), CancellationToken.None);
        await host.ModePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(runner.TryRequestStop());
        Assert.False(host.CancellationObserved);
        host.ReleaseLockedTask();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
    }

    [Fact]
    public void RequestShutdown_NoActiveTaskReturnsNoActiveTask()
    {
        var runner = new AxTaskRunner(
            new ScriptedHost(Array.Empty<string>(), exitCode: 0),
            new AxTaskEventParser());
        var decision = runner.RequestShutdown();

        Assert.Equal(AxTaskShutdownDecision.NoActiveTask, decision);
    }

    [Theory]
    [InlineData(AxTaskCancellationMode.Stop)]
    [InlineData(AxTaskCancellationMode.Cancel)]
    public async Task RequestShutdown_StopOrCancelRequestsCancellationAtomically(
        AxTaskCancellationMode mode)
    {
        var host = new StopAwareHost(mode);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var task = runner.RunAsync(CreateTask($"shutdown-{mode}"), CancellationToken.None);
        await host.ModePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var decision = runner.RequestShutdown();

        Assert.Equal(AxTaskShutdownDecision.StopRequested, decision);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(host.CancellationObserved);
        Assert.Equal(AxTaskStatus.Stopped, result.Status);
    }

    [Fact]
    public async Task RequestShutdown_LockedTaskIsBlockedWithoutCancellingToken()
    {
        var host = new StopAwareHost(AxTaskCancellationMode.Locked);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var task = runner.RunAsync(CreateTask("shutdown-locked"), CancellationToken.None);
        await host.ModePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var decision = runner.RequestShutdown();

        Assert.Equal(AxTaskShutdownDecision.BlockedLocked, decision);
        Assert.False(host.CancellationObserved);
        host.ReleaseLockedTask();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AxTaskStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task RunAsync_ForwardsStructuredEventsAndProtocolErrorsInOrder()
    {
        var host = new ScriptedHost(
            new[]
            {
                "::axtools {\"type\":\"stage\",\"stage\":\"environment\",\"message\":\"检查环境\"}",
                "::axtools invalid",
                "普通输出",
                "::axtools {\"type\":\"result\",\"status\":\"success\",\"exitCode\":0}"
            },
            exitCode: 0);
        var runner = new AxTaskRunner(host, new AxTaskEventParser());
        var observed = new List<string>();
        runner.EventReceived += taskEvent => observed.Add($"event:{taskEvent.Type}");
        runner.OutputReceived += output => observed.Add(
            output.ProtocolError is null ? $"text:{output.Text}" : "protocol-error");

        var result = await runner.RunAsync(CreateTask("events"), CancellationToken.None);

        Assert.Equal(
            new[] { "event:Stage", "protocol-error", "text:普通输出", "event:Result" },
            observed);
        Assert.Contains("1 条无效协议事件", result.Summary);
    }

    private static AxTaskDefinition CreateTask(string id) =>
        new(id, id, $"{id}.ps1", Array.Empty<string>());

    private sealed class ScriptedHost(
        IReadOnlyList<string> lines,
        int exitCode) : IAxProcessHost
    {
        public async Task<AxProcessResult> RunAsync(
            AxTaskDefinition definition,
            Func<AxTaskOutputStream, string, ValueTask> onLine,
            CancellationToken cancellationToken)
        {
            foreach (var line in lines)
            {
                await onLine(AxTaskOutputStream.StandardOutput, line);
            }

            return new AxProcessResult(exitCode, CancellationRequested: false);
        }
    }

    private sealed class ConcurrencyHost : IAxProcessHost
    {
        private int _active;
        private int _maximumConcurrency;
        private readonly ConcurrentQueue<string> _startOrder = new();

        public int MaximumConcurrency => _maximumConcurrency;

        public IReadOnlyList<string> StartOrder => _startOrder.ToArray();

        public async Task<AxProcessResult> RunAsync(
            AxTaskDefinition definition,
            Func<AxTaskOutputStream, string, ValueTask> onLine,
            CancellationToken cancellationToken)
        {
            _startOrder.Enqueue(definition.Id);
            var current = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximumConcurrency, current);
            await Task.Delay(80, cancellationToken);
            await onLine(
                AxTaskOutputStream.StandardOutput,
                "::axtools {\"type\":\"result\",\"status\":\"success\",\"exitCode\":0}");
            Interlocked.Decrement(ref _active);
            return new AxProcessResult(0, CancellationRequested: false);
        }
    }

    private sealed class StopAwareHost(AxTaskCancellationMode mode) : IAxProcessHost
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ModePublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public void ReleaseLockedTask() => _release.TrySetResult();

        public async Task<AxProcessResult> RunAsync(
            AxTaskDefinition definition,
            Func<AxTaskOutputStream, string, ValueTask> onLine,
            CancellationToken cancellationToken)
        {
            await onLine(
                AxTaskOutputStream.StandardOutput,
                $"::axtools {{\"type\":\"cancellation\",\"mode\":\"{mode.ToString().ToLowerInvariant()}\"}}");
            ModePublished.TrySetResult();

            if (mode == AxTaskCancellationMode.Locked)
            {
                await _release.Task;
                await onLine(
                    AxTaskOutputStream.StandardOutput,
                    "::axtools {\"type\":\"result\",\"status\":\"success\",\"exitCode\":0}");
                return new AxProcessResult(0, CancellationRequested: false);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                await onLine(
                    AxTaskOutputStream.StandardOutput,
                    "::axtools {\"type\":\"result\",\"status\":\"stopped\",\"exitCode\":130}");
            }

            return new AxProcessResult(130, CancellationRequested: true);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var original = Interlocked.CompareExchange(ref location, value, current);
                if (original == current)
                {
                    return;
                }

                current = original;
            }
        }
    }
}
