using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ServerStatusService(
    PowerShellProcessHost processHost,
    string scriptsRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// 以 HTTPS 直接读取看门狗写好的状态文件。
    /// 相比每次轮询都启动远端 PowerShell，这条路不产生远程进程。
    /// </summary>
    public async Task<ServerStatusSnapshot> ReadFromUrlAsync(
        string statusUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            throw new InvalidOperationException("未配置服务器状态地址。");
        }

        var separator = statusUrl.Contains('?') ? '&' : '?';
        var requestUrl = $"{statusUrl}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var response = await Http.GetAsync(requestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadAsStringAsync(cancellationToken))
            .TrimStart('\uFEFF', '\u200B')
            .Trim();
        var snapshot = JsonSerializer.Deserialize<ServerStatusSnapshot>(json, JsonOptions);
        return snapshot ?? throw new InvalidDataException("服务器状态内容为空。");
    }

    private readonly string _scriptPath = Path.Combine(
        Path.GetFullPath(scriptsRoot),
        "Servers",
        "Aliyun",
        "Read-AliyunServerStatus.ps1");

    private readonly string _actionScriptPath = Path.Combine(
        Path.GetFullPath(scriptsRoot),
        "Servers",
        "Aliyun",
        "Invoke-AliyunServerAction.ps1");

    public async Task<ServerActionResult> RunActionAsync(
        string sshTarget,
        string action,
        string instanceName,
        CancellationToken cancellationToken,
        int timeoutSeconds = 30)
    {
        if (string.IsNullOrWhiteSpace(sshTarget))
        {
            throw new InvalidOperationException("未配置服务器 SSH 目标。");
        }

        if (!File.Exists(_actionScriptPath))
        {
            throw new FileNotFoundException("缺少服务器动作脚本。", _actionScriptPath);
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"axtools-server-action-{Guid.NewGuid():N}.json");
        try
        {
            var arguments = new List<string>
            {
                "-SshTarget", sshTarget,
                "-Action", action,
                "-OutputPath", outputPath,
                "-TimeoutSeconds", timeoutSeconds.ToString()
            };
            if (!string.IsNullOrWhiteSpace(instanceName))
            {
                arguments.Add("-InstanceName");
                arguments.Add(instanceName);
            }

            var definition = new AxTaskDefinition(
                $"server-action-{Guid.NewGuid():N}",
                $"服务器动作 {action}",
                _actionScriptPath,
                arguments);
            var lines = new List<string>();
            var result = await processHost.RunAsync(
                definition,
                (_, line) =>
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                string detail;
                lock (lines)
                {
                    detail = string.Join(
                        Environment.NewLine,
                        lines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(15));
                }

                throw new InvalidOperationException(
                    $"服务器动作 {action} 失败，退出码 {result.ExitCode}。" +
                    (string.IsNullOrWhiteSpace(detail)
                        ? string.Empty
                        : Environment.NewLine + detail));
            }

            var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ServerActionResult(
                root.TryGetProperty("success", out var success) && success.GetBoolean(),
                root.TryGetProperty("action", out var actionName) ? actionName.GetString() ?? action : action,
                root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("data", out var data) ? data.Clone() : null);
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (IOException)
            {
                // Temporary file cleanup must not hide the action result.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary file cleanup must not hide the action result.
            }
        }
    }

    public async Task<ServerStatusSnapshot> ReadAsync(
        string sshTarget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sshTarget))
        {
            throw new InvalidOperationException("未配置服务器 SSH 目标。");
        }

        if (!File.Exists(_scriptPath))
        {
            throw new FileNotFoundException("缺少服务器状态读取脚本。", _scriptPath);
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"axtools-server-status-{Guid.NewGuid():N}.json");
        try
        {
            var definition = new AxTaskDefinition(
                $"server-status-{Guid.NewGuid():N}",
                "读取服务器状态",
                _scriptPath,
                ["-SshTarget", sshTarget, "-OutputPath", outputPath]);
            var result = await processHost.RunAsync(
                definition,
                (_, _) => ValueTask.CompletedTask,
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    $"读取服务器状态失败，退出码 {result.ExitCode}。");
            }

            await using var stream = File.OpenRead(outputPath);
            var snapshot = await JsonSerializer.DeserializeAsync<ServerStatusSnapshot>(
                stream,
                JsonOptions,
                cancellationToken);
            return snapshot ?? throw new InvalidDataException("服务器状态内容为空。");
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (IOException)
            {
                // Temporary file cleanup must not hide the read result.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary file cleanup must not hide the read result.
            }
        }
    }
}
