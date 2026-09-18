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
        var gamePaths = settings.ManagedTools.TryGetValue("CrossingVoidGame", out var configuredGamePaths)
            ? configuredGamePaths
            : new ManagedToolPaths();
        settings.ManagedTools["CrossingVoidGame"] = gamePaths;
        var sharedGamePublishState = new CrossingVoidGamePublishState(gamePaths, gamePaths);
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
                sharedGamePublishState: descriptor.Key is ManagedToolKey.CrossingVoidGame
                        ? sharedGamePublishState
                        : null))
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
                GlobalProgress);
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

}
