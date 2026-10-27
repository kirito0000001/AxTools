namespace AxTools.Core.Models;

/// <summary>
/// 「功能散页」里的一条暂存项：暂时没有正式位置、或逻辑还没复用的功能。
/// 这是给后续排期看的清单，不参与任何执行。
/// </summary>
public sealed record GameScratchItem(
    string Title,
    string Detail,
    string Status);
