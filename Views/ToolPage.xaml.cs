using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AxTools.Core.Models;
using AxTools.Core.Services;
using System.Globalization;
using AxTools.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace AxTools.Views;

public sealed partial class ToolPage : UserControl
{
    private const double WidePublishingFieldsWidth = 720;
    private const double PreferredActionSlotWidth = 220;
    private bool _isDialogOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _serverStateTimer;
    private bool _publishModeSelected;

    public event EventHandler<ToolActionRequestedEventArgs>? ToolActionRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? ProjectDownloadRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? ReleaseDownloadRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? DownloadLinkRequested;

    public event EventHandler? GamePackageRootSelectionRequested;

    /// <summary>设置页里选择游戏项目目录（本体 / 安卓启动器）。</summary>
    public event EventHandler<ToolProjectPathSelectionRequestedEventArgs>? ProjectPathSelectionRequested;

    /// <summary>发布区点了服务端操作按钮；交给主窗口转给服务器页执行（复用旧实现）。</summary>
    public event EventHandler<ToolServerActionRequestedEventArgs>? ServerActionRequested;

    /// <summary>服务器管理卡片的动作（看门狗日志、日志与缓存清理等）。</summary>
    public event EventHandler<ToolServerManagementActionRequestedEventArgs>? ServerManagementActionRequested;

    /// <summary>实例行上的日志 / 启动 / 停止 / 重启。</summary>
    public event EventHandler<ToolInstanceActionRequestedEventArgs>? InstanceActionRequested;

    /// <summary>启动器打包：发布安卓启动器新版本。</summary>
    public event EventHandler<ToolLauncherPublishRequestedEventArgs>? LauncherPublishRequested;

