using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

/// <summary>火影。dl 目录 `narutobp` / `narutobp-android`。</summary>
public sealed class NarutoGameAdapter(string scriptsRoot) : GameUploadAdapter(
    ManagedToolKey.NarutoGame,
    scriptsRoot,
    "NarutoGame",
    "火影",
    "narutobp");
