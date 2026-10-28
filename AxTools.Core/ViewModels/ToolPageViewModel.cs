using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed partial class ToolPageViewModel : ObservableObject
{
    private readonly ManagedToolPaths _paths;
    private readonly ManagedToolPaths? _gamePublishPaths;
    private readonly IReadOnlyList<FragmentGameOption> _fragmentGameOptions;
    private readonly IManagedToolAdapter _adapter;
    private readonly bool _isRunnerAvailable;
    private readonly Func<bool> _pendingPackageAvailable;
    private readonly PublishedVersionService _publishedVersionService;
    private readonly CrossingVoidGamePublishState? _gamePublishState;
    private readonly GameWorkbenchProfile? _gameWorkbench;
    private bool _hasAndroidLauncher;
    private bool _hasAndroidGameBuild;
    private readonly GlobalProgressViewModel? _globalProgress;
    private readonly AppSettings? _appSettings;
    private readonly GameProjectGitService _gitService = new();
    private readonly RiderLauncher _riderLauncher = new();
    private bool _isRunning;
    private string _lastResultText = "尚未执行任务。";
    private string _launcherPublishedVersionText = "尚未检查线上发布版本。";
    private string _launcherVersionCheckStatus = "进入发布页后将自动检查。";
    private bool _isLauncherVersionChecking;
    private readonly SemaphoreSlim _launcherVersionCheckLock = new(1, 1);
    private bool _launcherVersionCheckSucceeded;
    private string? _launcherPublishedVersion;
    private readonly ManagedPublishVersionPersistenceService _versionPersistence = new();

    public ToolPageViewModel(
        ManagedToolDescriptor descriptor,
        ManagedToolPaths paths,
        IManagedToolAdapter adapter,
        string? pathDetectionStatus = null,
        bool isRunnerAvailable = true,
        Func<bool>? pendingPackageAvailable = null,
        PublishedVersionService? publishedVersionService = null,
        CrossingVoidGamePublishState? gamePublishState = null,
        ManagedToolPaths? gamePublishPaths = null,
        IReadOnlyList<FragmentGameOption>? fragmentGameOptions = null,
        GlobalProgressViewModel? globalProgress = null,
        AppSettings? appSettings = null)
    {
        Descriptor = descriptor;
        _paths = paths;
        _gamePublishPaths = gamePublishPaths;
        _fragmentGameOptions = fragmentGameOptions ?? Array.Empty<FragmentGameOption>();
        _selectedFragmentGame = _fragmentGameOptions.Count > 0 ? _fragmentGameOptions[0] : null;
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _gameWorkbench = GameWorkbenchCatalog.Find(descriptor.Key);
        _globalProgress = globalProgress;
        _appSettings = appSettings;
        _hasAndroidLauncher = paths.HasAndroidLauncher
            ?? _gameWorkbench?.HasAndroidLauncherByDefault
            ?? false;
        _hasAndroidGameBuild = paths.HasAndroidGameBuild
            ?? _gameWorkbench?.HasAndroidGameBuildByDefault
            ?? false;
        _isRunnerAvailable = isRunnerAvailable;
        _publishedVersionService = publishedVersionService ?? new PublishedVersionService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        _gamePublishState = gamePublishState;
        _pendingPackageAvailable = pendingPackageAvailable ?? (() =>
            Descriptor.Key == ManagedToolKey.CrossingVoidZDTool &&
            new PendingPackageService().IsValid(
                Descriptor.StableKey,
                _paths.OutputRoot,
                out _));
        if (adapter.Key != descriptor.Key)
        {
            throw new ArgumentException("工具描述符与适配器不匹配。", nameof(adapter));
        }

        PathDetectionStatus = pathDetectionStatus ?? string.Empty;
        AllActions = adapter.Actions
            .Select(definition => new ToolActionItemViewModel(definition))
            .ToArray();
        DevelopmentActions = FilterActions(ManagedToolActionSection.Development);
        SourceDownloadAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.DownloadSource,
            "从 GitHub 下载源码",
            ManagedToolActionSection.Development,
            "\uE896",
            true));
        DevelopmentPanelActions = [SourceDownloadAction, .. DevelopmentActions];
        PublishActions = FilterActions(ManagedToolActionSection.Publish);
        var launcherPublishActions = PublishActions
            .Where(item => !IsGamePublishingAction(item.Action))
            .ToArray();
        var gamePublishActions = PublishActions
            .Where(item => IsGamePublishingAction(item.Action))
            .ToArray();
        // 日常入口：「只检查」+「发布到服务器」+「发布到 GitHub」；「重整仓库」是维护动作。
        var gameMaintenanceActions = gamePublishActions
            .Where(item => IsGameMaintenanceAction(item.Action))
            .ToArray();
        var gamePrimaryActions = gamePublishActions
            .Where(item => !IsGameMaintenanceAction(item.Action))
            .ToArray();
        LauncherReleaseNotesAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.EditLauncherReleaseNotes,
            "编辑 Release 介绍",
            ManagedToolActionSection.Publish,
            "\uE70F",
            false));
        GameReleaseNotesAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.EditGameReleaseNotes,
            "编辑 Release 介绍",
            ManagedToolActionSection.Publish,
            "\uE70F",
            false));
        LauncherPublishActions = launcherPublishActions.Length == 0
            ? []
            : [LauncherReleaseNotesAction, .. launcherPublishActions];
        GamePublishActions = gamePrimaryActions.Length == 0
            ? []
            : [GameReleaseNotesAction, .. gamePrimaryActions];
        GameMaintenanceActions = gameMaintenanceActions;
        if (_gamePublishState is not null)
        {
            _gamePublishState.PropertyChanged += SharedGamePublishState_PropertyChanged;
        }
        if (!string.IsNullOrWhiteSpace(PublishVersion))
        {
            _versionPersistence.Persist(Descriptor.Key, SourceRoot, PublishVersion);
        }
        RefreshActionAvailability();
        GameProjects = BuildGameProjects();
    }

    public ManagedToolDescriptor Descriptor { get; }

    public string StableKey => Descriptor.StableKey;

    public string DisplayName => Descriptor.DisplayName;

    public string NavigationTag => Descriptor.NavigationTag;

    public string Description => Descriptor.Description;

    public string PathDetectionStatus { get; }

    public bool HasPathDetectionStatus =>
        !string.IsNullOrWhiteSpace(PathDetectionStatus);

    public IReadOnlyList<ToolActionItemViewModel> AllActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> DevelopmentActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> DevelopmentPanelActions { get; }

    public ToolActionItemViewModel SourceDownloadAction { get; }

    public string GiteeReleasesUrl =>
        ManagedRepositoryCatalog.Get(Descriptor.Key).GiteeReleasesUrl;

    public string DevelopmentVersionText =>
        $"开发版版本：{ExecutableVersionReader.Read(DevelopmentExecutable)}";

    public IReadOnlyList<ToolActionItemViewModel> PublishActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> LauncherPublishActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> GamePublishActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> GameMaintenanceActions { get; }

    public bool HasGameMaintenanceActions => GameMaintenanceActions.Count > 0;

    public ToolActionItemViewModel LauncherReleaseNotesAction { get; }

    public ToolActionItemViewModel GameReleaseNotesAction { get; }

    public bool HasPublishActions => PublishActions.Count > 0;

    public bool HasLauncherPublishActions => LauncherPublishActions.Count > 0;

    /// <summary>这个工具是不是"游戏包发布"工具（由动作集合决定，三个游戏共用同一套）。</summary>
    public bool HasGamePackagePublishing =>
        PublishActions.Any(item => IsGamePublishingAction(item.Action));

    public string GamePackageLabel => "游戏包目录（PC / 安卓各记一个）";

    public string GamePublishingTitle => $"{DisplayName} · 游戏包发布";

    public string GamePublishingDescription =>
        "把选定的产物目录生成文件级清单，按需要发到下载站或 GitHub Release；" +
        "两个目标互相独立，PC 与安卓各发各的。";

    public string GameReleaseDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(GamePublishReleaseNotes))
            {
                return GamePublishReleaseNotes;
            }
            var version = string.IsNullOrWhiteSpace(GamePublishVersion)
                ? "V待填写版本"
                : GamePublishVersion.StartsWith("V", StringComparison.OrdinalIgnoreCase)
                    ? GamePublishVersion
                    : $"V{GamePublishVersion}";
            return $"## {GameProductName} {version}\n\n平台：PC / Android\n\n" +
                "本版本包含完整游戏资源分片，可由启动器自动下载、校验和合并。";
        }
        set => GamePublishReleaseNotes = value;
    }

    /// <summary>
    /// 这个游戏在 Release 介绍里用的名字（零境交错 / 幻杀 / 火影BP）。
    /// 目录里的显示名带「游戏-」前缀，那是导航分类用的，写进更新日志里太啰嗦。
    /// </summary>
    private string GameProductName
    {
        get
        {
            const string prefix = "游戏-";
            var name = Descriptor.DisplayName ?? string.Empty;
            return name.StartsWith(prefix, StringComparison.Ordinal)
                ? name[prefix.Length..]
                : name;
        }
    }

    public bool IsStorageAvailable => false;

    public string StorageAvailabilityText =>
        "存储扫描与安全清理将在第四阶段接入。";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                OnPropertyChanged(nameof(CanGenerateFragments));
            }
        }
    }

    public string PublishVersion
    {
        get => _paths.PublishVersion;
        set
        {
            if (SetProperty(
                _paths.PublishVersion,
                value,
                _paths,
                static (model, newValue) => model.PublishVersion = newValue))
            {
                _versionPersistence.Persist(Descriptor.Key, SourceRoot, value);
                OnPropertyChanged(nameof(LauncherVersionStateText));
                OnPropertyChanged(nameof(PublishVersionMajor));
                OnPropertyChanged(nameof(PublishVersionFeature));
                OnPropertyChanged(nameof(PublishVersionBugFix));
                RefreshActionAvailability();
            }
        }
    }

    public double PublishVersionMajor
    {
        get => ThreePartVersion.ParseOrDefault(PublishVersion).Major;
        set => PublishVersion = ThreePartVersion.ParseOrDefault(PublishVersion).WithMajor(value).ToString();
    }

    public double PublishVersionFeature
    {
        get => ThreePartVersion.ParseOrDefault(PublishVersion).Feature;
        set => PublishVersion = ThreePartVersion.ParseOrDefault(PublishVersion).WithFeature(value).ToString();
    }

    public double PublishVersionBugFix
    {
        get => ThreePartVersion.ParseOrDefault(PublishVersion).BugFix;
        set => PublishVersion = ThreePartVersion.ParseOrDefault(PublishVersion).WithBugFix(value).ToString();
    }

    public string LauncherVersionStateText => string.IsNullOrWhiteSpace(PublishVersion)
        ? "当前尚未设置版本。"
        : $"当前版本设置：{PublishVersion.Trim()}";

    public string PublishChannel
    {
        get => _paths.PublishChannel;
        set => SetProperty(
            _paths.PublishChannel,
            value is "stable" or "beta" ? value : "stable",
            _paths,
            static (model, newValue) => model.PublishChannel = newValue);
    }

    public string LauncherReleaseDescription
    {
        get => string.IsNullOrWhiteSpace(_paths.PublishReleaseNotes)
            ? CreateDefaultLauncherReleaseDescription()
            : _paths.PublishReleaseNotes;
        set => SetProperty(
            _paths.PublishReleaseNotes,
            value ?? string.Empty,
            _paths,
            static (model, newValue) => model.PublishReleaseNotes = newValue);
    }

    public string LauncherPublishedVersionText
    {
        get => _launcherPublishedVersionText;
        private set => SetProperty(ref _launcherPublishedVersionText, value);
    }

    public string LauncherVersionCheckStatus
    {
        get => _launcherVersionCheckStatus;
        private set => SetProperty(ref _launcherVersionCheckStatus, value);
    }

    public bool IsLauncherVersionChecking
    {
        get => _isLauncherVersionChecking;
        private set => SetProperty(ref _isLauncherVersionChecking, value);
    }

    public string GamePublishVersion
    {
        get
        {
            // 有插件产物 Version.json 时，版本以它为准；旧设置值只在没有版本文件时兜底。
            var info = GamePackageBuildInfoReader.Read(CurrentGamePackageRoot, out _, PlatformLabel == "安卓" ? "Android" : "Windows");
            if (info is not null)
            {
                return info.Version;
            }

            return _gamePublishState?.Version ?? _paths.GamePublishVersion;
        }
        set
        {
            // 保留 setter 供历史设置/测试走；界面已不再提供手写版本入口。
            if (_gamePublishState is not null)
            {
                _gamePublishState.Version = value;
                return;
            }
            if (SetProperty(
                _paths.GamePublishVersion,
                value,
                _paths,
                static (model, newValue) => model.GamePublishVersion = newValue))
            {
                OnPropertyChanged(nameof(GameReleaseDescription));
                OnPropertyChanged(nameof(GameVersionMajor));
                OnPropertyChanged(nameof(GameVersionFeature));
                OnPropertyChanged(nameof(GameVersionBugFix));
                RefreshActionAvailability();
            }
        }
    }

    public string GamePackageVersionText
    {
        get
        {
            var info = GamePackageBuildInfoReader.Read(CurrentGamePackageRoot, out var error, PlatformLabel == "安卓" ? "Android" : "Windows");
            if (info is null)
            {
                return string.IsNullOrWhiteSpace(error) ? "尚未读取到包内版本。" : error;
            }

            return $"包内版本：{info.Version}（{info.Mode}，基线 {info.ReleaseVersion}）";
        }
    }

    public void RefreshGamePackageBuildInfo()
    {
        OnPropertyChanged(nameof(GamePublishVersion));
        OnPropertyChanged(nameof(GamePackageVersionText));
        OnPropertyChanged(nameof(CanPublishWindowsPatch));
        OnPropertyChanged(nameof(CanPublishFullGamePackage));
        OnPropertyChanged(nameof(GameVersionMajor));
        OnPropertyChanged(nameof(GameVersionFeature));
        OnPropertyChanged(nameof(GameVersionBugFix));
        OnPropertyChanged(nameof(GameReleaseDescription));
        RefreshActionAvailability();
    }

    public bool CanPublishFullGamePackage => !IsPatchGamePackage;

    public bool CanPublishWindowsPatch =>
        !IsAndroidPlatform &&
        GamePackageBuildInfoReader.Read(CurrentGamePackageRoot, out _, "Windows") is { IsPatch: true };

    private bool IsPatchGamePackage =>
        GamePackageBuildInfoReader.Read(CurrentGamePackageRoot, out _, PlatformLabel == "安卓" ? "Android" : "Windows") is { IsPatch: true };

    public double GameVersionMajor
    {
        get => ThreePartVersion.ParseOrDefault(GamePublishVersion).Major;
        set => GamePublishVersion = ThreePartVersion.ParseOrDefault(GamePublishVersion).WithMajor(value).ToString();
    }

    public double GameVersionFeature
    {
        get => ThreePartVersion.ParseOrDefault(GamePublishVersion).Feature;
        set => GamePublishVersion = ThreePartVersion.ParseOrDefault(GamePublishVersion).WithFeature(value).ToString();
    }

    public double GameVersionBugFix
    {
        get => ThreePartVersion.ParseOrDefault(GamePublishVersion).BugFix;
        set => GamePublishVersion = ThreePartVersion.ParseOrDefault(GamePublishVersion).WithBugFix(value).ToString();
    }

    public string GamePublishChannel
    {
        get => _gamePublishState?.Channel ?? _paths.GamePublishChannel;
        set
        {
            if (_gamePublishState is not null)
            {
                _gamePublishState.Channel = value;
                return;
            }
            SetProperty(
                _paths.GamePublishChannel,
                value,
                _paths,
                static (model, newValue) => model.GamePublishChannel = newValue);
        }
    }

    public string GamePublishPlatform
    {
        get => _gamePublishState is null ? _paths.GamePublishPlatform : _paths.GamePublishPlatform;
        set
        {
            if (SetProperty(_paths.GamePublishPlatform, value is "Android" ? "Android" : "Windows", _paths,
                    static (model, newValue) => model.GamePublishPlatform = newValue))
            {
                // 换平台 = 换一整套产物目录和校验结果，整页状态都要跟着重算
                OnPropertyChanged(nameof(GamePackageRoot));
                RefreshFragmentBindings();
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                RefreshGamePackageBuildInfo();
                RefreshActionAvailability();
            }
        }
    }

    public string GamePublishReleaseNotes
    {
        get => _gamePublishState?.ReleaseNotes ?? _paths.GamePublishReleaseNotes;
        set
        {
            if (_gamePublishState is not null)
            {
                _gamePublishState.ReleaseNotes = value;
                return;
            }
            SetProperty(
                _paths.GamePublishReleaseNotes,
                value ?? string.Empty,
                _paths,
                static (model, newValue) => model.GamePublishReleaseNotes = newValue);
        }
    }

    public string GamePublishedVersionText =>
        _gamePublishState?.PublishedVersionText ?? "线上版本检查：这个游戏还没接（发布时会做公网校验）。";

    public string GameVersionCheckStatus =>
        _gamePublishState?.VersionCheckStatus ??
            "下载站上没有历史版本记录；同一个版本号重发会直接覆盖。";

    public bool IsGameVersionChecking => _gamePublishState?.IsChecking == true;

    public string LastResultText
    {
        get => _lastResultText;
        private set => SetProperty(ref _lastResultText, value);
    }

    /// <summary>页面级动作（复制链接、启动编辑器等）的结果写回这里展示。</summary>
    public void ReportActionMessage(string message) => LastResultText = message;

    public string SourceRoot
    {
        get => _paths.SourceRoot;
        set
        {
            if (SetProperty(_paths.SourceRoot, value, _paths, static (model, newValue) => model.SourceRoot = newValue))
            {
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(HasValidSourceRoot));
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                RefreshActionAvailability();
            }
        }
    }

    public string DevelopmentExecutable
    {
        get => _paths.DevelopmentExecutable;
        set
        {
            if (SetProperty(
                _paths.DevelopmentExecutable,
                value,
                _paths,
                static (model, newValue) => model.DevelopmentExecutable = newValue))
            {
                OnPropertyChanged(nameof(DevelopmentVersionText));
            }
        }
    }

    public string ReleaseExecutable
    {
        get => _paths.ReleaseExecutable;
        set => SetProperty(
            _paths.ReleaseExecutable,
            value,
            _paths,
            static (model, newValue) => model.ReleaseExecutable = newValue);
    }

    public string OutputRoot
    {
        get => _paths.OutputRoot;
        set
        {
            if (SetProperty(
                _paths.OutputRoot,
                value,
                _paths,
                static (model, newValue) => model.OutputRoot = newValue))
            {
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                RefreshActionAvailability();
            }
        }
    }

    internal void RefreshWorkspaceOutputRoot()
    {
        OnPropertyChanged(nameof(OutputRoot));
        NotifyConfigurationChanged();
        OnPropertyChanged(nameof(IsExecutionAvailable));
        OnPropertyChanged(nameof(ExecutionAvailabilityText));
        RefreshActionAvailability();
    }

    public string GamePackageRoot
    {
        // PC 和安卓是两个完全不同的产物目录，各记一个；切平台时这一格自动换成对应那份。
        get => IsAndroidPlatform ? _paths.GamePackageRootAndroid : _paths.GamePackageRoot;
        set
        {
            var stored = IsAndroidPlatform ? _paths.GamePackageRootAndroid : _paths.GamePackageRoot;
            if (!string.Equals(stored, value, StringComparison.Ordinal))
            {
                if (IsAndroidPlatform) { _paths.GamePackageRootAndroid = value; }
                else { _paths.GamePackageRoot = value; }
                OnPropertyChanged();
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                RefreshFragmentBindings();
                RefreshGamePackageBuildInfo();
                RefreshActionAvailability();
            }
        }
    }

    private string CurrentGamePackageRoot =>
        IsAndroidPlatform ? _paths.GamePackageRootAndroid : _paths.GamePackageRoot;

    internal bool IsAndroidPlatform => _paths.GamePublishPlatform is "Android";

    internal string PlatformLabel => IsAndroidPlatform ? "安卓" : "PC";

    public bool HasValidSourceRoot =>
        !string.IsNullOrWhiteSpace(SourceRoot) && Directory.Exists(SourceRoot);

    /// <summary>
    /// 顶部「配置状态」通知栏的文案，跟着当前页签走：
    /// 开发版只看开发环境（项目是否都在），打包与发布只看游戏包目录；
    /// 其余页签与非游戏工具仍用适配器的校验结果。
    /// </summary>
    public string ConfigurationStatus => IsGameTool
        ? BuildGameConfigurationReport()
        : _adapter.Validate(_paths).Message;

    /// <summary>当前页签；页面切换时由 ToolPage 调 <see cref="SelectSection"/> 更新。</summary>
    public ToolPageSection CurrentSection { get; private set; } = ToolPageSection.Development;

    /// <summary>切换页签后重算通知栏文案与颜色。</summary>
    public void SelectSection(ToolPageSection section)
    {
        if (CurrentSection == section)
        {
            return;
        }

        CurrentSection = section;
        NotifyConfigurationChanged();
    }

    /// <summary>配置状态和它的颜色档位总是一起变，统一从这里发通知，避免只刷新文字不刷新颜色。</summary>
    private void NotifyConfigurationChanged()
    {
        OnPropertyChanged(nameof(ConfigurationStatus));
        OnPropertyChanged(nameof(ConfigurationSeverity));
        OnPropertyChanged(nameof(PcPublishSummaryText));
        OnPropertyChanged(nameof(AndroidPublishSummaryText));
        // 开发版面板里那行可用性会回落到这份文案；不一起刷新，
        // 切回开发版时还会残留打包页的“缺少游戏包目录”。
        OnPropertyChanged(nameof(ExecutionAvailabilityText));
    }

    /// <summary>
    /// 配置状态通告栏的颜色档位。以前固定 Informational，导致“目录不存在”也显示成绿色提示；
    /// 这里按校验结果给：通过才算 Success，其余一律 Warning。
    /// </summary>
    public OperationNoticeSeverity ConfigurationSeverity =>
        IsGameTool && CurrentSection is ToolPageSection.Settings
            ? OperationNoticeSeverity.Informational
            : IsConfigurationReady ? OperationNoticeSeverity.Success : OperationNoticeSeverity.Warning;

    private bool IsConfigurationReady => IsGameTool
        ? EvaluateGameConfiguration().Ready
        : _adapter.Validate(_paths).IsValid;

    private string BuildGameConfigurationReport() => EvaluateGameConfiguration().Message;

    /// <summary>
    /// 游戏页按当前页签评估：开发版只看项目，打包与发布只看游戏包目录，
    /// 其它页签交回适配器。文案和颜色都从这一个结果来，避免“绿条配警告字”。
    /// </summary>
    private (string Message, bool Ready) EvaluateGameConfiguration()
    {
        if (CurrentSection is ToolPageSection.Development)
        {
            var missingProjects = MissingGameProjectItems();
            return missingProjects.Count == 0
                ? ("开发环境已就绪", true)
                : ($"缺少：{string.Join("；", missingProjects)}", false);
        }

        if (CurrentSection is ToolPageSection.Launcher)
        {
            return HasLauncherPackaging
                ? ("启动器打包就绪", true)
                : ("未启用安卓启动器打包。", true);
        }

        if (CurrentSection is ToolPageSection.Server)
        {
            // 服务器页：只有「推送了但没切」才值得提醒。
            var updateState = _serverProfile is null
                ? null
                : _serverPage?.GetUpdateState(_serverProfile.Key);
            if (updateState?.Remote?.PendingCommit == true)
            {
                return ($"服务器有待应用版本：{updateState.PendingText}；切到最新版本后生效。", false);
            }

            return ("服务端状态正常", true);
        }

        if (CurrentSection is ToolPageSection.Publish)
        {
            return ("发布环境已就绪", true);
        }

        if (CurrentSection is ToolPageSection.Settings)
        {
            // 「设置」页不参与环境校验，通知栏保持中性。
            return ("设置页不参与环境校验。", true);
        }

        var validation = _adapter.Validate(_paths);
        return (validation.Message, validation.IsValid);
    }

    /// <summary>开发版要查的：各个项目的本地路径是否配了、是否还在。</summary>
    private List<string> MissingGameProjectItems()
    {
        var missing = new List<string>();
        foreach (var project in GameProjects)
        {
            if (string.IsNullOrWhiteSpace(project.Path))
            {
                missing.Add($"{project.DisplayName}路径");
            }
            else if (project.StateKey == "missing")
            {
                missing.Add($"{project.DisplayName}（{project.Path} 不存在）");
            }
        }

        return missing;
    }

    /// <summary>这个工具是不是三个游戏之一（决定页面是否走游戏通用面板）。</summary>
    public bool IsGameTool => _gameWorkbench is not null;

    /// <summary>游戏页要展示的项目（本体 + 启动器）；非游戏工具为空。</summary>
    public IReadOnlyList<GameProjectStatus> GameProjects { get; private set; } = [];

    /// <summary>「功能散页」的暂存清单；非游戏工具为空。</summary>
    public IReadOnlyList<GameScratchItem> GameScratchItems =>
        IsGameTool ? GameScratchCatalog.All : [];

    /// <summary>
    /// 这个游戏是否拥有安卓启动器。关掉以后安卓项目不再参与开发环境检查。
    /// 值写回 ManagedToolPaths，随设置一起在窗口关闭时落盘。
    /// </summary>
    public bool HasAndroidLauncher
    {
        get => _hasAndroidLauncher;
        set
        {
            if (_hasAndroidLauncher == value)
            {
                return;
            }

            _hasAndroidLauncher = value;
            _paths.HasAndroidLauncher = value;
            OnPropertyChanged();
            RefreshGameProjects();
        }
    }

    /// <summary>
    /// 这个游戏是否有安卓版本（安卓游戏包）。与「是否有安卓启动器」互相独立：
    /// 有安卓游戏不一定有安卓启动器，反之亦然。
    /// </summary>
    public bool HasAndroidGameBuild
    {
        get => _hasAndroidGameBuild;
        set
        {
            if (_hasAndroidGameBuild == value)
            {
                return;
            }

            _hasAndroidGameBuild = value;
            _paths.HasAndroidGameBuild = value;
            OnPropertyChanged();
            NotifyConfigurationChanged();
        }
    }

    /// <summary>PC 游戏包目录（发布到下载站用的那份产物）。</summary>
    public string PcGamePackageRoot
    {
        get => _paths.GamePackageRoot;
        set
        {
            if (SetProperty(
                    _paths.GamePackageRoot,
                    value,
                    _paths,
                    static (model, newValue) => model.GamePackageRoot = newValue))
            {
                NotifyConfigurationChanged();
            }
        }
    }

    /// <summary>安卓游戏包目录；开关关掉时界面不显示。</summary>
    public string AndroidGamePackageRoot
    {
        get => _paths.GamePackageRootAndroid;
        set
        {
            if (SetProperty(
                    _paths.GamePackageRootAndroid,
                    value,
                    _paths,
                    static (model, newValue) => model.GamePackageRootAndroid = newValue))
            {
                NotifyConfigurationChanged();
            }
        }
    }

    /// <summary>上传渠道：下载站（野草云）。</summary>
    public bool PublishToDownloadSite
    {
        get => _paths.PublishGameToDownloadSite ?? true;
        set
        {
            if (_paths.PublishGameToDownloadSite == value)
            {
                return;
            }

            _paths.PublishGameToDownloadSite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GamePublishPcActions));
            OnPropertyChanged(nameof(GamePublishAndroidActions));
        }
    }

    /// <summary>上传渠道：GitHub Release。</summary>
    public bool PublishToGitHub
    {
        get => _paths.PublishGameToGitHub ?? true;
        set
        {
            if (_paths.PublishGameToGitHub == value)
            {
                return;
            }

            _paths.PublishGameToGitHub = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GamePublishPcActions));
            OnPropertyChanged(nameof(GamePublishAndroidActions));
        }
    }

    /// <summary>给当前动作切平台：PC 卡按钮传 Windows，安卓卡传 Android。</summary>
    public void SetPublishPlatform(string platform)
    {
        var normalized = string.Equals(platform, "Android", StringComparison.Ordinal)
            ? "Android"
            : "Windows";
        if (string.Equals(_paths.GamePublishPlatform, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _paths.GamePublishPlatform = normalized;
        NotifyConfigurationChanged();
    }

    /// <summary>本体打包（PC 卡）的按钮：顺序即界面顺序。</summary>
    public IReadOnlyList<ServerActionCommand> PublishPcCommands { get; } =
    [
        new("check", "体检", "\uE9D5"),
        new("publish", "发布新版本", "\uE72D"),
        new("patch", "发布补丁包", "\uE8B7"),
        new("patch-fragments", "生成补丁碎片", "\uE7B8"),
        new("body-fragments", "生成本体碎片", "\uE898") { IsEnabled = false }
    ];

    /// <summary>本体打包（安卓卡）的按钮：补丁类只支持 PC。</summary>
    public IReadOnlyList<ServerActionCommand> PublishAndroidCommands { get; } =
    [
        new("check", "体检", "\uE9D5"),
        new("publish", "发布新版本", "\uE72D")
    ];

    /// <summary>PC 产物摘要：版本 · 模式 · 文件数 · 总大小。</summary>
    public string PcProductSummary => BuildProductSummary(_paths.GamePackageRoot, "Windows");

    /// <summary>安卓产物摘要。</summary>
    public string AndroidProductSummary => BuildProductSummary(_paths.GamePackageRootAndroid, "Android");

    private static string BuildProductSummary(string? packageRoot, string platform)
    {
        var info = GamePackageBuildInfoReader.Read(packageRoot, out var error, platform);
        if (info is null)
        {
            return $"读取失败：{error}";
        }

        var size = FormatBytes(info.TotalBytes);
        return $"版本 {info.Version} · {info.Kind} · {info.Files.Count} 个文件 · {size}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };

    /// <summary>
    /// 游戏包体检：本地打包戳 ↔ 线上下载站清单，逐文件比对 sha256。
    /// 不重新扫盘（戳里已经有哈希），所以很快；进度仍写全局进度条。
    /// </summary>
    public async Task<GamePackageHealthReport> CheckGamePackageHealthAsync(
        bool android,
        CancellationToken cancellationToken)
    {
        var root = android ? _paths.GamePackageRootAndroid : _paths.GamePackageRoot;
        var info = GamePackageBuildInfoReader.Read(root, out var error, android ? "Android" : "Windows");
        if (info is null)
        {
            return new GamePackageHealthReport(
                false,
                "体检无法开始",
                ["✗ " + (error ?? "读不到产物版本信息。")]);
        }

        var segment = (_adapter as GameUploadAdapter)?.GameProductSegment ?? "crossingvoid";
        if (android)
        {
            segment += "-android";
        }

        var progress = _globalProgress;
        progress?.Start("游戏包体检", $"{DisplayName} · {segment}");
        progress?.SetCancellationMode(AxTaskCancellationMode.Locked, "正在比对线上清单。");
        try
        {
            var report = await GamePackageHealthService.CheckAsync(info, segment, cancellationToken);
            progress?.Complete(
                report.Healthy ? "体检通过" : "体检发现问题",
                report.Summary);
            return report;
        }
        catch (Exception exception)
        {
            return new GamePackageHealthReport(
                false,
                "体检失败",
                ["✗ " + exception.Message]);
        }
        finally
        {
            if (progress is not null)
            {
                await Task.Delay(1200);
                progress.Hide();
            }
        }
    }

    /// <summary>本体打包按钮 → 动作；「生成本体碎片」暂无脚本入口，返回 null。</summary>
    public static ManagedToolAction? ResolvePublishCommand(string key) => key switch
    {
        "check" => ManagedToolAction.CheckGamePackage,
        "publish" => ManagedToolAction.PublishGameVersion,
        "patch" => ManagedToolAction.PublishGamePatch,
        "patch-fragments" => ManagedToolAction.GenerateGamePatchFragments,
        _ => null
    };

    /// <summary>第三片 PC 游戏推送的按钮：按渠道开关过滤。</summary>
    public IReadOnlyList<ToolActionItemViewModel> GamePublishPcActions =>
        IsGameTool ? GamePublishActions.Where(IsPublishChannelEnabled).ToArray() : [];

    /// <summary>第四片安卓游戏推送的按钮：补丁类动作只支持 PC，这里排除。</summary>
    public IReadOnlyList<ToolActionItemViewModel> GamePublishAndroidActions =>
        IsGameTool
            ? GamePublishActions
                .Where(IsPublishChannelEnabled)
                .Where(item => item.Action is not (
                    ManagedToolAction.PublishGamePatch or
                    ManagedToolAction.GenerateGamePatchFragments))
                .ToArray()
            : [];

    private bool IsPublishChannelEnabled(ToolActionItemViewModel item) => item.Action switch
    {
        ManagedToolAction.PublishGameVersion => PublishToDownloadSite,
        ManagedToolAction.PublishGameVersionToGitHub => PublishToGitHub,
        _ => true
    };

    /// <summary>游戏本体的本地路径（设置页可改）。</summary>
    public string ClientProjectPath
    {
        get => EffectiveProjectPath("client");
        set => SetProjectPath("client", value);
    }

    /// <summary>本体项目的仓库地址；空串表示没配。</summary>
    public string ClientRepositoryUrl => RepositoryUrlFor("client");

    /// <summary>按项目 Id 取仓库地址（client / android）。</summary>
    public string RepositoryUrlFor(string projectId) =>
        _gameWorkbench?.Projects.FirstOrDefault(project => project.Id == projectId)?.RepositoryUrl
            ?? string.Empty;

    /// <summary>
    /// 开发版的卡片列表：本体一张，开了「拥有安卓启动器」再加一张。
    /// 关掉开关后 GameProjects 里没有安卓项目，这里自然就不出那张卡。
    /// </summary>
    public IReadOnlyList<GameProjectCard> GameProjectCards =>
        IsGameTool
            ? GameProjects
                .Select(project => new GameProjectCard(
                    project.Id,
                    BuildProjectCardTitle(project),
                    GameDevActionCatalog.ForProject(project.Id)
                        .Select(action => action with { ProjectId = project.Id })
                        .ToArray()))
                .ToArray()
            : [];

    private ToolPageViewModel? _androidLauncherTool;
    private ServerPageViewModel? _serverPage;
    private ServerProgramProfile? _serverProfile;
    private bool _isRefreshingServerState;

    /// <summary>发布区里的「服务端状态」是否可用（游戏页 + 任务运行器可用）。</summary>
    public bool HasServerUpdate =>
        IsGameTool && _serverProfile is not null && _serverPage?.HasUpdateService == true;

    /// <summary>服务端实例（含端口与运行状态），按当前游戏过滤。</summary>
    public IReadOnlyList<ServerInstanceViewModel> ServerInstances { get; private set; } = [];

    public bool HasServerInstances => ServerInstances.Count > 0;

    /// <summary>
    /// 仓库状态三行：当前部署版本 / 上次旧版存留 / 本机仓库。
    /// 没读到数据时给占位文字，保证卡片结构不变——旧版空白最难查就是这个原因。
    /// </summary>
    public IReadOnlyList<ServerUpdateLine> ServerUpdateLines { get; private set; } =
    [
        new ServerUpdateLine("当前部署版本", "尚未读取"),
        new ServerUpdateLine("上次旧版存留", "尚未读取"),
        new ServerUpdateLine("本机仓库", "尚未读取")
    ];

    /// <summary>卡片顶部那句话：没读过、读好了、还是在读，一眼能看出来（旧版空白最难查）。</summary>
    public string ServerStateStatus { get; private set; } =
        "尚未读取服务端状态；进入本分区会自动读取，也可以点「立即刷新」。";

    /// <summary>
    /// 这个游戏对应的安卓启动器工具（只有零境有）。启动器打包分区用的就是它的发布管道。
    /// </summary>
    public void AttachAndroidLauncherTool(ToolPageViewModel? launcherTool)
    {
        _androidLauncherTool = launcherTool;
        OnPropertyChanged(nameof(AndroidLauncherTool));
        OnPropertyChanged(nameof(HasLauncherPackaging));
    }

    public ToolPageViewModel? AndroidLauncherTool
    {
        get => _androidLauncherTool;
        private set
        {
            if (ReferenceEquals(_androidLauncherTool, value))
            {
                return;
            }

            _androidLauncherTool = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLauncherPackaging));
        }
    }

    /// <summary>启动器打包分区是否出现：游戏页、开了安卓启动器、且确实有启动器工具。</summary>
    public bool HasLauncherPackaging =>
        IsGameTool && HasAndroidLauncher && _androidLauncherTool is not null;

    /// <summary>启动器三段版本号代理到安卓启动器工具。</summary>
    public double LauncherVersionMajor
    {
        get => _androidLauncherTool?.PublishVersionMajor ?? 0;
        set => SetLauncherVersion("major", value);
    }

    public double LauncherVersionFeature
    {
        get => _androidLauncherTool?.PublishVersionFeature ?? 0;
        set => SetLauncherVersion("feature", value);
    }

    public double LauncherVersionBugFix
    {
        get => _androidLauncherTool?.PublishVersionBugFix ?? 0;
        set => SetLauncherVersion("bugfix", value);
    }

    private void SetLauncherVersion(string part, double value)
    {
        if (_androidLauncherTool is null)
        {
            return;
        }

        switch (part)
        {
            case "major":
                _androidLauncherTool.PublishVersionMajor = value;
                break;
            case "feature":
                _androidLauncherTool.PublishVersionFeature = value;
                break;
            default:
                _androidLauncherTool.PublishVersionBugFix = value;
                break;
        }

        OnPropertyChanged(nameof(LauncherPackagingStatusText));
    }

    /// <summary>版本状态与线上版本合成一句：线上 x.y.z · 版本校验结果。</summary>
    public string LauncherPackagingStatusText
    {
        get
        {
            if (_androidLauncherTool is null)
            {
                return "还没有可用的安卓启动器工具。";
            }

            var online = _androidLauncherTool.LauncherPublishedVersionText;
            var state = _androidLauncherTool.LauncherVersionStateText;
            return string.IsNullOrWhiteSpace(state) ? online : $"{online} · {state}";
        }
    }

    public async Task RefreshLauncherPackagingAsync(CancellationToken cancellationToken)
    {
        if (_androidLauncherTool is null)
        {
            return;
        }

        await _androidLauncherTool.CheckLauncherPublishedVersionAsync(cancellationToken);
        OnPropertyChanged(nameof(LauncherPackagingStatusText));
    }

    /// <summary>本机服务端仓库根目录（设置页可改）；留空时用默认目录。</summary>
    public string ServerLocalRepoRoot
    {
        get => string.IsNullOrWhiteSpace(_appSettings?.ServerLocalRepoRoot)
            ? ServerProgramCatalog.DefaultLocalRoot
            : _appSettings!.ServerLocalRepoRoot;
        set
        {
            if (_appSettings is null)
            {
                return;
            }

            _appSettings.ServerLocalRepoRoot = value ?? string.Empty;
            OnPropertyChanged();
            // 换目录后旧缓存不成立：清掉，并按新目录重读一次。
            _serverPage?.InvalidateUpdateStates();
            RebuildServerUpdateLines();
            NotifyConfigurationChanged();
        }
    }

    /// <summary>当前游戏对应的服务端程序；非游戏页为空。</summary>
    public ServerProgramProfile? ServerProfile => _serverProfile;

    /// <summary>服务器页 VM（执行服务端操作要靠它的 UpdateService）。</summary>
    public ServerPageViewModel? ServerViewModel => _serverPage;

    /// <summary>服务器级动作（看门狗日志、日志/缓存清理等）；挂上服务器 VM 后填充。</summary>
    public IReadOnlyList<ServerActionCommand> ServerManagementActions { get; private set; } = [];

    /// <summary>服务器页三张卡的标题，跟着游戏名走。</summary>
    public string ServerPushCardTitle => $"{DisplayName}服务端推送";

    public string ServerManagementCardTitle => $"{DisplayName}服务器管理";

    public string GamePublishCardTitle => $"{DisplayName}游戏本体推送";

    /// <summary>打包页签的标题：游戏页是「本体打包」，其它工具仍是「打包与发布」。</summary>
    public string PublishTabTitle => IsGameTool ? "本体打包" : "打包与发布";

    public string GamePublishAndroidCardTitle => $"{DisplayName}安卓游戏推送";

    /// <summary>PC 游戏包的发布摘要；固定按 Windows 算，不受当前操作平台影响。</summary>
    public string PcPublishSummaryText => BuildPublishSummary("Windows");

    /// <summary>安卓游戏包的发布摘要；固定按 Android 算。</summary>
    public string AndroidPublishSummaryText => BuildPublishSummary("Android");

    private string BuildPublishSummary(string platform) =>
        GamePublishRequestBuilder.TryBuild(
            Descriptor.Key,
            _paths,
            ManagedToolAction.PublishGameVersion,
            GamePublishReleaseNotes,
            out var request,
            out var error,
            platform)
            ? request.Summary
            : $"尚未就绪：{error}";

    /// <summary>当前设置能组装出的发布请求；页面门禁与适配器共用这一份规则。</summary>
    public bool TryBuildGamePublishRequest(
        ManagedToolAction action,
        out GamePublishRequest request,
        out string error) =>
        GamePublishRequestBuilder.TryBuild(
            Descriptor.Key,
            _paths,
            action,
            GamePublishReleaseNotes,
            out request,
            out error);

    /// <summary>本次会话是否导入过新构建（推完清掉，决定推送/预览按钮灰不灰）。</summary>
    private bool _serverImportPending;

    /// <summary>页面上的服务端操作执行完后回报一句，用来更新按钮可点状态。</summary>
    public void MarkServerActionCompleted(string actionKey)
    {
        if (string.Equals(actionKey, "import", StringComparison.Ordinal))
        {
            _serverImportPending = true;
        }
        else if (string.Equals(actionKey, "push", StringComparison.Ordinal))
        {
            _serverImportPending = false;
        }

        RebuildServerUpdateLines();
    }

    /// <summary>服务端更新按钮；可点状态跟着刷新结果变。</summary>
    public IReadOnlyList<ServerActionCommand> ServerActions { get; } =
    [
        new("import", "导入新构建", "\uE8E5"),
        new("preview", "查看待更新内容", "\uE8A5") { IsEnabled = false },
        new("push", "推送新版本", "\uE72A") { IsEnabled = false },
        new("apply", "切换最新版本", "\uE72C") { IsEnabled = false },
        new("rollback", "回退上个版本", "\uE7A7") { IsEnabled = false },
        new("recover", "恢复服务端", "\uE777") { IsEnabled = false },
        new("cleanup", "清理本地仓库", "\uE74D") { IsEnabled = false }
    ];

    /// <summary>
    /// 由 ApplicationViewModel 在服务器页 VM 建好之后调用；只有游戏页会匹配到服务端程序。
    /// </summary>
    public void AttachServerUpdates(ServerPageViewModel? server)
    {
        _serverPage = server;
        _serverProfile = Descriptor.Key switch
        {
            ManagedToolKey.CrossingVoidGame => ServerProgramCatalog.Get("crossingvoid"),
            ManagedToolKey.NarutoGame => ServerProgramCatalog.Get("narutobp"),
            ManagedToolKey.FantasyGame => ServerProgramCatalog.Get("fantasyproject"),
            _ => null
        };
        RebuildServerUpdateLines();
        ServerManagementActions = _serverPage?.Actions
            .Select(action => new ServerActionCommand(action.Key, action.DisplayName, action.IconGlyph))
            .ToArray() ?? [];
        OnPropertyChanged(nameof(ServerManagementActions));
        OnPropertyChanged(nameof(HasServerUpdate));
    }

    /// <summary>
    /// 读一次服务端状态：实例与端口 + 仓库更新状态。
    /// 进入「打包与发布」分区时调一次，之后由页面每分钟自动调用。
    /// </summary>
    public async Task RefreshServerStateAsync(CancellationToken cancellationToken)
    {
        if (_serverPage is null || _serverProfile is null || _isRefreshingServerState)
        {
            return;
        }

        _isRefreshingServerState = true;
        ServerStateStatus = "正在刷新服务端状态…";
        OnPropertyChanged(nameof(ServerStateStatus));

        try
        {
            await _serverPage.RefreshAsync(cancellationToken);
            var state = await _serverPage.RefreshUpdateStateAsync(_serverProfile, cancellationToken);

            ServerInstances = _serverPage.InstancesOf(_serverProfile.Key).ToArray();
            OnPropertyChanged(nameof(ServerInstances));
            OnPropertyChanged(nameof(HasServerInstances));
            RebuildServerUpdateLines();

            ServerStateStatus = string.IsNullOrWhiteSpace(state.Error)
                ? $"已更新 · {DateTime.Now:HH:mm:ss}"
                : $"状态读取不完整：{state.Error}";
        }
        catch (Exception exception)
        {
            ServerStateStatus = $"读取失败：{exception.Message}";
        }
        finally
        {
            _isRefreshingServerState = false;
            OnPropertyChanged(nameof(ServerStateStatus));
            // 「有待应用版本」显示在顶部通告栏里，读完要一起刷新。
            NotifyConfigurationChanged();
        }
    }

    private void RebuildServerUpdateLines()
    {
        var state = _serverProfile is null
            ? null
            : _serverPage?.GetUpdateState(_serverProfile.Key);

        ServerUpdateLines =
        [
            new ServerUpdateLine("当前部署版本", state?.DeployedText ?? "尚未读取"),
            new ServerUpdateLine("上次旧版存留", state?.PreviousText ?? "尚未读取"),
            new ServerUpdateLine("本机仓库", state?.LocalAheadText ?? "尚未读取")
        ];
        OnPropertyChanged(nameof(ServerUpdateLines));

        // 按钮可点状态跟旧页面一致：切换看能否前进、回退看能否后退、
        // 恢复要求远端仓库已初始化、清理要求本机仓库存在。
        // 推送与预览：本次会话里导入过、且本机确实领先服务器才亮；推完立刻灰回去。
        var hasUnpushedCommits = _serverImportPending && state?.Local?.Ahead > 0;
        SetServerActionEnabled("preview", hasUnpushedCommits);
        SetServerActionEnabled("push", hasUnpushedCommits);
        SetServerActionEnabled("apply", state?.CanSwitchLatest == true);
        SetServerActionEnabled("rollback", state?.CanRollback == true);
        SetServerActionEnabled("recover", state?.Remote is { Initialized: true });
        SetServerActionEnabled("cleanup", state?.Local is not null);
    }

    private void SetServerActionEnabled(string key, bool enabled)
    {
        var action = ServerActions.FirstOrDefault(item => item.Key == key);
        if (action is not null)
        {
            action.IsEnabled = enabled;
        }
    }

    /// <summary>标题形如「项目本体-CrossingVoid」「安卓启动器-CrossingVoid-Downloader-Android」。</summary>
    private static string BuildProjectCardTitle(GameProjectStatus project)
    {
        var name = project.RepositoryUrl.TrimEnd('/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < name.Length - 1)
        {
            name = name[(lastSlash + 1)..];
        }

        var prefix = string.Equals(project.Id, "android", StringComparison.Ordinal)
            ? "安卓启动器"
            : "项目本体";
        return string.IsNullOrWhiteSpace(name) ? prefix : $"{prefix}-{name}";
    }

    /// <summary>
    /// 在项目根目录找 .uproject 并用系统关联打开。
    /// 找不到文件或启动失败时返回 false，并把原因写进 message。
    /// </summary>
    public bool TryLaunchUProject(string projectId, out string message)
    {
        var root = EffectiveProjectPath(projectId);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            message = "本体路径不存在，先去「设置」里选目录。";
            return false;
        }

        string? uproject;
        try
        {
            uproject = Directory
                .EnumerateFiles(root, "*.uproject", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            message = $"读取项目目录失败：{exception.Message}";
            return false;
        }

        if (uproject is null)
        {
            message = "项目根目录里没有 .uproject 文件。";
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uproject) { UseShellExecute = true });
            message = $"已用 .uproject 启动：{Path.GetFileName(uproject)}";
            return true;
        }
        catch (Exception exception)
        {
            message = $"启动 .uproject 失败：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 从 GitHub 安装本体项目：clone 到目标目录，成功后把设置里的本体路径同步过去。
    /// 目标目录必须不存在或为空，避免覆盖已有工程。
    /// </summary>
    public async Task InstallProjectAsync(
        string projectId,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var repositoryUrl = RepositoryUrlFor(projectId);
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            ReportActionMessage("这个项目的仓库地址还没配置。");
            return;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ReportActionMessage("没有选择安装路径。");
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetPath.Trim());
        }
        catch (Exception exception)
        {
            ReportActionMessage($"安装路径无效：{exception.Message}");
            return;
        }

        if (Directory.Exists(fullPath))
        {
            var hasContent = Directory.EnumerateFileSystemEntries(fullPath).Any();
            if (hasContent)
            {
                ReportActionMessage($"目标目录不是空的，换一个位置：{fullPath}");
                return;
            }
        }

        var succeeded = await RunGitWithProgressAsync(
            "安装项目",
            $"克隆 {repositoryUrl} 到 {fullPath}",
            (progress, token) => _gitService.CloneAsync(
                repositoryUrl,
                fullPath,
                progress,
                token),
            cancellationToken);
        if (succeeded)
        {
            // 装完自动同步设置里的项目路径，省得再手动选一次。
            SetProjectPath(projectId, fullPath);
            ReportActionMessage($"安装完成，项目路径已设为：{fullPath}");
        }
    }

    /// <summary>检查上游并更新：在项目目录里执行 git pull（默认合并）。</summary>
    public async Task PullProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        var root = EffectiveProjectPath(projectId);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ReportActionMessage("项目路径不存在，先去「设置」里选目录。");
            return;
        }

        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            ReportActionMessage("这个目录不是 git 仓库，无法检查上游。");
            return;
        }

        await RunGitWithProgressAsync(
            "检查上游并更新",
            root,
            (progress, token) => _gitService.PullAsync(root, progress, token),
            cancellationToken);
    }

    /// <summary>用 Rider 打开指定项目（只打开，不编译不运行）。</summary>
    public bool TryLaunchRider(string projectId, out string message)
    {
        var opened = _riderLauncher.TryOpenProject(EffectiveProjectPath(projectId), out message);
        ReportActionMessage(message);
        return opened;
    }

    /// <summary>查询目标路径所在磁盘的可用空间，供安装弹窗显示。</summary>
    public static bool TryGetAvailableFreeBytes(string path, out long bytes, out string message)
    {
        bytes = 0;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                message = "路径无效。";
                return false;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                message = "磁盘未就绪。";
                return false;
            }

            bytes = drive.AvailableFreeSpace;
            message = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 统一跑 git 操作：全局进度条显示阶段与输出，结束后在结果行留一句结论。
    /// </summary>
    private async Task<bool> RunGitWithProgressAsync(
        string title,
        string detail,
        Func<IProgress<ProgressUpdate>, CancellationToken, Task<GameProjectGitResult>> operation,
        CancellationToken cancellationToken)
    {
        var progressViewModel = _globalProgress;
        progressViewModel?.Start(title, detail);
        progressViewModel?.SetCancellationMode(
            AxTaskCancellationMode.Locked,
            "执行期间请勿关闭窗口。");

        var progress = new Progress<ProgressUpdate>(update => progressViewModel?.Report(update));
        var result = await operation(progress, cancellationToken);

        progressViewModel?.Complete(
            result.Success ? $"{title}完成" : $"{title}失败",
            result.Message);
        ReportActionMessage(result.Message);

        if (progressViewModel is not null)
        {
            // 让用户看到结果再收起，不长时间占着底部。
            await Task.Delay(2500);
            progressViewModel.Hide();
        }

        return result.Success;
    }

    /// <summary>安卓启动器的本地路径（设置页可改）。</summary>
    public string AndroidLauncherPath
    {
        get => EffectiveProjectPath("android");
        set => SetProjectPath("android", value);
    }

    /// <summary>写入某个项目的本地路径；空串表示清空，之后会按“缺少路径”提示。</summary>
    public void SetProjectPath(string projectId, string path)
    {
        _paths.ProjectPaths[projectId] = path ?? string.Empty;
        OnPropertyChanged(nameof(ClientProjectPath));
        OnPropertyChanged(nameof(AndroidLauncherPath));
        RefreshGameProjects();
    }

    /// <summary>设置里没写过就用目录默认值；写过（哪怕空串）以设置为准。</summary>
    private string EffectiveProjectPath(string projectId) =>
        _paths.ProjectPaths.TryGetValue(projectId, out var stored)
            ? stored
            : _gameWorkbench?.Projects.FirstOrDefault(project => project.Id == projectId)?.DefaultPath
                ?? string.Empty;

    private string EffectiveProjectPath(GameProjectDefinition project) =>
        EffectiveProjectPath(project.Id);

    /// <summary>
    /// 重新检查项目是否存在。进游戏页的「开发版」时调一次，
    /// 免得启动后新建了目录还要重启才认。
    /// </summary>
    public void RefreshGameProjects()
    {
        if (_gameWorkbench is null)
        {
            return;
        }

        GameProjects = BuildGameProjects();
        OnPropertyChanged(nameof(GameProjects));
        OnPropertyChanged(nameof(GameProjectCards));
        // 顶部通知栏的“缺少什么”也要跟着重算，否则新建目录后仍然显示缺失。
        NotifyConfigurationChanged();
    }

    private IReadOnlyList<GameProjectStatus> BuildGameProjects()
    {
        if (_gameWorkbench is null)
        {
            return [];
        }

        return _gameWorkbench.Projects
            .Where(project => HasAndroidLauncher || !project.IsAndroidLauncher)
            .Select(project =>
            {
                var path = EffectiveProjectPath(project);
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new GameProjectStatus
                    {
                        Id = project.Id,
                        DisplayName = project.DisplayName,
                        Role = project.Role,
                        Path = string.Empty,
                        RepositoryUrl = project.RepositoryUrl,
                        StateKey = "unconfigured",
                        StateText = "未配置",
                        DetailText = "尚未指定本地路径"
                    };
                }

                var exists = Directory.Exists(path);
                return new GameProjectStatus
                {
                    Id = project.Id,
                    DisplayName = project.DisplayName,
                    Role = project.Role,
                    Path = path,
                    RepositoryUrl = project.RepositoryUrl,
                    StateKey = exists ? "exists" : "missing",
                    StateText = exists ? "存在" : "缺失",
                    DetailText = path
                };
            })
            .ToArray();
    }

    public bool IsExecutionAvailable =>
        _isRunnerAvailable &&
        !IsRunning &&
        _adapter.Validate(_paths).IsValid;

    public string ExecutionAvailabilityText => IsRunning
        ? "当前任务正在运行，其他操作暂时不可用。"
        : !_isRunnerAvailable
            ? "任务运行器不可用，请先恢复 PowerShell 7 运行环境。"
        : IsExecutionAvailable
            ? "项目特征已通过检查，可以执行适配器动作。"
            : ConfigurationStatus;

    public ManagedToolActionRequest CreateRequest(
        ManagedToolAction action,
        PublishChannelChoice? channels = null)
    {
        var definition = _adapter.Actions.SingleOrDefault(item => item.Action == action)
            ?? throw new NotSupportedException($"{DisplayName} 不支持动作 {action}。");
        var isGameAction = IsGamePublishingAction(action);
        var version = isGameAction ? GamePublishVersion : PublishVersion;
        if (definition.RequiresVersion && string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(isGameAction
                ? "请先填写游戏版本号。"
                : "请先填写启动器版本号。");
        }

        var channel = action switch
        {
            ManagedToolAction.PackageStable => "stable",
            ManagedToolAction.PackageBeta => "beta",
            _ when isGameAction => GamePublishChannel,
            _ => PublishChannel
        };
        return new ManagedToolActionRequest(
            action,
            version.Trim(),
            channel,
            isGameAction
                ? GameReleaseDescription.Trim()
                : LauncherReleaseDescription.Trim(),
            channels);
    }

    public void RefreshLauncherPublishSettings()
    {
        OnPropertyChanged(nameof(PublishVersion));
        OnPropertyChanged(nameof(PublishChannel));
        OnPropertyChanged(nameof(LauncherReleaseDescription));
        OnPropertyChanged(nameof(LauncherVersionStateText));
        RefreshActionAvailability();
    }

    public AxTaskDefinition CreateTask(ManagedToolActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _adapter.CreateTask(request, _paths);
    }

    public void BeginAction(ToolActionItemViewModel action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IsRunning = true;
        LastResultText = $"正在执行：{action.DisplayName}";
        RefreshActionAvailability();
    }

    public void CompleteAction(AxTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IsRunning = false;
        OnPropertyChanged(nameof(DevelopmentVersionText));
        LastResultText = result.Status switch
        {
            AxTaskStatus.Succeeded => $"成功 · {result.Duration.TotalSeconds:F1}s",
            AxTaskStatus.Stopped => $"已停止 · {result.Duration.TotalSeconds:F1}s",
            _ => $"失败 · 退出码 {result.ExitCode}"
        };
        RefreshActionAvailability();
    }

    public async Task CheckLauncherPublishedVersionAsync(CancellationToken cancellationToken)
    {
        if (!HasLauncherPublishActions)
        {
            return;
        }
        await CheckLauncherPublishedVersionCoreAsync(PublishChannel, cancellationToken);
    }

    /// <summary>
    /// 动作预检：版本门禁 → 生成请求 → 需要确认时把确认内容一起算好。
    /// 这三步原先散在 MainWindow.ToolActions 里，界面只负责渲染。
    /// </summary>
    public async Task<ToolActionPreflightResult> PrepareActionAsync(
        ManagedToolAction action,
        CancellationToken cancellationToken,
        PublishChannelChoice? channels = null)
    {
        var validation = await ValidatePublishingVersionAsync(action, cancellationToken);
        if (!validation.IsAllowed)
        {
            return ToolActionPreflightResult.Block("版本检查未通过", validation.Message);
        }

        ManagedToolActionRequest request;
        try
        {
            request = CreateRequest(action, channels);
        }
        catch (Exception exception)
        {
            return ToolActionPreflightResult.Block("无法创建任务", exception.Message);
        }

        if (action == ManagedToolAction.ReplaceRelease)
        {
            var pendingService = new PendingPackageService();
            if (!pendingService.TryGetValidSummary(
                    StableKey,
                    OutputRoot,
                    out var pending,
                    out var validationMessage))
            {
                return ToolActionPreflightResult.Block("无法替换正式版", validationMessage);
            }

            return ToolActionPreflightResult.Ready(new ToolActionConfirmation(
                "确认替换正式版",
                "替换正式版",
                [
                    new("工具", DisplayName),
                    new("版本", pending!.Version),
                    new("已验证临时包", pending.PackageRoot),
                    new("正式版目标", pending.TargetRoot)
                ],
                "替换期间旧目录会被临时改名；新目录就位后立即删除临时备份。若操作失败，AxTools 会恢复旧正式版。"),
                request);
        }

        var isGameAction = PublishTargetCatalog.IsGamePublishingAction(action);
        var lines = new List<ToolActionConfirmationLine>
        {
            new("工具", DisplayName),
            new(isGameAction ? "游戏版本" : "启动器版本",
                string.IsNullOrWhiteSpace(request.Version) ? "未填写" : request.Version),
            new("通道", request.Channel),
            new("输出目录", string.IsNullOrWhiteSpace(OutputRoot) ? "未配置" : OutputRoot)
        };
        if (isGameAction)
        {
            lines.Add(new ToolActionConfirmationLine(
                "游戏目录",
                string.IsNullOrWhiteSpace(GamePackageRoot) ? "未配置" : GamePackageRoot));
        }

        lines.Add(new ToolActionConfirmationLine(
            "发布目标",
            action == ManagedToolAction.PublishGameVersionToGitHub
                ? "GitHub Release（游戏适配器配置的仓库）"
                : action == ManagedToolAction.PublishGamePatch
                    ? "GitHub Release（Windows 补丁包）"
                    : action == ManagedToolAction.GenerateGamePatchFragments
                        ? "本机补丁碎片输出目录"
                        : PublishTargetCatalog.GetPublishTarget(Descriptor.Key)));

        if (channels is not null)
        {
            lines.Add(new ToolActionConfirmationLine("本次渠道", channels.Describe()));
        }

        return ToolActionPreflightResult.Ready(new ToolActionConfirmation(
            "确认真实上传",
            "确认真实上传",
            lines,
            isGameAction
                ? PublishTargetCatalog.GetIntent(action)
                : "这是实际上传操作，将写入远端发布目标。请确认以上信息无误。"),
            request);
    }

    public async Task<PublishingVersionValidationResult> ValidatePublishingVersionAsync(
        ManagedToolAction action,
        CancellationToken cancellationToken)
    {
        if (!IsVersionGuardedPublishingAction(action))
        {
            return PublishingVersionValidationResult.Allowed("此动作不需要发布版本门禁。");
        }

        var candidate = IsGamePublishingAction(action) ? GamePublishVersion : PublishVersion;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return PublishingVersionValidationResult.Blocked("请先填写要打包或发布的版本号。");
        }

        string? onlineVersion;
        if (IsGamePublishingAction(action))
        {
            if (_gamePublishState is null)
            {
                return PublishingVersionValidationResult.Allowed(
                    "这个工具没有游戏发布状态；发布时会做下载站公网校验。");
            }

            await CheckGamePublishedVersionAsync(cancellationToken);
            if (!_gamePublishState.PublishedCheckSucceeded)
            {
                // 读不到下载站清单时不再拦死：代理/网络抖动太常见，拦死反而挡住正常发布。
                // 真正的兜底是发布内核推完之后对公网清单做的那次校验。
                return PublishingVersionValidationResult.Allowed(
                    "未能确认线上版本，先放行；发布时会做下载站公网校验。");
            }

            // PC 与安卓各发各的，所以门禁只看当前选中的平台；另一个平台落后不影响这个平台。
            onlineVersion = _gamePublishState.OnlineVersionFor(IsAndroidPlatform);
            if (string.IsNullOrWhiteSpace(onlineVersion))
            {
                return PublishingVersionValidationResult.Allowed(
                    $"下载站上还没有{PlatformLabel}版本，可以首次发布。");
            }
        }
        else
        {
            var channel = action switch
            {
                ManagedToolAction.PackageStable => "stable",
                ManagedToolAction.PackageBeta => "beta",
                _ => PublishChannel
            };
            await CheckLauncherPublishedVersionCoreAsync(channel, cancellationToken);
            if (!_launcherVersionCheckSucceeded)
            {
                // 读不到线上版本时**不再拦死**（2026-10-05 实测踩到：本机 IP 被 Gitee 匿名配额限流，
                // 返回 403 Rate Limit Exceeded，于是「出更新包 / 发布演练 / 完整发布」全被这条门禁挡住，
                // 连纯本地的出包都点不动 —— 而门禁本意只是防"版本倒退"）。
                // 与游戏包发布那条路保持一致：读不到就先放行，真正的兜底放在脚本侧 ——
                // 零境素材的发布脚本自己做幂等（同名同大小**且内容一致**才跳过）、
                // 清单最后生效、发布后做匿名自检；版本倒退检查在读得到线上版本时依然生效。
                return PublishingVersionValidationResult.Allowed(
                    "未能确认线上版本（网络或匿名限流），先放行；脚本侧会做幂等与发布后核验。");
            }
            onlineVersion = _launcherPublishedVersion;
            if (onlineVersion is null)
            {
                return PublishingVersionValidationResult.Allowed("线上尚无版本，可以首次发布。");
            }
        }

        if (string.IsNullOrWhiteSpace(onlineVersion))
        {
            // 走到这里说明"检查说自己成功了，但版本号是空的"——那是检查本身的问题，不是发布的问题。
            // 门禁只拦"确认过的版本倒退"，读不出一律放行（见上面 2026-10-05 那条注释）。
            return PublishingVersionValidationResult.Allowed(
                "线上版本号读出来是空的，无法比较，先放行；脚本侧会做幂等与发布后核验。");
        }

        try
        {
            var comparison = PublishVersionComparer.Compare(candidate, onlineVersion);
            if (comparison < 0)
            {
                return PublishingVersionValidationResult.Blocked(
                    $"目标版本 {candidate.Trim()} 低于线上最新版本 {onlineVersion}，已停止打包或发布。",
                    onlineVersion);
            }
            return comparison == 0
                ? PublishingVersionValidationResult.Allowed(
                    $"目标版本与线上最新版本 {onlineVersion} 相同，将按重新打包或续传处理。",
                    onlineVersion)
                : PublishingVersionValidationResult.Allowed(
                    $"目标版本高于线上最新版本 {onlineVersion}。",
                    onlineVersion);
        }
        catch (FormatException exception)
        {
            // 分两种：本地版本号写错了 → 该拦（脚本侧也过不去）；线上版本号读不出来 → 放行。
            try
            {
                PublishVersionComparer.Compare(candidate, candidate);
            }
            catch (FormatException)
            {
                return PublishingVersionValidationResult.Blocked(exception.Message, onlineVersion);
            }

            return PublishingVersionValidationResult.Allowed(
                $"线上版本号 {onlineVersion} 无法解析，无法比较，先放行；脚本侧会做幂等与发布后核验。");
        }
    }

    private async Task CheckLauncherPublishedVersionCoreAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        await _launcherVersionCheckLock.WaitAsync(cancellationToken);
        try
        {
            IsLauncherVersionChecking = true;
            _launcherVersionCheckSucceeded = false;
            _launcherPublishedVersion = null;
            LauncherVersionCheckStatus = "正在读取线上发布页...";
            var result = await _publishedVersionService.CheckLauncherAsync(
                Descriptor.Key,
                channel,
                cancellationToken);
            _launcherVersionCheckSucceeded = true;
            _launcherPublishedVersion = result.Version;
            LauncherPublishedVersionText = result.IsAvailable
                ? $"线上当前版本：{result.Version}"
                : "线上当前版本：暂无";
            LauncherVersionCheckStatus = $"{result.SourceName} · {result.Status}";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or FormatException)
        {
            var rateLimited = IsGiteeRateLimitException(exception);
            _launcherVersionCheckSucceeded = false;
            _launcherPublishedVersion = null;
            LauncherPublishedVersionText = "线上当前版本：检查失败";
            if (rateLimited)
            {
                // 发布源的匿名 API 都有小时级配额（Gitee 60 次/小时、**按 IP** 计；超了返回 403 +
                // text/plain + body `403 Forbidden (Rate Limit Exceeded)`，不是 429、也没有
                // X-RateLimit-* 头；GitHub 同类）。这不是"发布出问题了"，得把话说明白，
                // 否则用户只会看到一个吓人的 403。
                LauncherVersionCheckStatus =
                    "检查失败：发布源匿名接口限流（Gitee 是 60 次/小时、按 IP 计）。等一会儿或换网络即可；" +
                    "这不影响出包与发布——门禁已放行，脚本侧会做幂等与发布后核验。";
            }
            else
            {
                LauncherVersionCheckStatus = exception is TaskCanceledException
                    ? "检查超时，请稍后重试。"
                    : $"检查失败：{exception.Message}";
            }
        }
        finally
        {
            IsLauncherVersionChecking = false;
            _launcherVersionCheckLock.Release();
        }
    }

    /// <summary>
    /// Gitee 的限流只能靠**响应体**认（需求 §2 第 5、6 条）：状态码是 403，正文是
    /// <c>403 Forbidden (Rate Limit Exceeded)</c>，既不是 429 也没有 <c>X-RateLimit-*</c> 头。
    /// 403 本身还有第二种含义（令牌无权限），所以不能只看状态码。
    /// </summary>
    private static bool IsGiteeRateLimitException(Exception exception)
    {
        if (exception is not HttpRequestException requestException)
        {
            return false;
        }

        var message = requestException.Message;
        return message.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("403", StringComparison.Ordinal) &&
                message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));
    }

    public async Task CheckGamePublishedVersionAsync(CancellationToken cancellationToken)
    {
        // 只有游戏包发布工具（三个游戏）才有产品段；其它工具没有这个检查。
        if (_gamePublishState is null || _adapter.GameProductSegment is not { } productSegment)
        {
            return;
        }
        await _gamePublishState.CheckLock.WaitAsync(cancellationToken);
        try
        {
            _gamePublishState.IsChecking = true;
            _gamePublishState.VersionCheckStatus = "正在读取下载站上的 PC 与 Android 清单...";
            var result = await _publishedVersionService.CheckGameAsync(productSegment, cancellationToken);
            _gamePublishState.ApplyPublishedVersions(result);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or FormatException)
        {
            var message = exception is TaskCanceledException
                ? "线上版本检查超时，请稍后重试。"
                : $"线上版本检查失败：{exception.Message}";
            _gamePublishState.MarkPublishedCheckFailed(message);
        }
        finally
        {
            _gamePublishState.IsChecking = false;
            _gamePublishState.CheckLock.Release();
        }
    }

    private static bool IsVersionGuardedPublishingAction(ManagedToolAction action) => action is
        ManagedToolAction.PackageStable or
        ManagedToolAction.PackageBeta or
        ManagedToolAction.BuildLauncherPackage or
        ManagedToolAction.SetVersion or
        ManagedToolAction.PackageX64 or
        ManagedToolAction.ValidateAndStagePackage or
        ManagedToolAction.UploadDryRun or
        ManagedToolAction.Upload or
        ManagedToolAction.PublishDryRun or
        ManagedToolAction.Publish or
        ManagedToolAction.PublishGameVersion or
        ManagedToolAction.PublishGameVersionToGitHub or
        ManagedToolAction.PublishGamePatch or
        ManagedToolAction.GenerateGamePatchFragments;

    private IReadOnlyList<ToolActionItemViewModel> FilterActions(
        ManagedToolActionSection section) =>
        AllActions.Where(item => item.Definition.Section == section).ToArray();

    private string CreateDefaultLauncherReleaseDescription()
    {
        var version = string.IsNullOrWhiteSpace(PublishVersion)
            ? "待填写版本"
            : PublishVersion.Trim();
        return Descriptor.Key == ManagedToolKey.AxTools
            ? $"## AxTools V{version}\n\n统一管理工具的启动、构建、打包、发布、热更新与开发环境。"
            : $"## {DisplayName} V{version}";
    }

    private void RefreshActionAvailability()
    {
        var enabled = IsExecutionAvailable;
        foreach (var action in AllActions)
        {
            action.IsEnabled = enabled &&
                (action.Action is not (ManagedToolAction.PublishGamePatch or
                    ManagedToolAction.GenerateGamePatchFragments) ||
                    (!IsAndroidPlatform && CanPublishWindowsPatch)) &&
                (action.Action is not (ManagedToolAction.PublishGameVersion or
                    ManagedToolAction.PublishGameVersionToGitHub or
                    ManagedToolAction.RebuildGameDownloadRepo) || CanPublishFullGamePackage) &&
                (!IsGamePublishingAction(action.Action) ||
                 (!string.IsNullOrWhiteSpace(GamePackageRoot) &&
                  (!action.Definition.RequiresVersion ||
                   !string.IsNullOrWhiteSpace(GamePublishVersion)))) &&
                (IsGamePublishingAction(action.Action) ||
                 !action.Definition.RequiresVersion ||
                 !string.IsNullOrWhiteSpace(PublishVersion)) &&
                (action.Action != ManagedToolAction.ReplaceRelease ||
                 _pendingPackageAvailable()) &&
                !IsBlockedByOlderGameVersion(action.Action);
        }
        SourceDownloadAction.IsEnabled = _isRunnerAvailable && !IsRunning;
        LauncherReleaseNotesAction.IsEnabled = !IsRunning;
        GameReleaseNotesAction.IsEnabled = !IsRunning;
    }

    /// <summary>
    /// 倒序发布（目标版本低于这个平台线上版本）时先把按钮灰掉，省得点下去才被拦。
    /// 只比当前平台：PC 与安卓各发各的，另一个平台落后不影响这个平台。
    /// </summary>
    private bool IsBlockedByOlderGameVersion(ManagedToolAction action)
    {
        if (_gamePublishState is null ||
            action is not (ManagedToolAction.PublishGameVersion or
                ManagedToolAction.PublishGameVersionToGitHub or
                ManagedToolAction.PublishGamePatch or
                ManagedToolAction.GenerateGamePatchFragments) ||
            string.IsNullOrWhiteSpace(GamePublishVersion))
        {
            return false;
        }

        var online = _gamePublishState.OnlineVersionFor(IsAndroidPlatform);
        if (string.IsNullOrWhiteSpace(online))
        {
            return false;
        }

        try
        {
            return PublishVersionComparer.Compare(GamePublishVersion, online) < 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsGamePublishingAction(ManagedToolAction action) => action is
        ManagedToolAction.CheckGamePackage or
        ManagedToolAction.PublishGameVersion or
        ManagedToolAction.PublishGameVersionToGitHub or
        ManagedToolAction.RebuildGameDownloadRepo;

    private static bool IsGameMaintenanceAction(ManagedToolAction action) =>
        action is ManagedToolAction.RebuildGameDownloadRepo;

    private void SharedGamePublishState_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CrossingVoidGamePublishState.Version))
        {
            OnPropertyChanged(nameof(GamePublishVersion));
            OnPropertyChanged(nameof(GameVersionMajor));
            OnPropertyChanged(nameof(GameVersionFeature));
            OnPropertyChanged(nameof(GameVersionBugFix));
            OnPropertyChanged(nameof(GameReleaseDescription));
        }
        else if (args.PropertyName == nameof(CrossingVoidGamePublishState.Channel))
        {
            OnPropertyChanged(nameof(GamePublishChannel));
        }
        else if (args.PropertyName == nameof(CrossingVoidGamePublishState.ReleaseNotes))
        {
            OnPropertyChanged(nameof(GamePublishReleaseNotes));
            OnPropertyChanged(nameof(GameReleaseDescription));
        }
        else if (args.PropertyName == nameof(CrossingVoidGamePublishState.PublishedVersionText))
        {
            OnPropertyChanged(nameof(GamePublishedVersionText));
        }
        else if (args.PropertyName == nameof(CrossingVoidGamePublishState.VersionCheckStatus))
        {
            OnPropertyChanged(nameof(GameVersionCheckStatus));
        }
        else if (args.PropertyName == nameof(CrossingVoidGamePublishState.IsChecking))
        {
            OnPropertyChanged(nameof(IsGameVersionChecking));
        }

        OnPropertyChanged(args.PropertyName);
        RefreshActionAvailability();
    }
}