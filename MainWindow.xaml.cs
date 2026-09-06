using System.Reflection;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AxTools;

public sealed partial class MainWindow : Window
{
    private readonly ApplicationViewModel _viewModel;

    public MainWindow(ApplicationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        RootGrid.DataContext = viewModel;
        InitializeLogPanel();
        AxToolsPage.DataContext = viewModel.Tools[0];
        FantasyToolsPage.DataContext = viewModel.Tools[1];
        GalExcleToolsPage.DataContext = viewModel.Tools[2];
        CrossingVoidZDToolPage.DataContext = viewModel.Tools[3];
        FantasyProjectPcPage.DataContext = viewModel.Tools[4];
        CrossingVoidPcPage.DataContext = viewModel.Tools[5];
        CrossingVoidAndroidPage.DataContext = viewModel.Tools[6];
        FantasyGamePage.DataContext = viewModel.Tools[7];
        FantasyAndroidPage.DataContext = viewModel.Tools[8];
        CrossingVoidGamePage.DataContext = viewModel.Tools[9];
        GlobalSettingsPage.DataContext = viewModel.Settings;
        GlobalSettingsPage.PathSelectionRequested += GlobalSettingsPage_PathSelectionRequested;
        GlobalSettingsPage.ProjectRootSelectionRequested +=
            GlobalSettingsPage_ProjectRootSelectionRequested;
        GlobalSettingsPage.StorageScanRequested += GlobalSettingsPage_StorageScanRequested;
        GlobalSettingsPage.StorageCleanupRequested += GlobalSettingsPage_StorageCleanupRequested;
        GlobalSettingsPage.EnvironmentRescanRequested += GlobalSettingsPage_EnvironmentRescanRequested;
        GlobalSettingsPage.ThemeChanged += GlobalSettingsPage_ThemeChanged;
        GlobalSettingsPage.RunnerSelfTestRequested += GlobalSettingsPage_RunnerSelfTestRequested;
        GlobalSettingsPage.UpdateCheckRequested += GlobalSettingsPage_UpdateCheckRequested;
        GlobalSettingsPage.UpdateDownloadRequested += GlobalSettingsPage_UpdateDownloadRequested;
        GlobalSettingsPage.UpdateInstallRequested += GlobalSettingsPage_UpdateInstallRequested;
        GlobalSettingsPage.GiteeTokenSaveRequested += GlobalSettingsPage_GiteeTokenSaveRequested;
        GlobalSettingsPage.ReleaseActionRequested += GlobalSettingsPage_ReleaseActionRequested;
        AxToolsPage.ToolActionRequested += ToolPage_ToolActionRequested;
        FantasyToolsPage.ToolActionRequested += ToolPage_ToolActionRequested;
        GalExcleToolsPage.ToolActionRequested += ToolPage_ToolActionRequested;
        CrossingVoidZDToolPage.ToolActionRequested += ToolPage_ToolActionRequested;
        FantasyProjectPcPage.ToolActionRequested += ToolPage_ToolActionRequested;
        CrossingVoidPcPage.ToolActionRequested += ToolPage_ToolActionRequested;
        CrossingVoidAndroidPage.ToolActionRequested += ToolPage_ToolActionRequested;
        FantasyGamePage.ToolActionRequested += ToolPage_ToolActionRequested;
        FantasyAndroidPage.ToolActionRequested += ToolPage_ToolActionRequested;
        CrossingVoidGamePage.ToolActionRequested += ToolPage_ToolActionRequested;
        AxToolsPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        FantasyToolsPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        GalExcleToolsPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        CrossingVoidZDToolPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        FantasyProjectPcPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        CrossingVoidPcPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        CrossingVoidAndroidPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        FantasyGamePage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        FantasyAndroidPage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        CrossingVoidGamePage.ProjectDownloadRequested += ToolPage_ProjectDownloadRequested;
        AxToolsPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        AxToolsPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        FantasyToolsPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        FantasyToolsPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        GalExcleToolsPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        GalExcleToolsPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        CrossingVoidZDToolPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        CrossingVoidZDToolPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        FantasyProjectPcPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        FantasyProjectPcPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        CrossingVoidPcPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        CrossingVoidPcPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        CrossingVoidAndroidPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        FantasyGamePage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        FantasyAndroidPage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        CrossingVoidGamePage.ReleaseDownloadRequested += ToolPage_ReleaseDownloadRequested;
        CrossingVoidAndroidPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        FantasyGamePage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        FantasyAndroidPage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        CrossingVoidGamePage.DownloadLinkRequested += ToolPage_DownloadLinkRequested;
        CrossingVoidPcPage.GamePackageRootSelectionRequested += ToolPage_GamePackageRootSelectionRequested;
        CrossingVoidAndroidPage.GamePackageRootSelectionRequested += ToolPage_GamePackageRootSelectionRequested;
        CrossingVoidGamePage.GamePackageRootSelectionRequested += ToolPage_GamePackageRootSelectionRequested;
        viewModel.GlobalProgress.PropertyChanged += GlobalProgress_PropertyChanged;
        if (viewModel.TaskRunner is not null)
        {
            viewModel.TaskRunner.EventReceived += TaskRunner_EventReceived;
            viewModel.TaskRunner.OutputReceived += TaskRunner_OutputReceived;
        }

        AppVersionText.Text = GetInformationalVersion();
        WorkspacePathText.Text = $"整体项目位置：{viewModel.Settings.ProjectRootPath}";
        WorkspacePathText.Visibility = viewModel.Settings.ShowWorkspacePath
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyCustomTitleBar();
        ApplyWindowIcon();
        ApplyTheme(viewModel.Settings.ThemeMode);
        AppWindow.Resize(new SizeInt32(1500, 920));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        ShowPageByTag(viewModel.CurrentPageTag, playAnimation: false);
        viewModel.LogService.Write(Core.Models.LogKind.Info, "Ax工具箱已启动。");
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void ApplyCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AxTools.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private static string GetInformationalVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        UninitializeLogPanel();
        AppWindow.Closing -= AppWindow_Closing;
        _viewModel.GlobalProgress.PropertyChanged -= GlobalProgress_PropertyChanged;
        GlobalSettingsPage.RunnerSelfTestRequested -= GlobalSettingsPage_RunnerSelfTestRequested;
        GlobalSettingsPage.UpdateCheckRequested -= GlobalSettingsPage_UpdateCheckRequested;
        GlobalSettingsPage.UpdateDownloadRequested -= GlobalSettingsPage_UpdateDownloadRequested;
        GlobalSettingsPage.UpdateInstallRequested -= GlobalSettingsPage_UpdateInstallRequested;
        GlobalSettingsPage.GiteeTokenSaveRequested -= GlobalSettingsPage_GiteeTokenSaveRequested;
        GlobalSettingsPage.ReleaseActionRequested -= GlobalSettingsPage_ReleaseActionRequested;
        GlobalSettingsPage.ProjectRootSelectionRequested -=
            GlobalSettingsPage_ProjectRootSelectionRequested;
        GlobalSettingsPage.StorageScanRequested -= GlobalSettingsPage_StorageScanRequested;
        GlobalSettingsPage.StorageCleanupRequested -= GlobalSettingsPage_StorageCleanupRequested;
        GlobalSettingsPage.EnvironmentRescanRequested -= GlobalSettingsPage_EnvironmentRescanRequested;
        AxToolsPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        FantasyToolsPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        GalExcleToolsPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        CrossingVoidZDToolPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        FantasyProjectPcPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        CrossingVoidPcPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        CrossingVoidAndroidPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        FantasyGamePage.ToolActionRequested -= ToolPage_ToolActionRequested;
        FantasyAndroidPage.ToolActionRequested -= ToolPage_ToolActionRequested;
        CrossingVoidGamePage.ToolActionRequested -= ToolPage_ToolActionRequested;
        AxToolsPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        FantasyToolsPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        GalExcleToolsPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        CrossingVoidZDToolPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        FantasyProjectPcPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        CrossingVoidPcPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        CrossingVoidAndroidPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        FantasyGamePage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        FantasyAndroidPage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        CrossingVoidGamePage.ProjectDownloadRequested -= ToolPage_ProjectDownloadRequested;
        AxToolsPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        AxToolsPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        FantasyToolsPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        FantasyToolsPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        GalExcleToolsPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        GalExcleToolsPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        CrossingVoidZDToolPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        CrossingVoidZDToolPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        FantasyProjectPcPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        FantasyProjectPcPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        CrossingVoidPcPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        CrossingVoidPcPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        CrossingVoidAndroidPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        FantasyGamePage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        FantasyAndroidPage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        CrossingVoidGamePage.ReleaseDownloadRequested -= ToolPage_ReleaseDownloadRequested;
        CrossingVoidAndroidPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        FantasyGamePage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        FantasyAndroidPage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        CrossingVoidGamePage.DownloadLinkRequested -= ToolPage_DownloadLinkRequested;
        CrossingVoidPcPage.GamePackageRootSelectionRequested -= ToolPage_GamePackageRootSelectionRequested;
        CrossingVoidAndroidPage.GamePackageRootSelectionRequested -= ToolPage_GamePackageRootSelectionRequested;
        CrossingVoidGamePage.GamePackageRootSelectionRequested -= ToolPage_GamePackageRootSelectionRequested;
        if (_viewModel.TaskRunner is not null)
        {
            _viewModel.TaskRunner.EventReceived -= TaskRunner_EventReceived;
            _viewModel.TaskRunner.OutputReceived -= TaskRunner_OutputReceived;
        }

        await _viewModel.Settings.SaveAsync(CancellationToken.None);
    }

