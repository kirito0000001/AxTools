using System.Text.Json;
using AxTools.Core.Catalog;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 基于 Git 的服务端增量更新。远端仓库以裸仓库形式存在，运行目录作为外部工作树，
/// 运行目录里不会出现 .git，Saved\ 由 .gitignore 排除。
/// </summary>
public sealed class ServerGitUpdateService(
    PowerShellProcessHost processHost,
    string scriptsRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _remoteScriptPath = Path.Combine(
        Path.GetFullPath(scriptsRoot),
        "Servers",
        "Aliyun",
        "Invoke-AliyunServerGit.ps1");

    private readonly string _localScriptPath = Path.Combine(
        Path.GetFullPath(scriptsRoot),
        "Servers",
        "Aliyun",
        "Invoke-AliyunServerRepo.ps1");

    public string? LastDiagnostic { get; private set; }

    /// <summary>读取一个程序在服务器上的部署状态。</summary>
    public async Task<ServerGitRemoteStatus> ReadRemoteStatusAsync(
        ServerProgramProfile profile,
        CancellationToken cancellationToken) =>
        await RunRemoteAsync<ServerGitRemoteStatus>(
            "status",
            profile,
            commitMessage: string.Empty,
            timeoutSeconds: 120,
            cancellationToken);

    /// <summary>以当前线上产物为第一版初始化远端仓库。</summary>
    public async Task<ServerGitRemoteStatus> BootstrapAsync(
        ServerProgramProfile profile,
        string commitSubject,
        CancellationToken cancellationToken) =>
        await RunRemoteAsync<ServerGitRemoteStatus>(
            "bootstrap",
            profile,
            commitSubject,
            timeoutSeconds: 1800,
            cancellationToken);

    /// <summary>把服务器切换到已推送的最新提交（走维护闸门）。</summary>
    public async Task<ServerGitSwitchReport> ApplyAsync(
        ServerProgramProfile profile,
        CancellationToken cancellationToken) =>
        await RunRemoteAsync<ServerGitSwitchReport>(
            "apply",
            profile,
            commitMessage: string.Empty,
            timeoutSeconds: 600,
            cancellationToken);

    /// <summary>把服务器回退到上一版（走维护闸门）。</summary>
    public async Task<ServerGitSwitchReport> RollbackAsync(
        ServerProgramProfile profile,
        CancellationToken cancellationToken) =>
        await RunRemoteAsync<ServerGitSwitchReport>(
            "rollback",
            profile,
            commitMessage: string.Empty,
            timeoutSeconds: 600,
            cancellationToken);

    public async Task<ServerGitLocalStatus> ReadLocalStatusAsync(
        ServerProgramProfile profile,
        string? localRoot,
        CancellationToken cancellationToken) =>
        await RunLocalAsync<ServerGitLocalStatus>(
            profile,
            localRoot,
            "status",
            sourceDirectory: null,
            commitMessage: null,
            cancellationToken);

    /// <summary>把打包产物镜像到本机仓库并暂存；提交由用户确认后单独执行。</summary>
    public async Task<ServerGitImportResult> ImportBuildAsync(
        ServerProgramProfile profile,
        string sourceDirectory,
        string? localRoot,
        CancellationToken cancellationToken) =>
        await RunLocalAsync<ServerGitImportResult>(
            profile,
            localRoot,
            "import",
            sourceDirectory,
            commitMessage: null,
            cancellationToken);

    public async Task<ServerGitCommitResult> CommitAsync(
        ServerProgramProfile profile,
        string commitMessage,
        string? localRoot,
        CancellationToken cancellationToken) =>
        await RunLocalAsync<ServerGitCommitResult>(
            profile,
            localRoot,
            "commit",
            sourceDirectory: null,
            commitMessage,
            cancellationToken);

    public async Task<ServerGitLocalStatus> PushAsync(
        ServerProgramProfile profile,
        string? localRoot,
        CancellationToken cancellationToken)
    {
        var data = await RunLocalAsync<ServerGitPushResult>(
            profile,
            localRoot,
            "push",
            sourceDirectory: null,
            commitMessage: null,
            cancellationToken);
        return data.Status;
    }

    public async Task<ServerGitPreviewResult> PreviewAsync(
        ServerProgramProfile profile,
        string? localRoot,
        CancellationToken cancellationToken) =>
        await RunLocalAsync<ServerGitPreviewResult>(
            profile,
            localRoot,
            "preview",
            sourceDirectory: null,
            commitMessage: null,
            cancellationToken);

    private Task<T> RunRemoteAsync<T>(
        string action,
        ServerProgramProfile profile,
        string commitMessage,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        RunScriptAsync<T>(
            _remoteScriptPath,
            [
                "-SshTarget", ServerProgramCatalog.SshTarget,
                "-Action", action,
                "-ProgramKey", profile.Key,
                "-CommitMessage", commitMessage ?? string.Empty,
                "-TimeoutSeconds", timeoutSeconds.ToString()
            ],
            $"{profile.DisplayName} · 服务端{DescribeAction(action)}",
            cancellationToken);

    private Task<T> RunLocalAsync<T>(
        ServerProgramProfile profile,
        string? localRoot,
        string action,
        string? sourceDirectory,
        string? commitMessage,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-LocalRepoPath", ServerProgramCatalog.GetLocalRepoPath(profile, localRoot),
            "-Action", action
        };
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            arguments.Add("-SourceDirectory");
            arguments.Add(sourceDirectory);
        }

        if (!string.IsNullOrWhiteSpace(commitMessage))
        {
            arguments.Add("-CommitMessage");
            arguments.Add(commitMessage);
        }

        return RunScriptAsync<T>(
            _localScriptPath,
            arguments,
            $"{profile.DisplayName} · {DescribeAction(action)}（本机）",
            cancellationToken);
    }

    private static string DescribeAction(string action) => action switch
    {
        "status" => "状态",
        "bootstrap" => "初始化仓库",
        "apply" => "切换最新版本",
        "rollback" => "回退上个版本",
        "import" => "导入新构建",
        "commit" => "提交",
        "push" => "推送",
        "preview" => "查看待更新内容",
        _ => action
    };

    private async Task<T> RunScriptAsync<T>(
        string scriptPath,
        IReadOnlyList<string> arguments,
        string title,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("缺少服务端更新脚本。", scriptPath);
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"axtools-servergit-{Guid.NewGuid():N}.json");
        try
        {
            var fullArguments = new List<string>(arguments) { "-OutputPath", outputPath };
            var definition = new AxTaskDefinition(
                $"servergit-{Guid.NewGuid():N}",
                title,
                scriptPath,
                fullArguments);

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

                LastDiagnostic = detail;
                throw new InvalidOperationException(
                    $"{title}失败，退出码 {result.ExitCode}。" +
                    (string.IsNullOrWhiteSpace(detail)
                        ? string.Empty
                        : Environment.NewLine + detail));
            }

            var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successNode) && successNode.GetBoolean();
            var message = root.TryGetProperty("message", out var messageNode)
                ? messageNode.GetString() ?? string.Empty
                : string.Empty;
            if (!success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message) ? $"{title}失败。" : $"{title}失败：{message}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                throw new InvalidDataException($"{title}没有返回数据。");
            }

            LastDiagnostic = null;
            return data.Deserialize<T>(JsonOptions)
                ?? throw new InvalidDataException($"{title}返回的数据无法解析。");
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
                // 清理临时文件不能掩盖真正的结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}

