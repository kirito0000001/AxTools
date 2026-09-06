using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ManagedProcessService(
    IProcessController controller,
    TimeSpan gracefulCloseTimeout)
{
    private readonly IProcessController _controller = controller;
    private readonly TimeSpan _gracefulCloseTimeout = gracefulCloseTimeout;

    public ManagedProcessService()
        : this(new SystemProcessController(), TimeSpan.FromSeconds(10))
    {
    }

    public IReadOnlyList<ProcessSnapshot> FindMatches(string executablePath)
    {
        if (!TryNormalizePath(executablePath, out var targetPath))
        {
            return Array.Empty<ProcessSnapshot>();
        }

        return _controller.CaptureSnapshots()
            .Where(snapshot =>
                TryNormalizePath(snapshot.ExecutablePath, out var snapshotPath) &&
                string.Equals(
                    targetPath,
                    snapshotPath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<ManagedProcessCloseResult> RequestGracefulCloseAsync(
        ProcessSnapshot expectedSnapshot,
        CancellationToken cancellationToken)
    {
        var current = FindById(expectedSnapshot.ProcessId);
        if (current is null)
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.Exited,
                "程序已经退出。");
        }

        if (!PathsEqual(current.ExecutablePath, expectedSnapshot.ExecutablePath))
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.TargetChanged,
                "进程目标已变化，已取消关闭操作。");
        }

        try
        {
            if (!_controller.TryCloseMainWindow(expectedSnapshot.ProcessId))
            {
                return new ManagedProcessCloseResult(
                    ManagedProcessCloseStatus.ForceRequired,
                    "程序没有可响应的主窗口，需要再次确认后强制结束。");
            }

            var exited = await _controller.WaitForExitAsync(
                expectedSnapshot.ProcessId,
                _gracefulCloseTimeout,
                cancellationToken);
            return exited
                ? new ManagedProcessCloseResult(
                    ManagedProcessCloseStatus.Exited,
                    "程序已正常退出。")
                : new ManagedProcessCloseResult(
                    ManagedProcessCloseStatus.ForceRequired,
                    "程序未在 10 秒内退出，需要再次确认后强制结束。");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.Exited,
                "程序已经退出。");
        }
    }

    public async Task<ManagedProcessCloseResult> ForceTerminateAsync(
        ProcessSnapshot expectedSnapshot,
        CancellationToken cancellationToken)
    {
        var current = FindById(expectedSnapshot.ProcessId);
        if (current is null)
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.Exited,
                "程序已经退出。");
        }

        if (!PathsEqual(current.ExecutablePath, expectedSnapshot.ExecutablePath))
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.TargetChanged,
                "PID 对应的程序路径已变化，拒绝强制结束。");
        }

        try
        {
            _controller.KillProcessTree(expectedSnapshot.ProcessId);
            var exited = await _controller.WaitForExitAsync(
                expectedSnapshot.ProcessId,
                _gracefulCloseTimeout,
                cancellationToken);
            return new ManagedProcessCloseResult(
                exited
                    ? ManagedProcessCloseStatus.Exited
                    : ManagedProcessCloseStatus.Failed,
                exited ? "程序已强制结束。" : "强制结束后程序仍在运行。");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return new ManagedProcessCloseResult(
                ManagedProcessCloseStatus.Exited,
                "程序已经退出。");
        }
    }

    private ProcessSnapshot? FindById(int processId) =>
        _controller.CaptureSnapshots().FirstOrDefault(snapshot =>
            snapshot.ProcessId == processId);

    private static bool PathsEqual(string left, string right) =>
        TryNormalizePath(left, out var normalizedLeft) &&
        TryNormalizePath(right, out var normalizedRight) &&
        string.Equals(
            normalizedLeft,
            normalizedRight,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
