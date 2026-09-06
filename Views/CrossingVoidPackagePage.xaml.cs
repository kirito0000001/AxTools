using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AxTools.Views;

public sealed partial class CrossingVoidPackagePage : UserControl
{
    public CrossingVoidPackagePage()
    {
        InitializeComponent();
    }

    public event EventHandler<CrossingVoidPackagePathRequestedEventArgs>? PathSelectionRequested;

    public event EventHandler? InspectionRequested;

    public event EventHandler? GenerationRequested;

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pathKind })
        {
            PathSelectionRequested?.Invoke(
                this,
                new CrossingVoidPackagePathRequestedEventArgs(pathKind));
        }
    }

    private void InspectButton_Click(object sender, RoutedEventArgs e) =>
        InspectionRequested?.Invoke(this, EventArgs.Empty);

    private void GenerateButton_Click(object sender, RoutedEventArgs e) =>
        GenerationRequested?.Invoke(this, EventArgs.Empty);
}

public sealed record CrossingVoidPackagePathRequestedEventArgs(string PathKind);