    private void AppWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_closeAfterHostedTaskStops)
        {
            _windowTaskCancellation.Cancel();
            return;
        }

        var runner = _viewModel.TaskRunner;
        var decision = runner?.RequestShutdown() ??
            AxTaskShutdownDecision.NoActiveTask;
        if (decision == AxTaskShutdownDecision.NoActiveTask)
        {
            _windowTaskCancellation.Cancel();
            if (_hostedTaskCoordinator.IsBusy)
            {
                args.Cancel = true;
                _activeOperationCancellation?.Cancel();
                _viewModel.GlobalProgress.MarkStopRequested();
                BeginCloseWhenHostedTaskStops();
            }
            return;
        }

        if (decision == AxTaskShutdownDecision.StopRequested)
        {
            args.Cancel = true;
            if (_activeHostedTaskKind == HostedTaskKind.RunnerSelfTest)
            {
                _viewModel.RunnerDiagnostics.MarkStopRequested();
            }

            _viewModel.GlobalProgress.MarkStopRequested();
            _viewModel.LogService.Write(
                Core.Models.LogKind.User,
                "关闭窗口前已请求停止当前任务。");
            BeginCloseWhenHostedTaskStops();
            return;
        }

        args.Cancel = true;
        const string message =
            "当前任务正在提交发布，处于不可停止阶段。为避免破坏远端发布状态，暂时不能关闭 AxTools。";
        _viewModel.GlobalProgress.SetCancellationMode(
            AxTaskCancellationMode.Locked,
            message);
        _viewModel.LogService.Write(
            Core.Models.LogKind.Warning,
            message);
        _ = ShowMessageDialogAsync("当前无法关闭", message);
    }
}
