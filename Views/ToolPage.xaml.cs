using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AxTools.Core.Models;
using AxTools.Core.Services;
using System.Globalization;
using AxTools.Core.ViewModels;

namespace AxTools.Views;

public sealed partial class ToolPage : UserControl
{
    private const double WidePublishingFieldsWidth = 720;
    private const double PreferredActionSlotWidth = 220;
    private bool _isActionHelpOpen;

    public event EventHandler<ToolActionRequestedEventArgs>? ToolActionRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? ProjectDownloadRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? ReleaseDownloadRequested;

    public event EventHandler<ToolDownloadRequestedEventArgs>? DownloadLinkRequested;

    public event EventHandler? GamePackageRootSelectionRequested;

    public ToolPage()
    {
        InitializeComponent();
    }

    private void GamePackageRootBrowse_Click(object sender, RoutedEventArgs e) =>
        GamePackageRootSelectionRequested?.Invoke(this, EventArgs.Empty);

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
        if (_isActionHelpOpen || DataContext is not ToolPageViewModel viewModel || XamlRoot is null)
        {
            return;
        }

        _isActionHelpOpen = true;
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
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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
            _isActionHelpOpen = false;
        }
    }

    private void ToolAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolActionItemViewModel action } &&
            DataContext is ToolPageViewModel viewModel)
        {
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
        if (action is ManagedToolAction.BuildGameChunks or
            ManagedToolAction.UploadGameChunks or
            ManagedToolAction.PublishGamePackage)
        {
            viewModel.GamePublishVersion = ComposeVersion(
                GameMajorVersionNumberBox,
                GameFeatureVersionNumberBox,
                GameBugFixVersionNumberBox);
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
        Grid.SetRow(secondField, string.Equals(secondField.Name, "GameVersionField", StringComparison.Ordinal)
            ? (useWideFields ? 1 : 2)
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

    private async void ShowActionHelp_Click(object sender, RoutedEventArgs e)
    {
        if (_isActionHelpOpen || DataContext is not ToolPageViewModel viewModel || XamlRoot is null)
        {
            return;
        }

        _isActionHelpOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock
            {
                Text = "演练只执行本地准备、检查和流程模拟，不上传文件，也不修改远端版本、Release 或清单。",
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var group in viewModel.AllActions.GroupBy(action => action.Definition.Section))
            {
                content.Children.Add(new TextBlock
                {
                    Text = GetSectionTitle(group.Key),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                foreach (var action in group)
                {
                    var actionContent = new StackPanel { Spacing = 2 };
                    actionContent.Children.Add(new TextBlock
                    {
                        Text = action.DisplayName,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                    actionContent.Children.Add(new TextBlock
                    {
                        Text = action.Description,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    });
                    content.Children.Add(actionContent);
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = $"{viewModel.DisplayName} · 功能说明",
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isActionHelpOpen = false;
        }
    }

    private static string GetSectionTitle(ManagedToolActionSection section) => section switch
    {
        ManagedToolActionSection.Development => "开发版",
        ManagedToolActionSection.Release => "正式版",
        ManagedToolActionSection.Publish => "打包与发布",
        _ => section.ToString()
    };

    private async void ModeSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        var selectedItem = sender.SelectedItem;
        DevelopmentPanel.Visibility = selectedItem == DevelopmentSelectorItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReleasePanel.Visibility = selectedItem == ReleaseSelectorItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        PublishPanel.Visibility = selectedItem == PublishSelectorItem &&
            DataContext is ToolPageViewModel { HasPublishActions: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        StoragePanel.Visibility = selectedItem == StorageSelectorItem
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (selectedItem == PublishSelectorItem &&
            DataContext is ToolPageViewModel publishViewModel)
        {
            await publishViewModel.CheckLauncherPublishedVersionAsync(CancellationToken.None);
            await publishViewModel.CheckGamePublishedVersionAsync(CancellationToken.None);
        }
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
