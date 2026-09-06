using System.Diagnostics;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class SystemProcessController : IProcessController
{
    public IReadOnlyList<ProcessSnapshot> CaptureSnapshots() =>
        ProcessConflictDetector.CaptureRunningProcesses();

    public bool TryCloseMainWindow(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.CloseMainWindow();
    }

    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linkedCancellation.Token);
                return true;
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return process.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void KillProcessTree(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
    }
}
