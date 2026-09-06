using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using AxTools.Core.Adapters;
using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed class ToolPageViewModel : ObservableObject
{
    private readonly ManagedToolPaths _paths;
    private readonly IManagedToolAdapter _adapter;
    private readonly bool _isRunnerAvailable;
    private readonly Func<bool> _pendingPackageAvailable;
    private readonly PublishedVersionService _publishedVersionService;
    private readonly CrossingVoidGamePublishState? _sharedGamePublishState;
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
        CrossingVoidGamePublishState? sharedGamePublishState = null)
    {
        Descriptor = descriptor;
        _paths = paths;
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _isRunnerAvailable = isRunnerAvailable;
        _publishedVersionService = publishedVersionService ?? new PublishedVersionService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        _sharedGamePublishState = sharedGamePublishState;
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
        GamePublishActions = gamePublishActions.Length == 0
            ? []
            : [GameReleaseNotesAction, .. gamePublishActions];
        if (_sharedGamePublishState is not null)
        {
            _sharedGamePublishState.PropertyChanged += SharedGamePublishState_PropertyChanged;
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

    public ToolActionItemViewModel LauncherReleaseNotesAction { get; }

    public ToolActionItemViewModel GameReleaseNotesAction { get; }

    public bool HasPublishActions => PublishActions.Count > 0;

    public bool HasLauncherPublishActions => LauncherPublishActions.Count > 0;

    public bool HasGamePackagePublishing => Descriptor.Key == ManagedToolKey.CrossingVoidGame;

    public string GamePackageLabel => "游戏包目录";

    public string GamePublishingTitle => "零境游戏发布";

    public string GamePublishingDescription => "统一制作并发布 PC 与 Android 游戏本体分片，发布前保持双端版本一致。";

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
            return $"## 零境交错 {version}\n\n平台：PC / Android\n\n" +
                "本版本包含完整游戏资源分片，可由零境启动器自动下载、校验和合并。";
        }
        set => GamePublishReleaseNotes = value;
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
        get => _sharedGamePublishState?.Version ?? _paths.GamePublishVersion;
        set
        {
            if (_sharedGamePublishState is not null)
            {
                _sharedGamePublishState.Version = value;
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
        get => _sharedGamePublishState?.Channel ?? _paths.GamePublishChannel;
        set
        {
            if (_sharedGamePublishState is not null)
            {
                _sharedGamePublishState.Channel = value;
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
        get => _sharedGamePublishState is null ? _paths.GamePublishPlatform : _paths.GamePublishPlatform;
        set => SetProperty(_paths.GamePublishPlatform, value is "Android" ? "Android" : "Windows", _paths,
            static (model, newValue) => model.GamePublishPlatform = newValue);
    }

    public string GamePublishReleaseNotes
    {
        get => _sharedGamePublishState?.ReleaseNotes ?? _paths.GamePublishReleaseNotes;
        set
        {
            if (_sharedGamePublishState is not null)
            {
                _sharedGamePublishState.ReleaseNotes = value;
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
        _sharedGamePublishState?.PublishedVersionText ?? "尚未检查线上双端版本。";

    public string GameVersionCheckStatus =>
        _sharedGamePublishState?.VersionCheckStatus ?? "此工具没有双端游戏版本。";

    public bool IsGameVersionChecking => _sharedGamePublishState?.IsChecking == true;

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
        get => _paths.GamePackageRoot;
        set
        {
            if (SetProperty(
                _paths.GamePackageRoot,
                value,
                _paths,
                static (model, newValue) => model.GamePackageRoot = newValue))
            {
                RefreshActionAvailability();
            }
        }
    }

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
            await CheckGamePublishedVersionAsync(cancellationToken);
            if (_sharedGamePublishState is null || !_sharedGamePublishState.PublishedCheckSucceeded)
            {
                return PublishingVersionValidationResult.Blocked("无法确认线上最新游戏版本，已停止打包或发布。");
            }
            if (_sharedGamePublishState.HasNoPublishedVersion)
            {
                return PublishingVersionValidationResult.Allowed("线上尚无游戏版本，可以首次发布。");
            }
            if (_sharedGamePublishState.OnlineVersionsAligned != true)
            {
                return PublishingVersionValidationResult.Blocked("线上 PC 与 Android 游戏版本不一致，已停止打包或发布。");
            }
            onlineVersion = _sharedGamePublishState.OnlineVersion;
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
                return PublishingVersionValidationResult.Blocked("无法确认线上最新版本，已停止打包或发布。");
            }
            onlineVersion = _launcherPublishedVersion;
            if (onlineVersion is null)
            {
                return PublishingVersionValidationResult.Allowed("线上尚无版本，可以首次发布。");
            }
        }

        if (string.IsNullOrWhiteSpace(onlineVersion))
        {
            return PublishingVersionValidationResult.Blocked(
                "线上版本检查结果不完整，已停止打包或发布。");
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
            return PublishingVersionValidationResult.Blocked(exception.Message, onlineVersion);
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
            _launcherVersionCheckSucceeded = false;
            _launcherPublishedVersion = null;
            LauncherPublishedVersionText = "线上当前版本：检查失败";
            LauncherVersionCheckStatus = exception is TaskCanceledException
                ? "检查超时，请稍后重试。"
                : $"检查失败：{exception.Message}";
        }
        finally
        {
            IsLauncherVersionChecking = false;
            _launcherVersionCheckLock.Release();
        }
    }

    public async Task CheckGamePublishedVersionAsync(CancellationToken cancellationToken)
    {
        if (_sharedGamePublishState is null)
        {
            return;
        }
        await _sharedGamePublishState.CheckLock.WaitAsync(cancellationToken);
        try
        {
            _sharedGamePublishState.IsChecking = true;
            _sharedGamePublishState.VersionCheckStatus = "正在同时读取 PC 与 Android 发布清单...";
            var result = await _publishedVersionService.CheckCrossingVoidGameAsync(
                GamePublishChannel,
                cancellationToken);
            _sharedGamePublishState.ApplyPublishedVersions(result);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or FormatException)
        {
            var message = exception is TaskCanceledException
                ? "双端版本检查超时，请稍后重试。"
                : $"双端版本检查失败：{exception.Message}";
            _sharedGamePublishState.MarkPublishedCheckFailed(message);
        }
        finally
        {
            _sharedGamePublishState.IsChecking = false;
            _sharedGamePublishState.CheckLock.Release();
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
        ManagedToolAction.BuildGameChunks or
        ManagedToolAction.UploadGameChunks or
        ManagedToolAction.PublishGamePackage;

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
                (!IsGamePublishingAction(action.Action) ||
                 (!string.IsNullOrWhiteSpace(GamePackageRoot) &&
                  (!action.Definition.RequiresVersion ||
                   !string.IsNullOrWhiteSpace(GamePublishVersion)))) &&
                (IsGamePublishingAction(action.Action) ||
                 !action.Definition.RequiresVersion ||
                 !string.IsNullOrWhiteSpace(PublishVersion)) &&
                (action.Action != ManagedToolAction.ReplaceRelease ||
                 _pendingPackageAvailable()) &&
                !IsBlockedByGameVersionMismatch(action.Action);
        }
        SourceDownloadAction.IsEnabled = _isRunnerAvailable && !IsRunning;
        ReleaseDownloadAction.IsEnabled = _isRunnerAvailable && !IsRunning;
        DownloadLinkAction.IsEnabled = _isRunnerAvailable && !IsRunning;
        LauncherReleaseNotesAction.IsEnabled = !IsRunning;
        GameReleaseNotesAction.IsEnabled = !IsRunning;
    }

    private bool IsBlockedByGameVersionMismatch(ManagedToolAction action) =>
        _sharedGamePublishState is not null &&
        _sharedGamePublishState.OnlineVersionsAligned != true &&
        action is (ManagedToolAction.UploadGameChunks or
            ManagedToolAction.PublishGamePackage);

    private static bool IsGamePublishingAction(ManagedToolAction action) => action is
        ManagedToolAction.BuildGameChunks or
        ManagedToolAction.UploadGameChunks or
        ManagedToolAction.PublishGamePackage;

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
