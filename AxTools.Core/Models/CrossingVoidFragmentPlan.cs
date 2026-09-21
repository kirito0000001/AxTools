namespace AxTools.Core.Models;

/// <summary>碎片分发时一个文件属于哪一组。</summary>
public enum CrossingVoidFragmentKind
{
    /// <summary>`Content/Paks` 里的 pak / ucas / utoc：走散件，保持目录结构。</summary>
    Chunk,

    /// <summary>`Content/Movies` 里的影片：每个单独压一个包（避免网盘把它当视频转码、QQ 群拒收视频）。</summary>
    Video,

    /// <summary>其余全部（Engine、游戏本体、exe、dll、json、txt…）：打成一个包，内部保持相对路径。</summary>
    Remainder,
}

/// <summary>参与分发的单个文件（路径取清单里的相对路径，带目录结构）。</summary>
public sealed record CrossingVoidFragmentEntry(string Path, long SizeBytes, CrossingVoidFragmentKind Kind);

/// <summary>一次《生成碎片》的分类结果：三组各有哪些文件、各占多少字节。</summary>
public sealed record CrossingVoidFragmentPlan(
    IReadOnlyList<CrossingVoidFragmentEntry> Chunks,
    IReadOnlyList<CrossingVoidFragmentEntry> Videos,
    IReadOnlyList<CrossingVoidFragmentEntry> Remainder)
{
    public long ChunkBytes => Chunks.Sum(file => file.SizeBytes);

    public long VideoBytes => Videos.Sum(file => file.SizeBytes);

    public long RemainderBytes => Remainder.Sum(file => file.SizeBytes);

    public long TotalBytes => ChunkBytes + VideoBytes + RemainderBytes;

    public int TotalCount => Chunks.Count + Videos.Count + Remainder.Count;
}
