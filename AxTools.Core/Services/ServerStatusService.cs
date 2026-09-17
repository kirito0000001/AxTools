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

    private readonly string _scriptPath = Path.Combine(
        Path.GetFullPath(scriptsRoot),
        "Servers",
        "Aliyun",
        "Read-AliyunServerStatus.ps1");

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
