using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed class ApplicationViewModel : ObservableObject
{
    private const string DefaultServerSshTarget = "crossing-server";

    private const string DefaultServerStatusUrl =
        "https://www.crossingvoid.top/status/ca4bc259f3a20d66a1b87b8eef2021f2.json";

    private readonly AppSettings _settings;
    private string _currentPageTag;

    public ApplicationViewModel(
        AppSettings settings,
        AppSettingsService settingsService,
        LogService? logService = null,
        AxTaskRunner? taskRunner = null,
        TaskHistoryService? taskHistoryService = null,
        RunnerDiagnosticsViewModel? runnerDiagnostics = null,
        string? axToolsPathDetectionStatus = null,
        IReadOnlyDictionary<ManagedToolKey, string>? pathDetectionStatuses = null,
        IReadOnlyList<ToolchainStatusItem>? toolchainStatuses = null,
        string? environmentChangeSummary = null,
        string? scriptsRoot = null,
        ServerStatusService? serverStatusService = null,
        ServerGitUpdateService? serverGitUpdateService = null)
    {
        _settings = settings;
        TaskRunner = taskRunner;
        RunnerDiagnostics = runnerDiagnostics ?? new RunnerDiagnosticsViewModel(
            null,
            "任务运行器尚未初始化。");
        var adapters = ManagedToolAdapterCatalog
            .Create(
                scriptsRoot ?? Path.Combine(AppContext.BaseDirectory, "Scripts"),
                settings.Toolchains)
            .ToDictionary(adapter => adapter.Key);
        foreach (var descriptor in ManagedToolCatalog.All)
        {
            settings.ManagedTools.TryAdd(descriptor.StableKey, new ManagedToolPaths());
        }
        var publishedVersionService = new PublishedVersionService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        // 三个游戏（零境 / 幻杀 / 火影）各一份游戏发布状态：版本号、Release 介绍、
        // 以及"线上最新版本"的检查结果；每个游戏内部 PC 与安卓共用这一份。
        var gamePublishStates = ManagedToolCatalog.All
            .Where(item => IsGamePublishTool(item.Key))
            .ToDictionary(
                item => item.Key,
                item =>
                {
                    var gamePaths = settings.ManagedTools[item.StableKey];
                    return new CrossingVoidGamePublishState(gamePaths, gamePaths);
                });
        // 每个游戏包发布页和它对应的 PC/安卓碎片页共用同一份 ManagedToolPaths：
        // 零境 PC/安卓读 CrossingVoidGame，幻杀 PC/安卓读 FantasyGame。
        // 这样碎片从哪个目录生成、用哪个版本，不需要在页面里再填一遍。
        var gamePublishPathsByToolKey = new Dictionary<ManagedToolKey, ManagedToolPaths>();
        foreach (var profile in GameProfileCatalog.All)
        {
            var gameDescriptor = ManagedToolCatalog.All.Single(item => item.Key == profile.GameToolKey);
            var gamePaths = settings.ManagedTools[gameDescriptor.StableKey];
            gamePublishPathsByToolKey[profile.PcToolKey] = gamePaths;
            gamePublishPathsByToolKey[profile.AndroidToolKey] = gamePaths;
        }

        Tools = ManagedToolCatalog.All
            .Select(descriptor => new ToolPageViewModel(
                descriptor,
                settings.ManagedTools[descriptor.StableKey],
                adapters[descriptor.Key],
                pathDetectionStatuses?.GetValueOrDefault(descriptor.Key) ??
                    (descriptor.Key == ManagedToolKey.AxTools
                        ? axToolsPathDetectionStatus
                        : null),
                isRunnerAvailable: TaskRunner is not null,
                publishedVersionService: publishedVersionService,
                gamePublishState: gamePublishStates.GetValueOrDefault(descriptor.Key),
                gamePublishPaths: gamePublishPathsByToolKey.GetValueOrDefault(descriptor.Key)))
            .ToArray();
        CrossingVoidPackage = new CrossingVoidPackageViewModel(
            settings.CrossingVoidPackage,
            new CrossingVoidPackageInspector(),
            scriptsRoot ?? Path.Combine(AppContext.BaseDirectory, "Scripts"),
            isRunnerAvailable: TaskRunner is not null);
        Settings = new SettingsViewModel(
            settings,
            settingsService,
            Tools,
            RunnerDiagnostics,
            taskHistoryService: taskHistoryService,
            toolchainStatuses: toolchainStatuses,
            environmentChangeSummary: environmentChangeSummary);
        GlobalProgress = new GlobalProgressViewModel();
        LogService = logService ?? new LogService(
            () => DateTime.Now,
            Settings.ShouldWriteLog,
            () => new LogFileOptions(
                Settings.LogSaveToFileEnabled,
                Settings.ProjectRootPath));
        Server = serverStatusService is null
            ? null
            : new ServerPageViewModel(
                serverStatusService,
                DefaultServerSshTarget,
                DefaultServerStatusUrl,
                LogService,
                serverGitUpdateService,
                GlobalProgress,
                settings,          // 《导入新构建》按程序记住上次选的目录
                settingsService);
        TaskHistoryService = taskHistoryService;
        _currentPageTag = NormalizePageTag(settings.LastPageTag);
        _settings.LastPageTag = _currentPageTag;
    }

    public IReadOnlyList<ToolPageViewModel> Tools { get; }

    public CrossingVoidPackageViewModel CrossingVoidPackage { get; }

    public ServerPageViewModel? Server { get; }

    public SettingsViewModel Settings { get; }

    public GlobalProgressViewModel GlobalProgress { get; }

    public RunnerDiagnosticsViewModel RunnerDiagnostics { get; }

    public AxTaskRunner? TaskRunner { get; }

    public TaskHistoryService? TaskHistoryService { get; }

    public LogService LogService { get; }

    public ObservableCollection<LogEntry> Logs => LogService.Entries;

    public string CurrentPageTag
    {
        get => _currentPageTag;
        set
        {
            var normalized = NormalizePageTag(value);
            if (SetProperty(ref _currentPageTag, normalized))
            {
                _settings.LastPageTag = normalized;
            }
        }
    }

    private static string NormalizePageTag(string? pageTag)
    {
        if (string.Equals(pageTag, "Settings", StringComparison.Ordinal) ||
            string.Equals(pageTag, "ServerAliyun", StringComparison.Ordinal) ||
            ManagedToolCatalog.All.Any(tool => string.Equals(
                tool.NavigationTag,
                pageTag,
                StringComparison.Ordinal)))
        {
            return pageTag!;
        }

        return "AxTools";
    }

    /// <summary>游戏包发布工具：三个游戏本体（它们才有「发布新版本」这类动作）。</summary>
    private static bool IsGamePublishTool(ManagedToolKey key) =>
        key is ManagedToolKey.CrossingVoidGame or ManagedToolKey.FantasyGame or ManagedToolKey.NarutoGame;

}
