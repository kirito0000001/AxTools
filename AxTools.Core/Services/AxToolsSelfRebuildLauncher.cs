using System.Diagnostics;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class AxToolsSelfRebuildLauncher
{
    private readonly string _powerShellPath;
    private readonly string _scriptPath;

    public AxToolsSelfRebuildLauncher(string powerShellPath, string scriptPath)
    {
        _powerShellPath = Path.GetFullPath(powerShellPath);
        _scriptPath = Path.GetFullPath(scriptPath);
    }

    public async Task<Process> LaunchAsync(
        SelfRebuildLaunchRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
        {
            throw new FileNotFoundException("缺少 AxTools 外部自重建脚本。", _scriptPath);
        }

        if (File.Exists(request.ReadySignalPath))
        {
            File.Delete(request.ReadySignalPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = request.ProjectRoot
        };
        foreach (var argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("无法启动 AxTools 外部自重建助手。");
        var deadline = DateTime.UtcNow + readyTimeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(request.ReadySignalPath))
                {
                    return process;
                }

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"外部自重建助手在就绪前退出，退出码 {process.ExitCode}。详情：{request.LogPath}");
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("外部自重建助手未能在限定时间内完成预检，AxTools 保持运行。");
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Failure to stop the helper must not hide the launch error.
                }
            }

            process.Dispose();
            throw;
        }
    }

    private IEnumerable<string> BuildArguments(SelfRebuildLaunchRequest request)
    {
        yield return "-NoLogo";
        yield return "-NoProfile";
        yield return "-NonInteractive";
        yield return "-ExecutionPolicy";
        yield return "Bypass";
        yield return "-File";
        yield return _scriptPath;
        yield return "-ProjectRoot";
        yield return Path.GetFullPath(request.ProjectRoot);
        yield return "-DevelopmentExecutable";
        yield return Path.GetFullPath(request.DevelopmentExecutable);
        yield return "-MainProcessId";
        yield return request.MainProcessId.ToString();
        yield return "-ReadySignalPath";
        yield return Path.GetFullPath(request.ReadySignalPath);
        yield return "-LogPath";
        yield return Path.GetFullPath(request.LogPath);
        yield return "-ResultPath";
        yield return Path.GetFullPath(request.ResultPath);
    }
}
