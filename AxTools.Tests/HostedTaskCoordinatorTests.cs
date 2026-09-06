using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class HostedTaskCoordinatorTests
{
    [Fact]
    public void TryBegin_RejectsSecondActionUntilActiveActionEnds()
    {
        var coordinator = new HostedTaskCoordinator();

        Assert.True(coordinator.TryBegin());
        Assert.False(coordinator.TryBegin());
        coordinator.End();
        Assert.True(coordinator.TryBegin());
    }

    [Fact]
    public async Task WaitForIdleAsync_WaitsForActiveActionToFinish()
    {
        var coordinator = new HostedTaskCoordinator();
        Assert.True(coordinator.TryBegin());

        var wait = coordinator.WaitForIdleAsync(CancellationToken.None);

        Assert.False(wait.IsCompleted);
        coordinator.End();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(coordinator.IsBusy);
    }
}
