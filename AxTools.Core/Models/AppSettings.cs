using AxTools.Core.Catalog;

namespace AxTools.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 9;

    public string ProjectRootPath { get; set; } = string.Empty;

    public ThemeMode ThemeMode { get; set; } = ThemeMode.Light;

    public bool ShowWorkspacePath { get; set; }

    public bool LogEnabled { get; set; }

    public bool LogSaveToFileEnabled { get; set; }

    public bool LogUserOperations { get; set; } = true;

    public bool LogWarnings { get; set; } = true;

    public bool LogErrors { get; set; } = true;

    public string LastPageTag { get; set; } = "AxTools";

    public string AxToolsReleaseNotes { get; set; } = string.Empty;

    public UpdateSource UpdateSource { get; set; } = UpdateSource.GitHub;

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    public bool UpdateAutoCheckEnabled { get; set; } = true;

    public bool UpdateCheckOnStartup { get; set; } = true;

    public int UpdateConnectionTimeoutSeconds { get; set; } = 120;

    public DateTimeOffset? UpdateLastCheckAt { get; set; }

    public string UpdateLastStatus { get; set; } = string.Empty;

    public Dictionary<string, ManagedToolPaths> ManagedTools { get; set; } = CreateManagedTools();

    public ToolchainSettings Toolchains { get; set; } = new();

    public CrossingVoidPackageSettings CrossingVoidPackage { get; set; } = new();

    /// <summary>
    /// 服务端《导入新构建》按程序记住的源目录（key = ServerProgramCatalog 的 Key）。
    /// 和 ManagedTools 一个路子：设置里只放路径，不放命令。
    /// </summary>
    public Dictionary<string, string> ServerImportSources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 本机服务端仓库根目录（三个程序的裸仓库都放在它下面）。
    /// 留空时用 ServerProgramCatalog.DefaultLocalRoot，换盘或换目录时只改这一处。
    /// </summary>
    public string ServerLocalRepoRoot { get; set; } = string.Empty;

    public static Dictionary<string, ManagedToolPaths> CreateManagedTools() =>
        ManagedToolCatalog.All.ToDictionary(
            tool => tool.StableKey,
            _ => new ManagedToolPaths(),
            StringComparer.Ordinal);
}
