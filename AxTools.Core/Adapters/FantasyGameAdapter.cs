using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class FantasyGameAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.FantasyGame,
    scriptsRoot,
    "FantasyGame",
    [Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false)])
{
    protected override string GetDisplayName() => "幻杀";
}
