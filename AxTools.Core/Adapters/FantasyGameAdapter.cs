using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

/// <summary>幻杀。dl 目录 `fantasyproject` / `fantasyproject-android`。</summary>
public sealed class FantasyGameAdapter(string scriptsRoot) : GameUploadAdapter(
    ManagedToolKey.FantasyGame,
    scriptsRoot,
    "FantasyGame",
    "幻杀",
    "fantasyproject");
