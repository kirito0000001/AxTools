namespace AxTools.Core.Models;

public sealed class ServerStatusSnapshot
{
    public int SchemaVersion { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    public string Host { get; set; } = string.Empty;

    public ServerWatchdogStatus? Watchdog { get; set; }

    public List<ServerInstanceStatus> Instances { get; set; } = [];
}

public sealed class ServerWatchdogStatus
{
    public int ProcessId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public int? UptimeSeconds { get; set; }

    public int CheckIntervalSeconds { get; set; }
}

public sealed class ServerInstanceStatus
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    public int Port { get; set; }

    public bool Maintenance { get; set; }

    public bool? Running { get; set; }

    public int? Pid { get; set; }

    public bool? PortListening { get; set; }

    public int? DuplicateProcesses { get; set; }

    public DateTimeOffset? LastRestartAt { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? LastError { get; set; }
}
