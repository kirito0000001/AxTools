using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public interface IManagedToolAdapter
{
    ManagedToolKey Key { get; }

    IReadOnlyList<ManagedToolActionDefinition> Actions { get; }

    ManagedToolValidationResult Validate(ManagedToolPaths paths);

    AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths);
}
