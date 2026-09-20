using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

/// <summary>零境交错：空界幻境。dl 目录 `crossingvoid` / `crossingvoid-android`。</summary>
public sealed class CrossingVoidGameAdapter(string scriptsRoot) : GameUploadAdapter(
    ManagedToolKey.CrossingVoidGame,
    scriptsRoot,
    "CrossingVoidGame",
    "零境交错：空界幻境",
    "crossingvoid");