    public ToolPage()
    {
        InitializeComponent();
        // 页面被切走（Visibility=Collapsed）时停掉自动刷新，别让三个游戏页各自每分钟打一次服务端。
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Collapsed)
            {
                _serverStateTimer?.Stop();
            }
        });
    }

    /// <summary>
    /// 首次进入时把页签切到「打包与发布」。
    /// 游戏页只有发布动作，默认的「开发版」面板是空的，不切过去等于点进来一片空白。
    /// 只生效一次：用户之后自己切回别的页签，不会被每次导航拽回来。
    /// </summary>
    internal void EnsurePublishMode()
    {
        if (_publishModeSelected || PublishSelectorItem.Visibility != Visibility.Visible)
        {
            return;
        }

        _publishModeSelected = true;
        ModeSelector.SelectedItem = PublishSelectorItem;
    }

    private void GamePackageRootBrowse_Click(object sender, RoutedEventArgs e) =>
        GamePackageRootSelectionRequested?.Invoke(this, EventArgs.Empty);

    private void ProjectPathBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string projectId } &&
            DataContext is ToolPageViewModel viewModel)
        {
            ProjectPathSelectionRequested?.Invoke(
                this,
                new ToolProjectPathSelectionRequestedEventArgs(viewModel, projectId));
        }
    }

    private void InstanceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerInstanceViewModel instance, Tag: string command } &&
            DataContext is ToolPageViewModel viewModel)
        {
            InstanceActionRequested?.Invoke(
                this,
                new ToolInstanceActionRequestedEventArgs(viewModel, instance.Name, command));
        }
    }

    private void ServerManagementAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerActionCommand command } &&
            DataContext is ToolPageViewModel viewModel)
        {
            ServerManagementActionRequested?.Invoke(
                this,
                new ToolServerManagementActionRequestedEventArgs(viewModel, command.Key));
        }
    }

    /// <summary>体检结果：一行一条，通过/问题都列出来，只给关闭按钮。</summary>
    private async Task ShowHealthReportAsync(GamePackageHealthReport report)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = report.Summary,
            Content = new TextBox
            {
                Text = string.Join(Environment.NewLine, report.Lines),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = null,
                MaxHeight = 420,
                MinWidth = 520
            },
            CloseButtonText = "关闭"
        };
        await DialogGate.TryShowAsync(dialog);
    }

    private void ServerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerActionCommand command } &&
            DataContext is ToolPageViewModel viewModel)
        {
            ServerActionRequested?.Invoke(
                this,
                new ToolServerActionRequestedEventArgs(viewModel, command.Key));
        }
    }

    /// <summary>
    /// 开发版卡片上的动作。已接入的只有「获取项目链接」与「从 UProject 启动」，
    /// 其余先明确回报“还没接入”，不做假成功。
    /// </summary>
    private void GameProjectAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameDevActionItem action } ||
            DataContext is not ToolPageViewModel viewModel)
        {
            return;
        }

        switch (action.Key)
        {
            case "copy-repo-link":
                CopyRepositoryLink(viewModel, action.ProjectId);
                break;
            case "launch-uproject":
                viewModel.ReportActionMessage(
                    viewModel.TryLaunchUProject(action.ProjectId, out var message)
                        ? message
                        : $"启动失败：{message}");
                break;
            case "launch-rider":
                viewModel.TryLaunchRider(action.ProjectId, out _);
                break;
            case "install-from-github":
                _ = ShowInstallDialogAsync(viewModel, action.ProjectId);
                break;
            case "check-upstream":
                _ = viewModel.PullProjectAsync(action.ProjectId, CancellationToken.None);
                break;
            default:
                viewModel.ReportActionMessage($"「{action.Title}」还没接入。");
                break;
        }
    }

    /// <summary>
    /// 从 GitHub 安装的确认弹窗：参考启动器的下载前弹窗——路径 + 更改 + 可用空间 + 继续。
    /// git clone 没有可靠的预估体积，所以只报可用空间，不编“所需空间”。
    /// </summary>
    private async Task ShowInstallDialogAsync(ToolPageViewModel viewModel, string projectId)
    {
        var project = viewModel.GameProjects.FirstOrDefault(item => item.Id == projectId);
        var cardTitle = viewModel.GameProjectCards
            .FirstOrDefault(card => card.ProjectId == projectId)?.Title
            ?? "从 GitHub 安装";
        var pathBox = new TextBox
        {
            Header = "安装到",
            Text = project?.Path ?? string.Empty,
            PlaceholderText = "选择或输入安装目录"
        };
        var changeButton = new Button
        {
            Content = "更改",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var spaceText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = "会完整拉取仓库，请预留足够空间；安装完成后会自动同步设置里的本体路径。",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        void RefreshSpace()
        {
            var path = pathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                spaceText.Text = "可用空间：请先选择路径";
                return;
            }

            spaceText.Text = ToolPageViewModel.TryGetAvailableFreeBytes(path, out var bytes, out var message)
                ? $"可用空间：{FormatBytes(bytes)}"
                : $"可用空间：无法检测（{message}）";
        }

        changeButton.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync();
            if (folder is not null)
            {
                pathBox.Text = folder;
                RefreshSpace();
            }
        };
        pathBox.TextChanged += (_, _) => RefreshSpace();

        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(changeButton, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(changeButton);

        var content = new StackPanel { Spacing = 12, MinWidth = 520 };
        content.Children.Add(pathRow);
        content.Children.Add(spaceText);
        content.Children.Add(hint);
        RefreshSpace();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"从 GitHub 安装 · {cardTitle}",
            Content = content,
            PrimaryButtonText = "继续安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await DialogGate.TryShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        await viewModel.InstallProjectAsync(
            projectId,
            pathBox.Text?.Trim() ?? string.Empty,
            CancellationToken.None);
    }

    private static async Task<string?> PickFolderAsync()
    {
        if (App.ShellWindow is null)
        {
            return null;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.ShellWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024 / 1024:N1} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N1} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} B"
    };

    private static void CopyRepositoryLink(ToolPageViewModel viewModel, string projectId)
    {
        var repositoryUrl = viewModel.RepositoryUrlFor(projectId);
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            viewModel.ReportActionMessage("这个项目的仓库地址还没配置。");
            return;
        }

        var package = new DataPackage();
        package.SetText(repositoryUrl);
        Clipboard.SetContent(package);
        viewModel.ReportActionMessage($"已复制到剪贴板：{repositoryUrl}");
    }

    private async void RefreshLauncherVersion_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ToolPageViewModel viewModel)
        {
            await viewModel.CheckLauncherPublishedVersionAsync(CancellationToken.None);
        }
    }

    private async void RefreshGameVersion_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ToolPageViewModel viewModel)
        {
            await viewModel.CheckGamePublishedVersionAsync(CancellationToken.None);
        }
    }

    private async Task ShowReleaseDescriptionEditorAsync(bool isGame)
    {
        if (_isDialogOpen || DataContext is not ToolPageViewModel viewModel || XamlRoot is null)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var editor = new TextBox
            {
                AcceptsReturn = true,
                MinWidth = 620,
                MinHeight = 300,
                TextWrapping = TextWrapping.Wrap,
                Text = isGame
                    ? viewModel.GameReleaseDescription
                    : viewModel.LauncherReleaseDescription
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = isGame ? "编辑游戏 Release 介绍" : "编辑启动器 Release 介绍",
                Content = editor,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await DialogGate.TryShowAsync(dialog) == ContentDialogResult.Primary)
            {
                if (isGame)
                {
                    viewModel.GameReleaseDescription = editor.Text;
                }
                else
                {
                    viewModel.LauncherReleaseDescription = editor.Text;
                }
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    /// <summary>本体打包卡片上的按钮：先按平台切好，再交给原发布管道。</summary>
    private async void PublishCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerActionCommand command, Tag: string platform } &&
            DataContext is ToolPageViewModel viewModel)
        {
            viewModel.SetPublishPlatform(platform);

            if (command.Key == "check")
            {
                var report = await viewModel.CheckGamePackageHealthAsync(
                    string.Equals(platform, "Android", StringComparison.Ordinal),
                    CancellationToken.None);
                await ShowHealthReportAsync(report);
                return;
            }

            if (ToolPageViewModel.ResolvePublishCommand(command.Key) is not { } action)
            {
                return;
            }

            var item = viewModel.AllActions.FirstOrDefault(candidate => candidate.Action == action);
            if (item is null)
            {
                return;
            }

            ToolActionRequested?.Invoke(this, new ToolActionRequestedEventArgs(viewModel, item));
        }
    }

    private void ToolAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolActionItemViewModel action } &&
            DataContext is ToolPageViewModel viewModel)
        {
            // 第三片按钮带 Tag=Windows、第四片带 Tag=Android：点哪个就按哪个平台发。
            if (sender is Button { Tag: string platform } && !string.IsNullOrWhiteSpace(platform))
            {
                viewModel.SetPublishPlatform(platform);
            }

            CommitCurrentVersionEditors(viewModel, action.Action);
            if (action.Action == ManagedToolAction.DownloadSource)
            {
                ProjectDownloadRequested?.Invoke(this, new ToolDownloadRequestedEventArgs(viewModel, action));
                return;
            }
            if (action.Action == ManagedToolAction.DownloadRelease)
            {
                ReleaseDownloadRequested?.Invoke(this, new ToolDownloadRequestedEventArgs(viewModel, action));
                return;
            }
            if (action.Action == ManagedToolAction.GetDownloadLink)
            {
                DownloadLinkRequested?.Invoke(this, new ToolDownloadRequestedEventArgs(viewModel, action));
                return;
            }
            if (action.Action == ManagedToolAction.EditLauncherReleaseNotes)
            {
                _ = ShowReleaseDescriptionEditorAsync(isGame: false);
                return;
            }
            if (action.Action == ManagedToolAction.EditGameReleaseNotes)
            {
                _ = ShowReleaseDescriptionEditorAsync(isGame: true);
                return;
            }
            ToolActionRequested?.Invoke(
                this,
                new ToolActionRequestedEventArgs(viewModel, action));
        }
    }

    private void CommitCurrentVersionEditors(
        ToolPageViewModel viewModel,
        ManagedToolAction action)
    {
        if (action is ManagedToolAction.CheckGamePackage or
            ManagedToolAction.PublishGameVersion or
            ManagedToolAction.PublishGameVersionToGitHub or
            ManagedToolAction.PublishGamePatch or
            ManagedToolAction.GenerateGamePatchFragments or
            ManagedToolAction.RebuildGameDownloadRepo)
        {
            // 游戏版本以包内 Version.json 为准，界面不再提供手写入口。
            viewModel.RefreshGamePackageBuildInfo();
            return;
        }

        if (action is ManagedToolAction.PackageStable or
            ManagedToolAction.PackageBeta or
            ManagedToolAction.BuildLauncherPackage or
            ManagedToolAction.ValidatePackage or
            ManagedToolAction.UploadDryRun or
            ManagedToolAction.Upload or
            ManagedToolAction.PublishDryRun or
            ManagedToolAction.Publish)
        {
            viewModel.PublishVersion = ComposeVersion(
                LauncherMajorVersionNumberBox,
                LauncherFeatureVersionNumberBox,
                LauncherBugFixVersionNumberBox);
        }
    }

    private static string ComposeVersion(NumberBox major, NumberBox feature, NumberBox bugFix) =>
        new ThreePartVersion(
            ReadVersionPart(major),
            ReadVersionPart(feature),
            ReadVersionPart(bugFix)).ToString();

    private static int ReadVersionPart(NumberBox numberBox) =>
        int.TryParse(numberBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var typedValue)
            ? Math.Max(0, typedValue)
            : NormalizeVersionPart(numberBox.Value);

    private static int NormalizeVersionPart(double value) =>
        double.IsFinite(value) ? Math.Max(0, (int)Math.Round(value)) : 0;

    private void ToolPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useWideFields = e.NewSize.Width >= WidePublishingFieldsWidth;
        UpdatePublishingFieldLayout(LauncherPublishingFields, LauncherVersionField, useWideFields);
        UpdatePublishingFieldLayout(GamePublishingFields, GameVersionField, useWideFields);
    }

    private static void UpdatePublishingFieldLayout(
        Grid fields,
        FrameworkElement secondField,
        bool useWideFields)
    {
        fields.ColumnSpacing = useWideFields ? 12 : 0;
        Grid.SetColumn(secondField, useWideFields ? 1 : 0);
        // 宽屏时版本号跟平台并排（游戏通道已经撤掉，两个字段排一行）；窄屏时各自占一行。
        Grid.SetRow(secondField, string.Equals(secondField.Name, "GameVersionField", StringComparison.Ordinal)
            ? (useWideFields ? 0 : 1)
            : (useWideFields ? 0 : 1));
        secondField.Margin = useWideFields
            ? new Thickness(0)
            : new Thickness(0, 12, 0, 0);
    }

    private void ActionItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ItemsControl itemsControl)
        {
            UpdateActionItemsWidth(itemsControl, e.NewSize.Width);
        }
    }

    private void ActionItemsControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl itemsControl)
        {
            UpdateActionItemsWidth(itemsControl, itemsControl.ActualWidth);
        }
    }

    private static void UpdateActionItemsWidth(ItemsControl itemsControl, double availableWidth)
    {
        if (itemsControl.ItemsPanelRoot is not ItemsWrapGrid panel || availableWidth <= 0)
        {
            return;
        }

        var columnCount = Math.Max(1, (int)Math.Floor(availableWidth / PreferredActionSlotWidth));
        panel.ItemWidth = Math.Floor(availableWidth / columnCount);
    }

    /// <summary>
    /// 把整页当成"游戏包发布"面板嵌进「服务器」页的游戏页签（零境交错 / 火影BP / 幻杀）。
    ///
    /// 这三个游戏工具本来就只有发布动作（检查环境 + 发布新版本 + 重整仓库），所以嵌进去以后
    /// 不需要模式选择器；标题 / 描述由服务器那边的卡片提供，这里去掉自己的一层页头和一层
    /// 卡片边框，只留状态、字段和按钮。动作、进度、日志、确认弹窗全部照旧 —— 事件仍然接在
    /// MainWindow 上，没有第二份执行逻辑。
    /// </summary>
    internal void HostAsGamePublishPanel()
    {
        PageTitleText.Visibility = Visibility.Collapsed;
        PageDescriptionText.Visibility = Visibility.Collapsed;
        GamePublishingHeader.Visibility = Visibility.Collapsed;

        ModeSelector.Visibility = Visibility.Collapsed;
        DevelopmentPanel.Visibility = Visibility.Collapsed;
        FragmentSelectorItem.Visibility = Visibility.Collapsed;
        FragmentPanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;

        // 外面已经套了一层卡片（服务器页的 CreatePanel），这里不要再叠一层边框和底色。
        PageScrollViewer.Padding = new Thickness(0);
        PageContentGrid.Margin = new Thickness(0);
        PublishPanel.Style = null;
        PublishPanel.Padding = new Thickness(0);
        PublishPanel.BorderThickness = new Thickness(0);
        PublishPanel.Background = null;
        PublishPanel.Visibility = DataContext is ToolPageViewModel { HasPublishActions: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        Visibility = Visibility.Visible;
    }

    private async void ModeSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        var selectedItem = sender.SelectedItem;

        // 顶部「配置状态」跟着页签走：开发版查项目，打包与发布查游戏包目录。
        var section = selectedItem == PublishSelectorItem ? ToolPageSection.Publish
            : selectedItem == ServerSelectorItem ? ToolPageSection.Server
            : selectedItem == LauncherSelectorItem ? ToolPageSection.Launcher
            : selectedItem == FragmentSelectorItem ? ToolPageSection.Fragments
            : selectedItem == StorageSelectorItem ? ToolPageSection.Storage
            : selectedItem == SettingsSelectorItem ? ToolPageSection.Settings
            : ToolPageSection.Development;
        if (DataContext is ToolPageViewModel sectionViewModel)
        {
            sectionViewModel.SelectSection(section);
        }

        DevelopmentPanel.Visibility = selectedItem == DevelopmentSelectorItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        PublishPanel.Visibility = selectedItem == PublishSelectorItem &&
            DataContext is ToolPageViewModel { HasPublishActions: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        LauncherPanel.Visibility = selectedItem == LauncherSelectorItem &&
            DataContext is ToolPageViewModel { HasLauncherPackaging: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        ServerPanel.Visibility = selectedItem == ServerSelectorItem &&
            DataContext is ToolPageViewModel { IsGameTool: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        FragmentPanel.Visibility = selectedItem == FragmentSelectorItem &&
            DataContext is ToolPageViewModel { HasFragmentGeneration: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        StoragePanel.Visibility = selectedItem == StorageSelectorItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPanel.Visibility = selectedItem == SettingsSelectorItem &&
            DataContext is ToolPageViewModel { IsGameTool: true }
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (selectedItem == DevelopmentSelectorItem && DataContext is ToolPageViewModel developmentViewModel)
        {
            // 进开发版时重新探一遍项目目录，别让启动后新建的目录一直显示“缺失”。
            developmentViewModel.RefreshGameProjects();
        }

        if (selectedItem == PublishSelectorItem &&
            DataContext is ToolPageViewModel publishViewModel)
        {
            await publishViewModel.CheckLauncherPublishedVersionAsync(CancellationToken.None);
            await publishViewModel.CheckGamePublishedVersionAsync(CancellationToken.None);
        }

        if (selectedItem == FragmentSelectorItem &&
            DataContext is ToolPageViewModel fragmentViewModel)
        {
            // 碎片页的"源文件"复用服务器页那份游戏包目录，隔了一页，进来时重算一次。
            fragmentViewModel.RefreshFragmentBindings();
        }

        // 服务端状态：进入发布分区立刻读一次，之后每分钟自动刷新；离开就停表。
        if (selectedItem == ServerSelectorItem &&
            DataContext is ToolPageViewModel { IsGameTool: true } publishServerViewModel)
        {
            EnsureServerStateTimer();
            _serverStateTimer?.Start();
            await publishServerViewModel.RefreshServerStateAsync(CancellationToken.None);
        }
        else
        {
            _serverStateTimer?.Stop();
        }
    }

    private async void RefreshLauncherPackaging_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ToolPageViewModel viewModel)
        {
            await viewModel.RefreshLauncherPackagingAsync(CancellationToken.None);
        }
    }

    private void PublishLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ToolPageViewModel { AndroidLauncherTool: { } launcher })
        {
            LauncherPublishRequested?.Invoke(this, new ToolLauncherPublishRequestedEventArgs(launcher));
        }
    }

    /// <summary>每分钟自动读一次服务端状态；和「立即刷新」走同一条路径。</summary>
    private void EnsureServerStateTimer()
    {
        if (_serverStateTimer is not null)
        {
            return;
        }

        _serverStateTimer = DispatcherQueue.CreateTimer();
        _serverStateTimer.Interval = TimeSpan.FromSeconds(15);
        _serverStateTimer.IsRepeating = true;
        _serverStateTimer.Tick += async (_, _) =>
        {
            if (DataContext is ToolPageViewModel viewModel)
            {
                await viewModel.RefreshServerStateAsync(CancellationToken.None);
            }
        };
    }
}

