using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace AxTools;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _ = LaunchSafelyAsync();
    }

    private async Task LaunchSafelyAsync()
    {
        try
        {
            await LaunchAsync();
        }
        catch (Exception exception)
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AxTools",
                "StartupErrors");
            Directory.CreateDirectory(logRoot);
            var logPath = Path.Combine(logRoot, $"Startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(logPath, exception.ToString());
            Exit();
        }
    }

    private async Task LaunchAsync()
    {
        var pathProvider = new SettingsPathProvider();
        var settingsService = new AppSettingsService(
            new AtomicJsonFileService(),
            pathProvider.GetBootstrapPath(),
            pathProvider.GetDefaultProjectRoot);
        var settings = await settingsService.LoadAsync(CancellationToken.None);
        IReadOnlyDictionary<Core.Models.ManagedToolKey, string> pathDetectionStatuses;
        IReadOnlyList<Core.Models.ToolchainStatusItem> toolchainStatuses;
        var environmentChangeSummary = "尚未建立环境快照。";
        try
        {
            var detectionResult = new ManagedToolPathDetector().DetectAndApply(
                AppContext.BaseDirectory,
                settings.ManagedTools);
            var toolchainResult = new ToolchainEnvironmentService().DetectAndApply(
                settings.Toolchains);
            if (detectionResult.Changed || toolchainResult.Changed)
            {
                await settingsService.SaveAsync(settings, CancellationToken.None);
            }

            pathDetectionStatuses = detectionResult.Statuses;
            var inventory = new SystemEnvironmentInventoryService().CreateInventory(
                settings.Toolchains,
                Core.Models.EnvironmentInventoryPaths.CreateDefault(settings.Toolchains),
                toolchainResult.Items);
            var snapshot = await new EnvironmentInventorySnapshotService(
                new AtomicJsonFileService()).ApplyAsync(
                    settings.ProjectRootPath,
                    inventory,
                    CancellationToken.None);
            toolchainStatuses = snapshot.Items;
            environmentChangeSummary = snapshot.ChangeSummary;
        }
        catch (Exception exception)
        {
            pathDetectionStatuses = new Dictionary<Core.Models.ManagedToolKey, string>
            {
                [Core.Models.ManagedToolKey.AxTools] =
                    $"AxTools 路径自动检测失败：{exception.Message}"
            };
            toolchainStatuses = [];
        }

        string? powerShellPath = null;
        string? runnerEnvironmentError = null;
        try
        {
            powerShellPath = File.Exists(settings.Toolchains.PowerShellPath)
                ? settings.Toolchains.PowerShellPath
                : new PowerShellLocator().FindPowerShell();
        }
        catch (FileNotFoundException exception)
        {
            runnerEnvironmentError = exception.Message;
        }

        var diagnostics = new RunnerDiagnosticsViewModel(
            powerShellPath,
            runnerEnvironmentError);
        var processHost = powerShellPath is null
            ? null
            : new PowerShellProcessHost(
                powerShellPath,
                toolchains: settings.Toolchains);
        var taskRunner = processHost is null
            ? null
            : new AxTaskRunner(processHost, new AxTaskEventParser());
        var serverStatusService = processHost is null
            ? null
            : new ServerStatusService(
                processHost,
                Path.Combine(AppContext.BaseDirectory, "Scripts"));
        var historyService = new TaskHistoryService(
            settings.ProjectRootPath,
            new AtomicJsonFileService());
        var viewModel = new ApplicationViewModel(
            settings,
            settingsService,
            taskRunner: taskRunner,
            taskHistoryService: historyService,
            runnerDiagnostics: diagnostics,
            pathDetectionStatuses: pathDetectionStatuses,
            toolchainStatuses: toolchainStatuses,
            environmentChangeSummary: environmentChangeSummary,
            serverStatusService: serverStatusService);

        var selfRebuildResult = SelfRebuildResultService.Take(
            SelfRebuildResultService.GetDefaultResultPath());
        if (selfRebuildResult is not null)
        {
            viewModel.LogService.Write(
                selfRebuildResult.Success
                    ? Core.Models.LogKind.Info
                    : Core.Models.LogKind.Error,
                $"{selfRebuildResult.Message}{Environment.NewLine}外部构建日志：{selfRebuildResult.LogPath}");
        }

        _window = new MainWindow(viewModel);
        _window.Activate();
    }
}
