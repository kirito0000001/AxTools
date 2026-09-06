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

    public static Dictionary<string, ManagedToolPaths> CreateManagedTools() =>
        ManagedToolCatalog.All.ToDictionary(
            tool => tool.StableKey,
            _ => new ManagedToolPaths(),
            StringComparer.Ordinal);
}
