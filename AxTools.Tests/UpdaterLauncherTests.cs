using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class UpdaterLauncherTests
{
    [Fact]
    public async Task LaunchAsync_ReturnsOnlyAfterReadySignal()
    {
        var root = CreateRoot();
        try
        {
            var script = Path.Combine(root, "updater.ps1");
            await File.WriteAllTextAsync(script, "param($ReadySignalPath) Start-Sleep -Milliseconds 80; 'READY' | Set-Content -LiteralPath $ReadySignalPath; Start-Sleep -Milliseconds 100");
            var signal = Path.Combine(root, "ready.txt");
            var launcher = new UpdaterLauncher("pwsh", script);
            using var process = await launcher.LaunchAsync(CreateRequest(root, signal), TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.True(File.Exists(signal));
            if (!process.HasExited) process.Kill(true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaunchAsync_ThrowsWhenReadySignalTimesOut()
    {
        var root = CreateRoot();
        try
        {
            var script = Path.Combine(root, "updater.ps1");
            await File.WriteAllTextAsync(script, "param($ReadySignalPath) Start-Sleep -Seconds 5");
            var launcher = new UpdaterLauncher("pwsh", script);
            await Assert.ThrowsAsync<TimeoutException>(() => launcher.LaunchAsync(CreateRequest(root, Path.Combine(root, "missing.txt")), TimeSpan.FromMilliseconds(150), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    private static UpdaterLaunchRequest CreateRequest(string root, string signal) => new(
        Path.Combine(root, "package.zip"), root, Environment.ProcessId, "AxTools.exe", "1.0.1-beta.1", signal, Path.Combine(root, "update.log"));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AxTools-Launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.zip"), "test");
        return root;
    }
}
