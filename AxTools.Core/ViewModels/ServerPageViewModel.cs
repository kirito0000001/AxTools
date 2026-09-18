using AxTools.Core.Models;
using AxTools.Core.Catalog;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed record ServerActionItem(string Key, string DisplayName, string IconGlyph);

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

    /// <summary>所属服务端程序，用于在服务器分区中按页签归类。</summary>
    public string ProgramKey =>
        Name.StartsWith("CrossingVoid", StringComparison.OrdinalIgnoreCase) ? "crossingvoid" :
        Name.Contains("Naruto", StringComparison.OrdinalIgnoreCase) ? "narutobp" :
        Name.Contains("Fantasy", StringComparison.OrdinalIgnoreCase) ? "fantasyproject" :
        "other";

    public string ProgramTitle => ProgramKey switch
    {
        "crossingvoid" => "零境交错",
        "narutobp" => "火影BP",
        "fantasyproject" => "幻杀",
        _ => Name
    };

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
    string sshTarget,
    string statusUrl,
    LogService? logService = null,
    ServerGitUpdateService? updateService = null,
    GlobalProgressViewModel? globalProgress = null)
{
    /// <summary>供页面把服务器动作的结果写进 AxTools 输出日志。</summary>
    public LogService? Log => logService;

    /// <summary>AxTools 底部那条全局进度条；服务器动作期间由页面驱动它。</summary>
    public GlobalProgressViewModel? GlobalProgress => globalProgress;

    private readonly ServerGitUpdateService? _updateService = updateService;

    private readonly Dictionary<string, ServerProgramUpdateState> _updateStates =
        new(StringComparer.Ordinal);

    /// <summary>是否具备服务端 Git 更新能力（缺少任务运行器时为 false）。</summary>
    public bool HasUpdateService => _updateService is not null;

    public ServerProgramUpdateState? GetUpdateState(string programKey) =>
        _updateStates.GetValueOrDefault(programKey);

    /// <summary>
    /// 只刷新一个程序的服务端更新状态。远端状态要起一次 SSH，因此只在用户进入
    /// 对应页签或执行动作后调用，不跟着看门狗轮询一起跑。
    /// </summary>
    public async Task<ServerProgramUpdateState> RefreshUpdateStateAsync(
        ServerProgramProfile profile,
        CancellationToken cancellationToken)
    {
        var state = new ServerProgramUpdateState
        {
            Profile = profile,
            LocalRepoPath = ServerProgramCatalog.GetLocalRepoPath(profile)
        };

        if (_updateService is null)
        {
            state.Error = "任务运行器尚未初始化，无法执行服务端更新。";
            _updateStates[profile.Key] = state;
            return state;
        }

        try
        {
            state.Remote = await _updateService.ReadRemoteStatusAsync(profile, cancellationToken);
        }
        catch (Exception exception)
        {
            state.Error = exception.Message;
        }

        try
        {
            state.Local = await _updateService.ReadLocalStatusAsync(profile, null, cancellationToken);
        }
        catch (Exception exception)
        {
            state.Error ??= exception.Message;
        }

        _updateStates[profile.Key] = state;
        return state;
    }

    public ServerGitUpdateService UpdateService =>
        _updateService ?? throw new InvalidOperationException("服务端更新服务不可用。");

    public IReadOnlyList<ServerInstanceViewModel> Instances { get; private set; } = [];

    public string Headline { get; private set; } = "尚未读取服务器状态。";

    public string Detail { get; private set; } = string.Empty;

    public string? ErrorText { get; private set; }

    public bool IsBusy { get; private set; }

    public DateTimeOffset? LastUpdatedAt { get; private set; }

    public string SshTarget => sshTarget;

    /// <summary>服务器级动作列表；后续新增上传、更新、清理只需在这里加一项。</summary>
    public IReadOnlyList<ServerActionItem> Actions { get; } =
    [
        new("watchdog-log", "看门狗日志", "\uE7C3"),
        new("old-logs", "清除过期日志", "\uE74D"),
        new("all-logs", "清除全部日志", "\uE74D"),
        new("caches", "清理缓存", "\uE74D"),
        new("crashes", "清理崩溃文件", "\uE74D"),
        new("watchdog-task", "暂停 / 恢复监控", "\uE769")
    ];

    public IEnumerable<ServerInstanceViewModel> InstancesOf(string programKey) =>
        Instances.Where(instance => string.Equals(instance.ProgramKey, programKey, StringComparison.Ordinal));

    public Task<ServerActionResult> RunActionAsync(
        string instanceName,
        string action,
        CancellationToken cancellationToken) =>
        statusService.RunActionAsync(sshTarget, action, instanceName, cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var snapshot = await statusService.ReadFromUrlAsync(statusUrl, cancellationToken);
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
