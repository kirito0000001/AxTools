using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using AxTools.Core.Adapters;
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
        IReadOnlyList<FragmentGameOption>? fragmentGameOptions = null)
    {
        Descriptor = descriptor;
        _paths = paths;
        _gamePublishPaths = gamePublishPaths;
        _fragmentGameOptions = fragmentGameOptions ?? Array.Empty<FragmentGameOption>();
        _selectedFragmentGame = _fragmentGameOptions.Count > 0 ? _fragmentGameOptions[0] : null;
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
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
        ReleaseActions = FilterActions(ManagedToolActionSection.Release);
        SourceDownloadAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.DownloadSource,
            "从 GitHub 下载源码",
            ManagedToolActionSection.Development,
            "\uE896",
            true));
        ReleaseDownloadAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.DownloadRelease,
            "下载正式版",
            ManagedToolActionSection.Release,
            "\uE896",
            true));
        DownloadLinkAction = new ToolActionItemViewModel(new ManagedToolActionDefinition(
            ManagedToolAction.GetDownloadLink,
            "获取下载链接",
            ManagedToolActionSection.Release,
            "\uE71B",
            false));
        DevelopmentPanelActions = [SourceDownloadAction, .. DevelopmentActions];
        ReleasePanelActions = [ReleaseDownloadAction, DownloadLinkAction, .. ReleaseActions];
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

    public IReadOnlyList<ToolActionItemViewModel> ReleaseActions { get; }

    public IReadOnlyList<ToolActionItemViewModel> ReleasePanelActions { get; }

    public ToolActionItemViewModel SourceDownloadAction { get; }

    public ToolActionItemViewModel ReleaseDownloadAction { get; }

    public ToolActionItemViewModel DownloadLinkAction { get; }

    public string GiteeReleasesUrl =>
        ManagedRepositoryCatalog.Get(Descriptor.Key).GiteeReleasesUrl;

    public string DevelopmentVersionText =>
        $"开发版版本：{ExecutableVersionReader.Read(DevelopmentExecutable)}";

    public string ReleaseVersionText =>
        $"正式版版本：{ExecutableVersionReader.Read(ReleaseExecutable)}";

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
                OnPropertyChanged(nameof(ConfigurationStatus));
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

    public string SourceRoot
    {
        get => _paths.SourceRoot;
        set
        {
            if (SetProperty(_paths.SourceRoot, value, _paths, static (model, newValue) => model.SourceRoot = newValue))
            {
                OnPropertyChanged(nameof(ConfigurationStatus));
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
        set
        {
            if (SetProperty(
                _paths.ReleaseExecutable,
                value,
                _paths,
                static (model, newValue) => model.ReleaseExecutable = newValue))
            {
                OnPropertyChanged(nameof(ReleaseVersionText));
            }
        }
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
                OnPropertyChanged(nameof(ConfigurationStatus));
                OnPropertyChanged(nameof(IsExecutionAvailable));
                OnPropertyChanged(nameof(ExecutionAvailabilityText));
                RefreshActionAvailability();
            }
        }
    }

    internal void RefreshWorkspaceOutputRoot()
    {
        OnPropertyChanged(nameof(OutputRoot));
        OnPropertyChanged(nameof(ConfigurationStatus));
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
                OnPropertyChanged(nameof(ConfigurationStatus));
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

    public string ConfigurationStatus => _adapter.Validate(_paths).Message;

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

    public ManagedToolActionRequest CreateRequest(ManagedToolAction action)
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
                : LauncherReleaseDescription.Trim());
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
        OnPropertyChanged(nameof(ReleaseVersionText));
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
        ReleaseDownloadAction.IsEnabled = _isRunnerAvailable && !IsRunning;
        DownloadLinkAction.IsEnabled = _isRunnerAvailable && !IsRunning;
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