using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace AxTools;

public partial class App : Application
{
    private Window? _window;

    /// <summary>主窗口引用；UserControl 里弹文件夹选择器需要拿窗口句柄。</summary>
    public static Window? ShellWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 兜底：UI 线程上没人接的异常默认会让进程直接消失、什么都不留
        // （2026-10-11 的闪退就是 COMException 0x80000019 双弹窗被抛到 async void 上，
        //  最终表现成 Microsoft.UI.Xaml.dll 的 stowed exception 0xc000027b）。
        // 这里先落一份现场，再让进程活下去。
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AxTools",
                "CrashLogs");
            Directory.CreateDirectory(logRoot);
            File.AppendAllText(
                Path.Combine(logRoot, $"Unhandled-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:O}] {args.Message}{Environment.NewLine}" +
                $"{args.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 记录现场本身失败就算了，别在兜底里再抛一次。
        }

        // 已经接过手：不让它升级成"进程消失"。当前这一件事多半是没做成，
        // 状态拿不准就重启一次 AxTools。
        args.Handled = true;
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
        var serverGitUpdateService = processHost is null
            ? null
            : new ServerGitUpdateService(
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
            serverStatusService: serverStatusService,
            serverGitUpdateService: serverGitUpdateService);

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
        ShellWindow = _window;
        _window.Activate();
    }
}
