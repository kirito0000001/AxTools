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

/// <summary>按大小切片时的一个文件（没有 Kind —— 切片只看体积，不看它原来属于哪一组）。</summary>
public sealed record CrossingVoidFragmentFile(string Path, long SizeBytes);

/// <summary>
/// 按大小切片后的一片：一组文件打进同一个 zip，解压后仍是原来的目录树。
/// <see cref="ArchiveName"/> 形如 <c>零境交错-0.6.0-基础碎片001.zip</c>（前缀由调用方给，
/// 没有版本前缀时退回 <c>基础碎片001.zip</c>）。
/// </summary>
public sealed record CrossingVoidFragmentSlice(
    int Index,
    string ArchiveName,
    IReadOnlyList<CrossingVoidFragmentFile> Files,
    long SourceBytes);

/// <summary>
/// 一次《生成碎片》按大小切片的结果。
/// <see cref="OversizedFiles"/> 是单个文件本身就超过上限的情况：那种文件没法切开，
/// 只能自己占一片，界面要提醒用户。
/// </summary>
public sealed record CrossingVoidFragmentSizePlan(
    IReadOnlyList<CrossingVoidFragmentSlice> Slices,
    IReadOnlyList<string> OversizedFiles)
{
    public long TotalSourceBytes => Slices.Sum(slice => slice.SourceBytes);

    public int TotalCount => Slices.Sum(slice => slice.Files.Count);
}

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