public sealed class ServerGitSwitchReport
{
    public string Program { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Message { get; set; }

    public bool MaintenanceReleased { get; set; }

    public bool Restored { get; set; }

    public string? RestoreError { get; set; }

    public ServerGitCommitInfo? Target { get; set; }

    public List<ServerGitHealthEntry> Health { get; set; } = [];

    public List<string> Steps { get; set; } = [];

    public string Describe()
    {
        var verb = Action == "rollback" ? "回退" : "应用";
        var target = Target?.Describe() ?? "未知版本";
        if (!Success)
        {
            return $"{verb}失败：{Message ?? "未返回原因"}"
                + (Restored ? "（已恢复到切换前的版本）" : "（未能恢复，服务端保持维护模式）");
        }

        var health = Health.Count == 0
            ? string.Empty
            : "；" + string.Join(
                "，",
                Health.Select(item => $"{item.Instance} {(item.Healthy ? "正常" : "异常")}"));
        return $"{verb}完成：{target}{health}";
    }
}

public sealed class ServerGitHealthEntry
{
    public string Instance { get; set; } = string.Empty;

    public bool Running { get; set; }

    public bool Listening { get; set; }

    public int Port { get; set; }

    public bool Healthy { get; set; }
}

public sealed class ServerGitCommitResult
{
    public bool Committed { get; set; }

    public string Detail { get; set; } = string.Empty;

    public ServerGitLocalStatus? Status { get; set; }
}

public sealed class ServerGitPushResult
{
    public bool Pushed { get; set; }

    public ServerGitLocalStatus Status { get; set; } = new();
}

public sealed class ServerGitPreviewResult
{
    public List<ServerGitCommitInfo> Commits { get; set; } = [];

    public List<string> DiffStat { get; set; } = [];
}
