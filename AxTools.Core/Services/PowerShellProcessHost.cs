using System.Diagnostics;
using System.Text;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class PowerShellProcessHost : IAxProcessHost
{
    private readonly string _powerShellPath;
    private readonly TimeSpan _gracefulStopTimeout;
    private readonly TimeSpan _outputDrainTimeout;
    private readonly ToolchainSettings? _toolchains;

    public PowerShellProcessHost(
        string powerShellPath,
        TimeSpan? gracefulStopTimeout = null,
        ToolchainSettings? toolchains = null,
        TimeSpan? outputDrainTimeout = null)
    {
        _powerShellPath = Path.GetFullPath(powerShellPath);
        _gracefulStopTimeout = gracefulStopTimeout ?? TimeSpan.FromMilliseconds(1800);
        _outputDrainTimeout = outputDrainTimeout ?? TimeSpan.FromMilliseconds(800);
        _toolchains = toolchains;
    }

    public async Task<AxProcessResult> RunAsync(
        AxTaskDefinition definition,
        Func<AxTaskOutputStream, string, ValueTask> onLine,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.GetFullPath(definition.ScriptPath);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("任务脚本不存在。", scriptPath);
        }

        var cancellationRoot = Path.Combine(Path.GetTempPath(), "AxTools", "Cancellation");
        Directory.CreateDirectory(cancellationRoot);
        var cancellationFile = Path.Combine(cancellationRoot, $"{Guid.NewGuid():N}.signal");
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                scriptPath,
                cancellationFile,
                definition.Arguments,
                definition.EnvironmentVariables)
        };
        using var callbackGate = new SemaphoreSlim(1, 1);
        var cancellationRequested = false;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("PowerShell 7 进程未能启动。");
            }

            var standardOutputTask = ReadLinesAsync(
                process.StandardOutput.BaseStream,
                AxTaskOutputStream.StandardOutput,
                onLine,
                callbackGate);
            var standardErrorTask = ReadLinesAsync(
                process.StandardError.BaseStream,
                AxTaskOutputStream.StandardError,
                onLine,
                callbackGate);
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            if (await Task.WhenAny(exitTask, cancellationTask) == cancellationTask)
            {
                cancellationRequested = true;
                await File.WriteAllTextAsync(
                    cancellationFile,
                    "stop",
                    new UTF8Encoding(false),
                    CancellationToken.None);

                if (await Task.WhenAny(exitTask, Task.Delay(_gracefulStopTimeout)) != exitTask &&
                    !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            await exitTask;
            await DrainOutputAsync(
                process,
                Task.WhenAll(standardOutputTask, standardErrorTask));
            return new AxProcessResult(process.ExitCode, cancellationRequested);
        }
        finally
        {
            try
            {
                File.Delete(cancellationFile);
            }
            catch (IOException)
            {
                // A stale signal is harmless and will not be reused.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup failure must not hide the task result.
            }
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string scriptPath,
        string cancellationFile,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["AXTOOLS_CANCEL_FILE"] = cancellationFile;
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        ApplyToolchains(startInfo);
        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                startInfo.Environment[name] = string.Equals(
                    name,
                    "PATH",
                    StringComparison.OrdinalIgnoreCase)
                    ? JoinPath(value, GetEnvironmentValue(startInfo, "PATH"))
                    : value;
            }
        }

        return startInfo;
    }

    private void ApplyToolchains(ProcessStartInfo startInfo)
    {
        if (_toolchains is null)
        {
            return;
        }

        var pathPrefixes = new[]
        {
            GetDirectory(_toolchains.DotNetPath),
            GetDirectory(_toolchains.NodePath),
            GetDirectory(_toolchains.NpmPath),
            GetDirectory(_toolchains.CargoPath),
            GetDirectory(_toolchains.BandizipPath),
            string.IsNullOrWhiteSpace(_toolchains.JavaHome)
                ? null
                : Path.Combine(_toolchains.JavaHome, "bin"),
            string.IsNullOrWhiteSpace(_toolchains.AndroidSdkRoot)
                ? null
                : Path.Combine(_toolchains.AndroidSdkRoot, "platform-tools")
        };
        startInfo.Environment["PATH"] = JoinPath(
            string.Join(
                Path.PathSeparator,
                pathPrefixes
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            GetEnvironmentValue(startInfo, "PATH"));

        if (!string.IsNullOrWhiteSpace(_toolchains.DotNetPath))
        {
            startInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(
                _toolchains.DotNetPath)!;
        }

        if (!string.IsNullOrWhiteSpace(_toolchains.JavaHome))
        {
            startInfo.Environment["JAVA_HOME"] = _toolchains.JavaHome;
        }

        if (!string.IsNullOrWhiteSpace(_toolchains.AndroidSdkRoot))
        {
            startInfo.Environment["ANDROID_HOME"] = _toolchains.AndroidSdkRoot;
            startInfo.Environment["ANDROID_SDK_ROOT"] = _toolchains.AndroidSdkRoot;
        }

        if (!string.IsNullOrWhiteSpace(_toolchains.VisualStudioRoot))
        {
            startInfo.Environment["AXTOOLS_VISUAL_STUDIO_ROOT"] =
                _toolchains.VisualStudioRoot;
        }

        if (!string.IsNullOrWhiteSpace(_toolchains.MsvcVersion))
        {
            startInfo.Environment["AXTOOLS_MSVC_VERSION"] =
                _toolchains.MsvcVersion;
        }
    }

    private static string? GetDirectory(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);

    private static string? GetEnvironmentValue(
        ProcessStartInfo startInfo,
        string name) =>
        startInfo.Environment.TryGetValue(name, out var value) ? value : null;

    private static string JoinPath(string? prefix, string? existing) =>
        string.Join(
            Path.PathSeparator,
            new[] { prefix, existing }.Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>
    /// 按字节读行，再决定用哪种编码解码。任务协议自己写的是 UTF-8，但 PowerShell 在"脚本还没跑起来"
    /// 时抛的错（参数绑定失败、语法错、脚本文件不存在）用的是系统本地代码页（中文机器上是 GBK），
    /// 硬按 UTF-8 解就是一串问号 —— 用户看到的就是"发布失败了但日志看不懂"。这里逐行回退。
    /// </summary>
    private static async Task ReadLinesAsync(
        Stream stream,
        AxTaskOutputStream outputStream,
        Func<AxTaskOutputStream, string, ValueTask> onLine,
        SemaphoreSlim callbackGate)
    {
        var buffer = new byte[8192];
        var line = new List<byte>();
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or IOException)
            {
                // 超时后主流程会主动关掉管道，这里安静收尾。
                break;
            }

            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    await EmitLineAsync(line, outputStream, onLine, callbackGate).ConfigureAwait(false);
                    line.Clear();
                }
                else if (value != (byte)'\r')
                {
                    line.Add(value);
                }
            }
        }

        if (line.Count > 0)
        {
            await EmitLineAsync(line, outputStream, onLine, callbackGate).ConfigureAwait(false);
        }
    }

    private static async Task EmitLineAsync(
        List<byte> bytes,
        AxTaskOutputStream outputStream,
        Func<AxTaskOutputStream, string, ValueTask> onLine,
        SemaphoreSlim callbackGate)
    {
        var text = DecodeLine(bytes);
        await callbackGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await onLine(outputStream, text).ConfigureAwait(false);
        }
        finally
        {
            callbackGate.Release();
        }
    }

    /// <summary>UTF-8 优先；解不出来（宿主自己写的本地代码页文本）就按系统 ANSI 代码页回退。</summary>
    private static string DecodeLine(List<byte> bytes)
    {
        var raw = bytes.ToArray();
        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return LocalAnsi.GetString(raw);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding LocalAnsi = ResolveLocalAnsiEncoding();

    private static Encoding ResolveLocalAnsiEncoding()
    {
        try
        {
            // .NET 默认只带 UTF-8 系；GBK 这类代码页要先注册（Windows App SDK 已经带了实现）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private async Task DrainOutputAsync(
        Process process,
        Task outputTask)
    {
        if (await Task.WhenAny(outputTask, Task.Delay(_outputDrainTimeout)) == outputTask)
        {
            await outputTask;
            return;
        }

        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        _ = outputTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
