using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;
    private readonly IProjectRootMigrationService _projectRootMigrationService;
    private readonly TaskHistoryService? _taskHistoryService;
    private string _projectRootStatusTitle = "目录已就绪";
    private string _projectRootStatusMessage = string.Empty;
    private OperationNoticeSeverity _projectRootStatusSeverity =
        OperationNoticeSeverity.Success;
    private bool _isProjectRootMigrationRunning;
    private readonly Stack<LogSettingChange> _logSettingUndoStack = new();
    private readonly object _settingsSaveSync = new();
    private Task _pendingSettingsSave = Task.CompletedTask;
    private bool _isApplyingLogUndo;
    private bool _isSynchronizingAxReleaseSettings;

    public SettingsViewModel(
        AppSettings settings,
        AppSettingsService settingsService,
        IReadOnlyList<ToolPageViewModel> tools,
        RunnerDiagnosticsViewModel runnerDiagnostics,
        IProjectRootMigrationService? projectRootMigrationService = null,
        TaskHistoryService? taskHistoryService = null,
        StorageViewModel? storage = null,
        IReadOnlyList<ToolchainStatusItem>? toolchainStatuses = null,
        string? environmentChangeSummary = null)
    {
        _settings = settings;
        _settingsService = settingsService;
        Tools = tools;
        RunnerDiagnostics = runnerDiagnostics;
        _projectRootMigrationService = projectRootMigrationService ??
            new ProjectRootMigrationService();
        _taskHistoryService = taskHistoryService;
        _projectRootStatusMessage = $"已确认目录存在：{settings.ProjectRootPath}";
        var storageCatalog = new StorageCatalogService();
        Storage = storage ?? new StorageViewModel(
            settings,
            new StorageScanService(storageCatalog),
            new StorageCleanupService(storageCatalog));
        Update = new UpdateViewModel(settings);
        DeveloperRelease = new DeveloperReleaseViewModel(settings);
        Environment = new ToolchainEnvironmentViewModel(
            settings.Toolchains,
            toolchainStatuses,
            environmentChangeSummary);
        Update.PropertyChanged += Update_PropertyChanged;
        DeveloperRelease.PropertyChanged += DeveloperRelease_PropertyChanged;
        Environment.Settings.PropertyChanged += ToolchainSettings_PropertyChanged;
        foreach (var tool in Tools)
        {
            tool.PropertyChanged += Tool_PropertyChanged;
        }
    }

    public IReadOnlyList<ToolPageViewModel> Tools { get; }

    public RunnerDiagnosticsViewModel RunnerDiagnostics { get; }

    public StorageViewModel Storage { get; }

    public UpdateViewModel Update { get; }

    public DeveloperReleaseViewModel DeveloperRelease { get; }

    public ToolchainEnvironmentViewModel Environment { get; }

    public event EventHandler? LogSettingsChanged;

    public event EventHandler<Exception>? SettingsSaveFailed;

    public string ProjectRootPath => _settings.ProjectRootPath;

    public string ProjectRootStatusTitle
    {
        get => _projectRootStatusTitle;
        private set => SetProperty(ref _projectRootStatusTitle, value);
    }

    public string ProjectRootStatusMessage
    {
        get => _projectRootStatusMessage;
        private set => SetProperty(ref _projectRootStatusMessage, value);
    }

    public OperationNoticeSeverity ProjectRootStatusSeverity
    {
        get => _projectRootStatusSeverity;
        private set => SetProperty(ref _projectRootStatusSeverity, value);
    }

    public bool IsProjectRootMigrationRunning
    {
        get => _isProjectRootMigrationRunning;
        private set
        {
            if (SetProperty(ref _isProjectRootMigrationRunning, value))
            {
                OnPropertyChanged(nameof(CanChangeProjectRoot));
            }
        }
    }

    public bool CanChangeProjectRoot => !IsProjectRootMigrationRunning;

    public ThemeMode ThemeMode
    {
        get => _settings.ThemeMode;
        set
        {
            if (SetProperty(
                _settings.ThemeMode,
                value,
                _settings,
                static (model, newValue) => model.ThemeMode = newValue))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool ShowWorkspacePath
    {
        get => _settings.ShowWorkspacePath;
        set
        {
            if (SetProperty(
                _settings.ShowWorkspacePath,
                value,
                _settings,
                static (model, newValue) => model.ShowWorkspacePath = newValue))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool LogEnabled
    {
        get => _settings.LogEnabled;
        set => SetLogSetting(
            nameof(LogEnabled),
            _settings.LogEnabled,
            value,
            newValue => _settings.LogEnabled = newValue,
            notifyOptionsEnabled: true);
    }

    public bool LogSaveToFileEnabled
    {
        get => _settings.LogSaveToFileEnabled;
        set => SetLogSetting(
            nameof(LogSaveToFileEnabled),
            _settings.LogSaveToFileEnabled,
            value,
            newValue => _settings.LogSaveToFileEnabled = newValue);
    }

    public bool LogUserOperations
    {
        get => _settings.LogUserOperations;
        set => SetLogSetting(
            nameof(LogUserOperations),
            _settings.LogUserOperations,
            value,
            newValue => _settings.LogUserOperations = newValue);
    }

    public bool LogWarnings
    {
        get => _settings.LogWarnings;
        set => SetLogSetting(
            nameof(LogWarnings),
            _settings.LogWarnings,
            value,
            newValue => _settings.LogWarnings = newValue);
    }

    public bool LogErrors
    {
        get => _settings.LogErrors;
        set => SetLogSetting(
            nameof(LogErrors),
            _settings.LogErrors,
            value,
            newValue => _settings.LogErrors = newValue);
    }

    public bool AreLogOptionsEnabled => LogEnabled;

    public bool CanUndoLogSetting => _logSettingUndoStack.Count > 0;

    public Task PendingLogSettingsSave
        => PendingSettingsSave;

    public Task PendingSettingsSave
    {
        get
        {
            lock (_settingsSaveSync)
            {
                return _pendingSettingsSave;
            }
        }
    }

    public bool ShouldWriteLog(LogKind kind)
    {
        if (!LogEnabled)
        {
            return false;
        }

        return kind switch
        {
            LogKind.User => LogUserOperations,
            LogKind.Warning => LogWarnings,
            LogKind.Error => LogErrors,
            _ => true
        };
    }

    public async Task<bool> UndoLastLogSettingAsync()
    {
        if (_logSettingUndoStack.Count == 0)
        {
            return false;
        }

        var change = _logSettingUndoStack.Pop();
        OnPropertyChanged(nameof(CanUndoLogSetting));
        _isApplyingLogUndo = true;
        try
        {
            ApplyLogSetting(change.PropertyName, change.PreviousValue);
        }
        finally
        {
            _isApplyingLogUndo = false;
        }

        await PendingSettingsSave.ConfigureAwait(false);
        return true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await PendingSettingsSave.ConfigureAwait(false);
        await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    private void SetLogSetting(
        string propertyName,
        bool currentValue,
        bool newValue,
        Action<bool> assign,
        bool notifyOptionsEnabled = false)
    {
        if (currentValue == newValue)
        {
            return;
        }

        if (!_isApplyingLogUndo)
        {
            _logSettingUndoStack.Push(new LogSettingChange(propertyName, currentValue));
            OnPropertyChanged(nameof(CanUndoLogSetting));
        }

        assign(newValue);
        OnPropertyChanged(propertyName);
        if (notifyOptionsEnabled)
        {
            OnPropertyChanged(nameof(AreLogOptionsEnabled));
        }

        LogSettingsChanged?.Invoke(this, EventArgs.Empty);
        QueueSettingsSave();
    }

    private void ApplyLogSetting(string propertyName, bool value)
    {
        switch (propertyName)
        {
            case nameof(LogEnabled):
                LogEnabled = value;
                break;
            case nameof(LogSaveToFileEnabled):
                LogSaveToFileEnabled = value;
                break;
            case nameof(LogUserOperations):
                LogUserOperations = value;
                break;
            case nameof(LogWarnings):
                LogWarnings = value;
                break;
            case nameof(LogErrors):
                LogErrors = value;
                break;
            default:
                throw new InvalidOperationException($"未知 Log 设置：{propertyName}");
        }
    }

    private void QueueSettingsSave()
    {
        lock (_settingsSaveSync)
        {
            _pendingSettingsSave = SaveSettingsAfterAsync(_pendingSettingsSave);
        }
    }

    private async Task SaveSettingsAfterAsync(Task previousSave)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            await _settingsService.SaveAsync(_settings, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SettingsSaveFailed?.Invoke(this, exception);
        }
    }

    private void Update_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(UpdateViewModel.Source) or
            nameof(UpdateViewModel.Channel) or
            nameof(UpdateViewModel.AutoCheckEnabled) or
            nameof(UpdateViewModel.CheckOnStartup) or
            nameof(UpdateViewModel.ConnectionTimeoutSeconds))
        {
            QueueSettingsSave();
        }
    }

    private void DeveloperRelease_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DeveloperReleaseViewModel.TargetVersion) or
            nameof(DeveloperReleaseViewModel.Channel) or
            nameof(DeveloperReleaseViewModel.ReleaseDescription))
        {
            if (!_isSynchronizingAxReleaseSettings)
            {
                _isSynchronizingAxReleaseSettings = true;
                try
                {
                    Tools.Single(tool => tool.Descriptor.Key == ManagedToolKey.AxTools)
                        .RefreshLauncherPublishSettings();
                }
                finally
                {
                    _isSynchronizingAxReleaseSettings = false;
                }
            }
            QueueSettingsSave();
        }
    }

    private void ToolchainSettings_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        QueueSettingsSave();

    private void Tool_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ToolPageViewModel.SourceRoot) or
            nameof(ToolPageViewModel.DevelopmentExecutable) or
            nameof(ToolPageViewModel.ReleaseExecutable) or
            nameof(ToolPageViewModel.OutputRoot) or
            nameof(ToolPageViewModel.PublishVersion) or
            nameof(ToolPageViewModel.PublishChannel) or
            nameof(ToolPageViewModel.LauncherReleaseDescription) or
            nameof(ToolPageViewModel.GamePackageRoot) or
            nameof(ToolPageViewModel.GamePublishVersion) or
            nameof(ToolPageViewModel.GamePublishChannel) or
            nameof(ToolPageViewModel.GamePublishReleaseNotes))
        {
            if (sender is ToolPageViewModel { Descriptor.Key: ManagedToolKey.AxTools } &&
                !_isSynchronizingAxReleaseSettings &&
                args.PropertyName is nameof(ToolPageViewModel.PublishVersion) or
                    nameof(ToolPageViewModel.PublishChannel) or
                    nameof(ToolPageViewModel.LauncherReleaseDescription))
            {
                _isSynchronizingAxReleaseSettings = true;
                try
                {
                    DeveloperRelease.RefreshReleaseSettings();
                }
                finally
                {
                    _isSynchronizingAxReleaseSettings = false;
                }
            }
            QueueSettingsSave();
        }
    }

    private sealed record LogSettingChange(string PropertyName, bool PreviousValue);

    public async Task<string> RescanEnvironmentAsync(
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (Environment.IsScanning)
        {
            throw new InvalidOperationException("环境扫描正在进行中。");
        }

        Environment.IsScanning = true;
        try
        {
            progress?.Report(new ProgressUpdate("正在检测工具链路径...", 20, string.Empty));
            var baseResult = new ToolchainEnvironmentService().DetectAndApply(_settings.Toolchains);
            progress?.Report(new ProgressUpdate("正在读取 Unreal、Visual Studio 与 Android 配置...", 55, string.Empty));
            var inventory = new SystemEnvironmentInventoryService().CreateInventory(
                _settings.Toolchains,
                EnvironmentInventoryPaths.CreateDefault(_settings.Toolchains),
                baseResult.Items);
            progress?.Report(new ProgressUpdate("正在对比上次环境快照...", 80, _settings.ProjectRootPath));
            var snapshot = await new EnvironmentInventorySnapshotService(
                new AtomicJsonFileService()).ApplyAsync(
                    _settings.ProjectRootPath,
                    inventory,
                    cancellationToken);
            Environment.ReplaceItems(snapshot.Items, snapshot.ChangeSummary);
            if (baseResult.Changed)
            {
                await _settingsService.SaveAsync(_settings, cancellationToken);
            }

            progress?.Report(new ProgressUpdate("环境扫描完成。", 100, snapshot.ChangeSummary));
            return $"检测到 {snapshot.Items.Count} 个环境组件。{snapshot.ChangeSummary}";
        }
        finally
        {
            Environment.IsScanning = false;
        }
    }

    public async Task<ProjectRootChangeResult> ChangeProjectRootAsync(
        string newProjectRoot,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (IsProjectRootMigrationRunning)
        {
            throw new InvalidOperationException("整体项目目录迁移正在进行中。");
        }

        IsProjectRootMigrationRunning = true;
        SetProjectRootStatus(
            OperationNoticeSeverity.Informational,
            "正在迁移目录",
            $"目标目录：{Path.GetFullPath(newProjectRoot)}");
        try
        {
            return await ChangeProjectRootCoreAsync(
                newProjectRoot,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetProjectRootStatus(
                OperationNoticeSeverity.Warning,
                "迁移已取消",
                "原目录和设置已保留，旧目录未删除。");
            throw;
        }
        catch (Exception exception)
        {
            SetProjectRootStatus(
                OperationNoticeSeverity.Error,
                "迁移失败",
                $"原目录和设置已保留。错误：{exception.Message}");
            throw;
        }
        finally
        {
            IsProjectRootMigrationRunning = false;
        }
    }

    private async Task<ProjectRootChangeResult> ChangeProjectRootCoreAsync(
        string newProjectRoot,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var oldProjectRoot = Path.GetFullPath(_settings.ProjectRootPath);
        var normalizedNewRoot = Path.GetFullPath(newProjectRoot);
        if (ProjectRootMigrationService.PathsEqual(oldProjectRoot, normalizedNewRoot))
        {
            SetProjectRootStatus(
                OperationNoticeSeverity.Warning,
                "目录未变化",
                $"当前已经在使用：{normalizedNewRoot}");
            return new ProjectRootChangeResult(0, 0, 0, true, null);
        }

        var migration = await _projectRootMigrationService.CopyAndVerifyAsync(
            oldProjectRoot,
            normalizedNewRoot,
            progress,
            cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _settings.ProjectRootPath = normalizedNewRoot;
            RebindWorkspaceArtifactPaths(normalizedNewRoot);
            await _settingsService.SaveAsync(_settings, cancellationToken);
            _taskHistoryService?.Rebind(normalizedNewRoot);
            await VerifyWritableAsync(normalizedNewRoot, cancellationToken);
            OnPropertyChanged(nameof(ProjectRootPath));
        }
        catch
        {
            _settings.ProjectRootPath = oldProjectRoot;
            RebindWorkspaceArtifactPaths(oldProjectRoot);
            _taskHistoryService?.Rebind(oldProjectRoot);
            try
            {
                await _settingsService.SaveAsync(_settings, CancellationToken.None);
            }
            catch
            {
                // Preserve the original exception; startup still follows the old bootstrap when commit failed.
            }

            _projectRootMigrationService.TryDeleteDirectory(
                normalizedNewRoot,
                out _);
            OnPropertyChanged(nameof(ProjectRootPath));
            throw;
        }

        var oldDirectoryDeleted = _projectRootMigrationService.TryDeleteDirectory(
            oldProjectRoot,
            out var cleanupError);
        progress?.Report(new ProgressUpdate(
            oldDirectoryDeleted
                ? "整体项目目录迁移完成。"
                : "整体项目目录已切换，旧目录清理失败。",
            100,
            oldDirectoryDeleted ? normalizedNewRoot : cleanupError));

        SetProjectRootStatus(
            oldDirectoryDeleted
                ? OperationNoticeSeverity.Success
                : OperationNoticeSeverity.Warning,
            oldDirectoryDeleted
                ? "目录迁移完成"
                : "目录迁移完成，旧目录待清理",
            oldDirectoryDeleted
                ? $"新目录：{normalizedNewRoot}。旧目录已删除。"
                : $"新目录：{normalizedNewRoot}。旧目录清理失败：{cleanupError}");

        return new ProjectRootChangeResult(
            migration.FileCount,
            migration.DirectoryCount,
            migration.TotalBytes,
            oldDirectoryDeleted,
            cleanupError);
    }

    private void RebindWorkspaceArtifactPaths(string projectRoot)
    {
        WorkspaceArtifactPathPolicy.Apply(projectRoot, _settings.ManagedTools);
        _settings.CrossingVoidPackage.OutputDirectory =
            _settings.ManagedTools["CrossingVoidinitiator-PC"].OutputRoot;

        foreach (var tool in Tools)
        {
            tool.RefreshWorkspaceOutputRoot();
        }
    }

    private void SetProjectRootStatus(
        OperationNoticeSeverity severity,
        string title,
        string message)
    {
        ProjectRootStatusSeverity = severity;
        ProjectRootStatusTitle = title;
        ProjectRootStatusMessage = message;
    }

    private static async Task VerifyWritableAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(
            projectRoot,
            $".axtools-write-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                probePath,
                "ok",
                cancellationToken);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }
}
