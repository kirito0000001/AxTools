using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 《生成碎片》的判定规则，有两条路：
///   · <see cref="PlanBySize"/>：按体积切片（PC 用，默认 1 GiB 一片）；
///   · <see cref="Plan"/>：按类型分三组（安卓暂时用）。
///
/// 为什么不按文件名写死：用户明确说过"之后会重新分配 chunk"，所以三组判定只看**路径**，
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

    /// <summary>按大小切片时每片的默认上限：1024 MiB（用户拍板的口径，按压缩前原始体积算）。</summary>
    public const long DefaultSliceBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// 传给 Bandizip 的命令行预算。Windows 上限 32767，这里留出余量；
    /// 分组阶段就把它算进去，避免压到一半才发现某一篇命令行太长。
    /// </summary>
    private const int ArchiveCommandLineBudget = 28000;

    /// <summary>固定开关（c -fmt:zip -l:5 -r -y + 压缩包路径）的粗略长度。</summary>
    private const int ArchiveFixedArgumentLength = 512;

    /// <summary>
    /// 基础碎片的文件名前缀：<c>零境V0.6</c>（后面直接接 <c>基础碎片001</c>，不加分隔符）。
    ///
    /// 只取主/次版本：0.0.x 是补丁，补丁之后单独出包，不混进基础碎片；
    /// 名字里也不放短横线/空格 —— 有些平台对文件名挑字符，<c>零境V0.6基础碎片001.zip</c> 最稳。
    /// </summary>
    public static string BuildArchiveNamePrefix(string version)
    {
        var normalized = version?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return "零境V";
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "零境V";
        }

        var display = segments.Length >= 2
            ? $"{segments[0]}.{segments[1]}"
            : segments[0];
        return $"零境V{display}";
    }

    /// <summary>
    /// 按大小切片：把文件按路径排序后顺序装箱，装满一片（默认 1 GiB）就换下一片。
    ///
    /// 为什么按路径顺序而不是"大文件优先"：同一份产物重复生成时结果必须稳定、可对拍；
    /// 大文件优先虽然片数更少，但顺序会随文件大小变化而变，排查时对不上。
    ///
    /// 单个文件超过上限时不切开（没法切），它自己占一片，并记进 <c>OversizedFiles</c>。
    /// </summary>
    /// <param name="archiveNamePrefix">
    /// 压缩包名前缀，PC 传 <c>零境交错-0.6.0-</c>；空串时就是 <c>基础碎片001.zip</c>。
    /// 前缀里的非法文件名字符会被替换成 <c>-</c>。
    /// </param>
    public static CrossingVoidFragmentSizePlan PlanBySize(
        IEnumerable<(string Path, long SizeBytes)> files,
        long maxSliceBytes = DefaultSliceBytes,
        string archiveNamePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(files);
        if (maxSliceBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSliceBytes), "单片上限必须大于 0。");
        }

        var safePrefix = SanitizeArchiveNamePrefix(archiveNamePrefix);
        var sorted = files
            .Select(file => new CrossingVoidFragmentFile(
                file.Path.Replace('\\', '/').Trim(),
                Math.Max(0, file.SizeBytes)))
            .Where(file => file.Path.Length > 0)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        var slices = new List<CrossingVoidFragmentSlice>();
        var oversized = new List<string>();
        var current = new List<CrossingVoidFragmentFile>();
        long currentBytes = 0;
        var currentArgumentLength = ArchiveFixedArgumentLength;

        foreach (var file in sorted)
        {
            var argumentLength = file.Path.Length + 3;
            if (current.Count > 0 &&
                (currentBytes + file.SizeBytes > maxSliceBytes ||
                 currentArgumentLength + argumentLength > ArchiveCommandLineBudget))
            {
                AppendSlice(slices, safePrefix, current, currentBytes);
                current = [];
                currentBytes = 0;
                currentArgumentLength = ArchiveFixedArgumentLength;
            }

            current.Add(file);
            currentBytes += file.SizeBytes;
            currentArgumentLength += argumentLength;
            if (file.SizeBytes > maxSliceBytes)
            {
                oversized.Add(file.Path);
            }
        }

        if (current.Count > 0)
        {
            AppendSlice(slices, safePrefix, current, currentBytes);
        }

        return new CrossingVoidFragmentSizePlan(slices, oversized);
    }

    private static void AppendSlice(
        List<CrossingVoidFragmentSlice> slices,
        string archiveNamePrefix,
        List<CrossingVoidFragmentFile> files,
        long sourceBytes)
    {
        var index = slices.Count + 1;
        slices.Add(new CrossingVoidFragmentSlice(
            index,
            $"{archiveNamePrefix}基础碎片{index:000}.zip",
            files.ToArray(),
            sourceBytes));
    }

    private static string SanitizeArchiveNamePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(prefix
            .Select(character =>
                invalid.Contains(character) || character is '/' or '\\' or ' ' ? '-' : character)
            .ToArray());
    }

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
