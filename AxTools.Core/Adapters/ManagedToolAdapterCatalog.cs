using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public static class ManagedToolAdapterCatalog
{
    public static IReadOnlyList<IManagedToolAdapter> Create(
        string scriptsRoot,
        ToolchainSettings? toolchains = null) =>
    [
        new AxToolsAdapter(scriptsRoot),
        new FantasyToolsAdapter(scriptsRoot),
        new GalExcleToolsAdapter(scriptsRoot),
        new CrossingVoidZDToolAdapter(scriptsRoot),
        new FantasyProjectPcAdapter(scriptsRoot),
        new CrossingVoidPcAdapter(scriptsRoot),
        new CrossingVoidAndroidAdapter(scriptsRoot, toolchains ?? new ToolchainSettings())
        , new FantasyGameAdapter(scriptsRoot)
        , new FantasyAndroidAdapter(scriptsRoot)
        , new CrossingVoidGameAdapter(scriptsRoot)
    ];
}