public sealed class ToolDownloadRequestedEventArgs(
    ToolPageViewModel viewModel,
    ToolActionItemViewModel action) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public ToolActionItemViewModel Action { get; } = action;
}

public sealed class ToolActionRequestedEventArgs(
    ToolPageViewModel viewModel,
    ToolActionItemViewModel action) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public ToolActionItemViewModel Action { get; } = action;
}

/// <summary>设置页里点了某个项目路径的「浏览」：projectId 为 client / android。</summary>
public sealed class ToolProjectPathSelectionRequestedEventArgs(
    ToolPageViewModel viewModel,
    string projectId) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public string ProjectId { get; } = projectId;
}

/// <summary>发布区点了服务端操作按钮：actionKey 与 ServerActionCommand.Key 对应。</summary>
public sealed class ToolServerActionRequestedEventArgs(
    ToolPageViewModel viewModel,
    string actionKey) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public string ActionKey { get; } = actionKey;
}

/// <summary>服务器管理卡片点了动作：actionKey 与 ServerActionItem.Key 对应。</summary>
public sealed class ToolServerManagementActionRequestedEventArgs(
    ToolPageViewModel viewModel,
    string actionKey) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public string ActionKey { get; } = actionKey;
}

/// <summary>实例行动作：instanceName 对应 ServerInstanceViewModel.Name，command 为 log/start/stop/restart。</summary>
public sealed class ToolInstanceActionRequestedEventArgs(
    ToolPageViewModel viewModel,
    string instanceName,
    string command) : EventArgs
{
    public ToolPageViewModel ViewModel { get; } = viewModel;

    public string InstanceName { get; } = instanceName;

    public string Command { get; } = command;
}

/// <summary>启动器打包：要执行发布的是安卓启动器工具本身。</summary>
public sealed class ToolLauncherPublishRequestedEventArgs(ToolPageViewModel launcherViewModel) : EventArgs
{
    public ToolPageViewModel LauncherViewModel { get; } = launcherViewModel;
}
