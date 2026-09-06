using System.Diagnostics;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class UpdaterLauncher
{
    private readonly string _powerShellPath;
    private readonly string _updaterScriptPath;

    public UpdaterLauncher(string powerShellPath, string updaterScriptPath)
    {
        _powerShellPath = powerShellPath;
        _updaterScriptPath = Path.GetFullPath(updaterScriptPath);
    }

    public async Task<Process> LaunchAsync(
        UpdaterLaunchRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        if (File.Exists(request.ReadySignalPath)) File.Delete(request.ReadySignalPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(_updaterScriptPath)!
        };
        foreach (var argument in BuildArguments(request)) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 AxTools 外部更新器。");
        var deadline = DateTime.UtcNow + readyTimeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(request.ReadySignalPath)) return process;
                if (process.HasExited) throw new InvalidOperationException($"外部更新器在就绪前退出，退出码 {process.ExitCode}。");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("外部更新器未能在限定时间内完成预检，AxTools 将保持运行。");
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }
            process.Dispose();
            throw;
        }
    }

    private IEnumerable<string> BuildArguments(UpdaterLaunchRequest request)
    {
        yield return "-NoLogo";
        yield return "-NoProfile";
        yield return "-ExecutionPolicy";
        yield return "Bypass";
        yield return "-File";
        yield return _updaterScriptPath;
        yield return "-PackagePath";
        yield return Path.GetFullPath(request.PackagePath);
        yield return "-TargetDirectory";
        yield return Path.GetFullPath(request.TargetDirectory);
        yield return "-MainProcessId";
        yield return request.MainProcessId.ToString();
        yield return "-EntryExe";
        yield return request.EntryExe;
        yield return "-TargetVersion";
        yield return request.TargetVersion;
        yield return "-ReadySignalPath";
        yield return Path.GetFullPath(request.ReadySignalPath);
        yield return "-LogPath";
        yield return Path.GetFullPath(request.LogPath);
    }
}
