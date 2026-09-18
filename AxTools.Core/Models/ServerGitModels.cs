namespace AxTools.Core.Models;

/// <summary>一个可独立更新的服务端程序（零境交错 / 火影BP / 幻杀）。</summary>
public sealed record ServerProgramProfile(
    string Key,
    string DisplayName,
    string LocalFolderName,
    string RepositoryName,
    IReadOnlyList<string> InstanceNames,
    string UpdateNote)
{
    public string DefaultCommitSubject(string timestamp) =>
        $"{DisplayName} 服务端 {timestamp} · 导入新构建";
}

public sealed class ServerGitCommitInfo
{
    public string Sha { get; set; } = string.Empty;

    public string Short { get; set; } = string.Empty;

    public DateTimeOffset? Date { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Describe()
    {
        if (string.IsNullOrWhiteSpace(Short))
        {
            return "（无）";
        }

        var when = Date is { } value ? value.LocalDateTime.ToString("MM-dd HH:mm") : "未知时间";
        return $"{Short} · {when} · {Subject}";
    }
}

public sealed class ServerGitRemoteStatus
{
    public string Program { get; set; } = string.Empty;

    public bool Initialized { get; set; }

    public string Repo { get; set; } = string.Empty;

    public string Worktree { get; set; } = string.Empty;

    public ServerGitCommitInfo? Main { get; set; }

    public ServerGitCommitInfo? Deployed { get; set; }

    public ServerGitCommitInfo? Previous { get; set; }

    public bool PendingCommit { get; set; }

    public bool CanSwitchLatest { get; set; }

    public bool RollbackPossible { get; set; }

    public string? RollbackTarget { get; set; }

    public string? LatestTarget { get; set; }

    public long RepoBytes { get; set; }

    public List<ServerGitInstanceStatus> Instances { get; set; } = [];
}

public sealed class ServerGitInstanceStatus
{
    public string Name { get; set; } = string.Empty;

    public bool Running { get; set; }

    public int? Pid { get; set; }

    public int Port { get; set; }

    public bool Listening { get; set; }
}

public sealed class ServerGitLocalStatus
{
    public string Branch { get; set; } = string.Empty;

    public ServerGitCommitInfo? Head { get; set; }

    public string Upstream { get; set; } = string.Empty;

    public int Ahead { get; set; }

    public int Behind { get; set; }

    public int DirtyCount { get; set; }

    public List<string> DirtyEntries { get; set; } = [];
}

public sealed class ServerGitSyncSummary
{
    public int AddedFiles { get; set; }

    public int UpdatedFiles { get; set; }

    public int RemovedFiles { get; set; }

    public long RemovedBytes { get; set; }
}

public sealed class ServerGitChangeEntry
{
    public string Kind { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long Bytes { get; set; }
}

public sealed class ServerGitImportResult
{
    public ServerGitSyncSummary? Sync { get; set; }

    public List<ServerGitChangeEntry> Changes { get; set; } = [];
}

/// <summary>一个程序在 AxTools 里的完整更新状态。</summary>
public sealed class ServerProgramUpdateState
{
    public required ServerProgramProfile Profile { get; init; }

    public ServerGitRemoteStatus? Remote { get; set; }

    public ServerGitLocalStatus? Local { get; set; }

    public string? Error { get; set; }

    public string LocalRepoPath { get; set; } = string.Empty;

    public string WorkTreePath => Remote?.Worktree ?? string.Empty;

    public bool Ready => Remote is { Initialized: true } && string.IsNullOrWhiteSpace(Error);

    public string DeployedText => Remote?.Deployed?.Describe() ?? "尚未初始化";

    public string PreviousText => Remote?.Previous?.Describe()
        ?? (Remote?.RollbackPossible == true ? "（按上一提交回退）" : "没有可回退的版本");

    public string LocalAheadText => Local is null
        ? "本机仓库状态未知"
        : Local.Ahead > 0
            ? $"本地领先 {Local.Ahead} 个提交，等待推送"
            : Local.DirtyCount > 0
                ? $"本地有 {Local.DirtyCount} 处未提交改动"
                : "本机仓库与服务器一致";

    public string PendingText => Remote?.PendingCommit == true
        ? "服务器已收到新版本，可切换到最新版"
        : "服务器无待应用版本";

    /// <summary>true 表示可以切到最新推送的那一版（未部署或已回退）。</summary>
    public bool CanSwitchLatest => Remote?.CanSwitchLatest == true;

    /// <summary>true 表示可以退到上一个版本；两个按键是互斥的切换开关。</summary>
    public bool CanRollback => Remote?.RollbackPossible == true;

    public string InstancesText
    {
        get
        {
            if (Remote is null || Remote.Instances.Count == 0)
            {
                return "实例状态未知";
            }

            var running = Remote.Instances.Count(item => item.Running && item.Listening);
            return $"{running}/{Remote.Instances.Count} 个实例运行中";
        }
    }
}
