namespace AxTools.Core.Models;

public sealed record ManagedToolActionDefinition(
    ManagedToolAction Action,
    string DisplayName,
    ManagedToolActionSection Section,
    string IconGlyph,
    bool IsHeavy,
    bool RequiresVersion = false,
    bool RequiresConfirmation = false);
