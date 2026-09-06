using CommunityToolkit.Mvvm.ComponentModel;
using AxTools.Core.Models;

namespace AxTools.Core.ViewModels;

public sealed class ToolActionItemViewModel : ObservableObject
{
    private bool _isEnabled;

    public ToolActionItemViewModel(ManagedToolActionDefinition definition)
    {
        Definition = definition;
    }

    public ManagedToolActionDefinition Definition { get; }

    public ManagedToolAction Action => Definition.Action;

    public string DisplayName => Definition.DisplayName;

    public string Description => ManagedToolActionDescriptionCatalog.GetDescription(Action);

    public string IconGlyph => Definition.IconGlyph;

    public bool RequiresConfirmation => Definition.RequiresConfirmation;

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }
}
