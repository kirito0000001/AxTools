using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedProcessServiceTests
{
    [Fact]
    public async Task RequestGracefulCloseAsync_WhenProcessExits_DoesNotForceTerminate()
    {
        var snapshot = new ProcessSnapshot(42, "Tool", @"D:\Tools\Tool.exe");
        var controller = new FakeProcessController(snapshot)
        {
            ExitDuringWait = true
        };
        var service = new ManagedProcessService(controller, TimeSpan.FromSeconds(10));

        var result = await service.RequestGracefulCloseAsync(
            snapshot,
            CancellationToken.None);

        Assert.Equal(ManagedProcessCloseStatus.Exited, result.Status);
        Assert.Equal(1, controller.CloseRequestCount);
        Assert.Equal(0, controller.KillCount);
    }

    private sealed class FakeProcessController(ProcessSnapshot snapshot)
        : IProcessController
    {
        private ProcessSnapshot? _snapshot = snapshot;

        public bool ExitDuringWait { get; init; }

        public int CloseRequestCount { get; private set; }

        public int KillCount { get; private set; }

        public IReadOnlyList<ProcessSnapshot> CaptureSnapshots() =>
            _snapshot is null ? Array.Empty<ProcessSnapshot>() : new[] { _snapshot };

        public bool TryCloseMainWindow(int processId)
        {
            CloseRequestCount++;
            return true;
        }

        public Task<bool> WaitForExitAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (ExitDuringWait)
            {
                _snapshot = null;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public void KillProcessTree(int processId)
        {
            KillCount++;
            _snapshot = null;
        }
    }
}
