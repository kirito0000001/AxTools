using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.Models;

public sealed class ToolchainSettings : ObservableObject
{
    private string _powerShellPath = string.Empty;
    private string _dotNetPath = string.Empty;
    private string _nodePath = string.Empty;
    private string _npmPath = string.Empty;
    private string _cargoPath = string.Empty;
    private string _javaHome = string.Empty;
    private string _androidJavaHome = string.Empty;
    private string _androidSdkRoot = string.Empty;
    private string _bandizipPath = string.Empty;
    private string _visualStudioRoot = string.Empty;
    private string _msvcVersion = string.Empty;

    public string PowerShellPath { get => _powerShellPath; set => SetProperty(ref _powerShellPath, value ?? string.Empty); }

    public string DotNetPath { get => _dotNetPath; set => SetProperty(ref _dotNetPath, value ?? string.Empty); }

    public string NodePath { get => _nodePath; set => SetProperty(ref _nodePath, value ?? string.Empty); }

    public string NpmPath { get => _npmPath; set => SetProperty(ref _npmPath, value ?? string.Empty); }

    public string CargoPath { get => _cargoPath; set => SetProperty(ref _cargoPath, value ?? string.Empty); }

    public string JavaHome { get => _javaHome; set => SetProperty(ref _javaHome, value ?? string.Empty); }

    public string AndroidJavaHome { get => _androidJavaHome; set => SetProperty(ref _androidJavaHome, value ?? string.Empty); }

    public string AndroidSdkRoot { get => _androidSdkRoot; set => SetProperty(ref _androidSdkRoot, value ?? string.Empty); }

    public string BandizipPath { get => _bandizipPath; set => SetProperty(ref _bandizipPath, value ?? string.Empty); }

    public string VisualStudioRoot { get => _visualStudioRoot; set => SetProperty(ref _visualStudioRoot, value ?? string.Empty); }

    public string MsvcVersion { get => _msvcVersion; set => SetProperty(ref _msvcVersion, value ?? string.Empty); }
}

public sealed record ToolchainStatusItem(
    string Key,
    string DisplayName,
    string Path,
    bool IsAvailable,
    string Message,
    string Description = "",
    string CurrentVersion = "",
    string RecommendedVersion = "",
    EnvironmentHealthState Health = EnvironmentHealthState.Unknown,
    IReadOnlyList<EnvironmentConsumerInfo>? Consumers = null,
    IReadOnlyList<string>? EvidencePaths = null,
    EnvironmentManagementMode ManagementMode = EnvironmentManagementMode.ReadOnly,
    string ManagementHint = "",
    DateTimeOffset? LastModifiedAt = null,
    bool IsNew = false,
    string ChangeSummary = "")
{
    public string HealthText => Health switch
    {
        EnvironmentHealthState.Recommended => "推荐版本",
        EnvironmentHealthState.Compatible => "兼容可用",
        EnvironmentHealthState.Warning => "需要注意",
        EnvironmentHealthState.Missing => "缺失",
        EnvironmentHealthState.Unused => "未发现使用者",
        _ => "尚未判断"
    };

    public string CurrentVersionDisplay => string.IsNullOrWhiteSpace(CurrentVersion)
        ? "未检测到版本"
        : CurrentVersion;

    public string RecommendedVersionDisplay => string.IsNullOrWhiteSpace(RecommendedVersion)
        ? "没有固定推荐版本"
        : RecommendedVersion;

    public string ConsumersText => Consumers is { Count: > 0 }
        ? string.Join("、", Consumers.Select(item => item.Name).Distinct(StringComparer.Ordinal))
        : "未发现使用程序";

    public string ConsumerDetailsText => Consumers is { Count: > 0 }
        ? string.Join(Environment.NewLine, Consumers.Select(item =>
            $"{item.Name} | {item.Requirement} | {item.UsageStatus}{Environment.NewLine}依据：{item.EvidencePath}"))
        : "未发现使用程序或项目。";

    public string EvidenceText => EvidencePaths is { Count: > 0 }
        ? string.Join(Environment.NewLine, EvidencePaths)
        : "没有配置或日志证据";

    public string ManagementText => ManagementMode switch
    {
        EnvironmentManagementMode.CacheCleanable => "可由 AxTools 清理缓存",
        EnvironmentManagementMode.OfficialManager => "通过官方管理器维护",
        _ => "只读检测"
    };

    public string LastModifiedText => LastModifiedAt is null
        ? "未读取到修改时间"
        : $"最近修改：{LastModifiedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";

    public string ChangeSummaryDisplay => string.IsNullOrWhiteSpace(ChangeSummary)
        ? "与上次扫描一致"
        : ChangeSummary;
}

public enum EnvironmentHealthState
{
    Unknown,
    Recommended,
    Compatible,
    Warning,
    Missing,
    Unused
}

public enum EnvironmentManagementMode
{
    ReadOnly,
    CacheCleanable,
    OfficialManager
}

public sealed record EnvironmentConsumerInfo(
    string Name,
    string Requirement,
    string UsageStatus,
    string EvidencePath);

public sealed record EnvironmentInventoryPaths(
    string UnrealEngineRoot,
    string CrossingVoidRoot,
    string FantasyProjectRoot,
    string VisualStudio2022Root,
    string VisualStudio2022Version,
    string VisualStudio2026Root,
    string VisualStudio2026Version,
    string WindowsKitsRoot,
    string AndroidStudioRoot,
    string AndroidSdkRoot,
    string DotNetRoot,
    string GlobalJavaHome)
{
    public static EnvironmentInventoryPaths CreateDefault(ToolchainSettings toolchains) => new(
        UnrealEngineRoot: @"D:\YuanMa\UnrealEngine-release",
        CrossingVoidRoot: @"D:\UnrealMap\CrossingVoid",
        FantasyProjectRoot: @"D:\UnrealMap\FantasyProject",
        VisualStudio2022Root: @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools",
        VisualStudio2022Version: "17.14.37",
        VisualStudio2026Root: @"C:\Program Files\Microsoft Visual Studio\18\Community",
        VisualStudio2026Version: "18.8.2",
        WindowsKitsRoot: @"C:\Program Files (x86)\Windows Kits\10",
        AndroidStudioRoot: @"C:\Program Files\Android\Android Studio",
        AndroidSdkRoot: toolchains.AndroidSdkRoot,
        DotNetRoot: string.IsNullOrWhiteSpace(toolchains.DotNetPath)
            ? @"C:\Program Files\dotnet"
            : Directory.GetParent(toolchains.DotNetPath)?.FullName ?? @"C:\Program Files\dotnet",
        GlobalJavaHome: Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty);
}

public sealed record ToolchainDetectionResult(
    bool Changed,
    IReadOnlyList<ToolchainStatusItem> Items);

public sealed record ToolchainCandidateRoots(
    string JavaRoot,
    string AndroidSdkRoot,
    string AndroidJavaRoot = "",
    IReadOnlyList<string>? VisualStudioRoots = null);
