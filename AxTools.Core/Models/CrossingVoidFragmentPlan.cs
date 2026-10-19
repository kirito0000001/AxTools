namespace AxTools.Core.Models;

/// <summary>参与切片的一个文件（PC 是磁盘文件，安卓是 OBB 条目 / APK / 旁车）。</summary>
public sealed record CrossingVoidFragmentFile(string Path, long SizeBytes);

/// <summary>
/// 按大小切片后的一片：一组文件打进同一个 zip，解压后仍是原来的目录树。
/// <see cref="ArchiveName"/> 形如 <c>零境V0.6PC基础碎片001.zip</c> 或
/// <c>零境V0.6安卓基础碎片001.zip</c>。
/// </summary>
public sealed record CrossingVoidFragmentSlice(
    int Index,
    string ArchiveName,
    IReadOnlyList<CrossingVoidFragmentFile> Files,
    long SourceBytes);

/// <summary>
/// 一次按大小切片的结果。
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
