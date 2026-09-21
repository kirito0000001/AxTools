using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 《生成碎片》的判定规则：把清单里的文件分进三组。
///
/// 为什么不按文件名写死：用户明确说过"之后会重新分配 chunk"，所以判定只看**清单里的路径**，
/// 不看 `pakchunkN` 这种具体名字 —— 只要还在 `Content/Paks` 里、还是 pak/ucas/utoc，就算 chunk。
///
/// 纯函数、不碰磁盘：真正落盘（拷 chunk、调 Bandizip 打包）由调用方做，
/// 这样规则可以先被测试钉住。
/// </summary>
public static class CrossingVoidFragmentPlanner
{
    private static readonly string[] ChunkExtensions = [".pak", ".ucas", ".utoc"];
    private static readonly string[] VideoExtensions = [".mp4"];

    private const string PaksSegment = "/Content/Paks/";
    private const string MoviesSegment = "/Content/Movies/";

    public static CrossingVoidFragmentKind Classify(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            return CrossingVoidFragmentKind.Remainder;
        }

        var extension = Path.GetExtension(normalized);

        if (normalized.Contains(PaksSegment, StringComparison.OrdinalIgnoreCase)
            && ChunkExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return CrossingVoidFragmentKind.Chunk;
        }

        if (normalized.Contains(MoviesSegment, StringComparison.OrdinalIgnoreCase)
            && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return CrossingVoidFragmentKind.Video;
        }

        return CrossingVoidFragmentKind.Remainder;
    }

    /// <summary>把清单里的 <c>files[]</c>（path + sizeBytes）分类成三组。</summary>
    public static CrossingVoidFragmentPlan Plan(IEnumerable<(string Path, long SizeBytes)> files)
    {
        var chunks = new List<CrossingVoidFragmentEntry>();
        var videos = new List<CrossingVoidFragmentEntry>();
        var remainder = new List<CrossingVoidFragmentEntry>();

        foreach (var (path, sizeBytes) in files)
        {
            var kind = Classify(path);
            var entry = new CrossingVoidFragmentEntry(path, sizeBytes, kind);
            switch (kind)
            {
                case CrossingVoidFragmentKind.Chunk:
                    chunks.Add(entry);
                    break;
                case CrossingVoidFragmentKind.Video:
                    videos.Add(entry);
                    break;
                default:
                    remainder.Add(entry);
                    break;
            }
        }

        return new CrossingVoidFragmentPlan(chunks, videos, remainder);
    }
}
