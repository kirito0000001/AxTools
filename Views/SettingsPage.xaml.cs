using System.Reflection;
using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AxTools.Views;

public sealed class PathSelectionRequestedEventArgs(
    ToolPageViewModel tool,
    string pathKind) : EventArgs
{
    public ToolPageViewModel Tool { get; } = tool;

    public string PathKind { get; } = pathKind;
}

public sealed class RunnerSelfTestRequestedEventArgs(
    string scenario,
    string displayName) : EventArgs
{
    public string Scenario { get; } = scenario;

    public string DisplayName { get; } = displayName;
}

public sealed record ReleaseActionRequestedEventArgs(
    string Action,
    string Version,
    string Channel,
    string ReleaseNotes);

public sealed partial class SettingsPage : UserControl
{
    private bool _isSynchronizingTheme;
    private bool _developerReleaseVersionChecked;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += SettingsPage_DataContextChanged;
        AboutVersionText.Text = $"版本 {GetInformationalVersion()}";
    }

    public event EventHandler<PathSelectionRequestedEventArgs>? PathSelectionRequested;

    public event EventHandler? ProjectRootSelectionRequested;

    public event EventHandler? StorageScanRequested;

    public event EventHandler? StorageCleanupRequested;
    public event EventHandler? EnvironmentRescanRequested;

    public event EventHandler<ThemeMode>? ThemeChanged;

    public event EventHandler<RunnerSelfTestRequestedEventArgs>? RunnerSelfTestRequested;

    public event EventHandler? UpdateCheckRequested;
    public event EventHandler? UpdateDownloadRequested;
    public event EventHandler? UpdateInstallRequested;
    public event EventHandler<string>? GiteeTokenSaveRequested;
    public event EventHandler<ReleaseActionRequestedEventArgs>? ReleaseActionRequested;

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void SettingsPage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is not SettingsViewModel viewModel)
        {
            return;
        }

        _isSynchronizingTheme = true;
        ThemeModeComboBox.SelectedIndex = (int)viewModel.ThemeMode;
        _isSynchronizingTheme = false;
    }

    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingTheme || ViewModel is null || ThemeModeComboBox.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ThemeMode = (ThemeMode)ThemeModeComboBox.SelectedIndex;
        ThemeChanged?.Invoke(this, ViewModel.ThemeMode);
    }

    private void SelectPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolPageViewModel tool, Tag: string pathKind })
        {
            PathSelectionRequested?.Invoke(this, new PathSelectionRequestedEventArgs(tool, pathKind));
        }
    }

    private void ChooseProjectRootButton_Click(object sender, RoutedEventArgs e)
    {
        ProjectRootSelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ScanStorageButton_Click(object sender, RoutedEventArgs e)
    {
        StorageScanRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupStorageButton_Click(object sender, RoutedEventArgs e)
    {
        StorageCleanupRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RescanEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        EnvironmentRescanRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void UndoLogSettingKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel is null)
        {
            return;
        }

        args.Handled = await ViewModel.UndoLastLogSettingAsync();
    }

    private void RunSelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string scenario, Content: FrameworkElement content } &&
            content.Tag is string displayName)
        {
            RunnerSelfTestRequested?.Invoke(
                this,
                new RunnerSelfTestRequestedEventArgs(scenario, displayName));
        }
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);
    private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateDownloadRequested?.Invoke(this, EventArgs.Empty);
    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateInstallRequested?.Invoke(this, EventArgs.Empty);

    private void SaveGiteeTokenButton_Click(object sender, RoutedEventArgs e)
    {
        GiteeTokenSaveRequested?.Invoke(this, GiteeTokenPasswordBox.Password);
        GiteeTokenPasswordBox.Password = string.Empty;
    }

    private async void DeveloperReleasePanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_developerReleaseVersionChecked ||
            sender is not FrameworkElement { DataContext: DeveloperReleaseViewModel viewModel })
        {
            return;
        }

        _developerReleaseVersionChecked = true;
        await viewModel.CheckPublishedVersionAsync(CancellationToken.None);
    }

    private async void RefreshAxToolsPublishedVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeveloperReleaseViewModel viewModel })
        {
            await viewModel.CheckPublishedVersionAsync(CancellationToken.None);
        }
    }

    private async void EditAxToolsReleaseDescription_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || XamlRoot is null)
        {
            return;
        }

        var editor = new TextBox
        {
            AcceptsReturn = true,
            MinWidth = 620,
            MinHeight = 300,
            TextWrapping = TextWrapping.Wrap,
            Text = ViewModel.DeveloperRelease.ReleaseDescription
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "编辑 AxTools Release 介绍",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeveloperRelease.ReleaseDescription = editor.Text;
        }
    }

    private void ReleaseActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action } && ViewModel is { } viewModel)
        {
            ReleaseActionRequested?.Invoke(this, new ReleaseActionRequestedEventArgs(
                action,
                viewModel.DeveloperRelease.TargetVersion,
                viewModel.DeveloperRelease.Channel,
                viewModel.DeveloperRelease.ReleaseDescription));
        }
    }

    private static string GetInformationalVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
}
