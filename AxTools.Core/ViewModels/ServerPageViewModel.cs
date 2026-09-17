using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed class ServerInstanceViewModel(ServerInstanceStatus status)
{
    public string Name { get; } = status.Name;

    public string DisplayName { get; } = string.IsNullOrWhiteSpace(status.DisplayName)
        ? status.Name
        : status.DisplayName;

    public string Protocol { get; } = status.Protocol;

    public int Port { get; } = status.Port;

    public bool Maintenance { get; } = status.Maintenance;

    public bool? Running { get; } = status.Running;

    public int? Pid { get; } = status.Pid;

    public bool? PortListening { get; } = status.PortListening;

    public int? DuplicateProcesses { get; } = status.DuplicateProcesses;

    public DateTimeOffset? LastRestartAt { get; } = status.LastRestartAt;

    public string Action { get; } = status.Action;

    public string? LastError { get; } = status.LastError;

    public string EndpointText => $"{Protocol} {Port}";

    public string StateKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LastError))
            {
                return "error";
            }

            if (Maintenance)
            {
                return "maintenance";
            }

            if (Running == true && PortListening == true)
            {
                return Action is "restarted" or "deduplicated" ? "recovered" : "healthy";
            }

            if (Running == true || Action is "starting" or "cooldown")
            {
                return "warning";
            }

            return "offline";
        }
    }

    public string StatusText => StateKey switch
    {
        "healthy" => "运行中",
        "recovered" => "运行中（本次已自动恢复）",
        "warning" => "异常：进程在但端口未监听",
        "maintenance" => "维护模式，不参与自动重启",
        "offline" => "未运行",
        _ => "检查失败"
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Pid is { } pid)
            {
                parts.Add($"PID {pid}");
            }

            if (DuplicateProcesses is > 0)
            {
                parts.Add($"重复进程 {DuplicateProcesses}");
            }

            if (LastRestartAt is { } restartedAt)
            {
                parts.Add($"最近重启 {restartedAt.LocalDateTime:MM-dd HH:mm:ss}");
            }

            if (!string.IsNullOrWhiteSpace(LastError))
            {
                parts.Add(LastError);
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed class ServerPageViewModel(
    ServerStatusService statusService,
    string sshTarget)
{
    public IReadOnlyList<ServerInstanceViewModel> Instances { get; private set; } = [];

    public string Headline { get; private set; } = "尚未读取服务器状态。";

    public string Detail { get; private set; } = string.Empty;

    public string? ErrorText { get; private set; }

    public bool IsBusy { get; private set; }

    public DateTimeOffset? LastUpdatedAt { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var snapshot = await statusService.ReadAsync(sshTarget, cancellationToken);
            Instances = snapshot.Instances
                .Select(status => new ServerInstanceViewModel(status))
                .ToArray();
            ErrorText = null;
            LastUpdatedAt = DateTimeOffset.Now;

            var watchdog = snapshot.Watchdog;
            var healthy = Instances.Count(item => item.StateKey is "healthy" or "recovered");
            Headline = $"看门狗运行中 · {healthy}/{Instances.Count} 个服务端正常";
            Detail = watchdog is null
                ? $"主机 {snapshot.Host}"
                : $"主机 {snapshot.Host} · 看门狗 PID {watchdog.ProcessId} · 检查间隔 {watchdog.CheckIntervalSeconds}s";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            Headline = "无法读取服务器状态";
            Detail = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
