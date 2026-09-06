namespace AxTools.Core.Models;

public sealed record ManagedToolDescriptor(
    ManagedToolKey Key,
    string StableKey,
    string DisplayName,
    string NavigationTag,
    string Description);
