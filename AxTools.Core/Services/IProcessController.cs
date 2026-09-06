using AxTools.Core.Models;

namespace AxTools.Core.Services;

public interface IProcessController
{
    IReadOnlyList<ProcessSnapshot> CaptureSnapshots();

    bool TryCloseMainWindow(int processId);

    Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    void KillProcessTree(int processId);
}
