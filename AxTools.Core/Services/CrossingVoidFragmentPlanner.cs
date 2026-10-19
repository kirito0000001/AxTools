using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 《生成碎片》的切片规则：把文件按路径排序后顺序装箱，每片不超过上限。
///
/// 纯函数、不碰磁盘：PC 的磁盘文件和安卓的 OBB 条目都先变成 (path, size)，
/// 规则先被测试钉住，落盘由各自的执行器做。
/// </summary>
public static class CrossingVoidFragmentPlanner
{
    /// <summary>按大小切片时每片的默认上限：1024 MiB（按压缩前原始体积算）。</summary>
    public const long DefaultSliceBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// 传给 Bandizip 的命令行预算。Windows 上限 32767，这里留出余量；
    /// 分组阶段就把它算进去，避免压到一半才发现某一篇命令行太长。
    /// </summary>
    private const int ArchiveCommandLineBudget = 28000;

    /// <summary>固定开关（c -fmt:zip -l:5 -r -y + 压缩包路径）的粗略长度。</summary>
    private const int ArchiveFixedArgumentLength = 512;

    /// <summary>
    /// 基础碎片的文件名前缀：<c>零境V0.6PC</c> / <c>幻杀V0.1安卓</c>
    /// （后面直接接 <c>基础碎片001</c>，不加分隔符）。
    ///
    /// 只取主/次版本：0.0.x 是补丁，补丁之后单独出包，不混进基础碎片；
    /// 名字里也不放短横线/空格 —— 有些平台对文件名挑字符。
    /// </summary>
    public static string BuildArchiveNamePrefix(
        string version,
        string platformLabel = "PC",
        string gameShortName = "零境")
    {
        var shortName = string.IsNullOrWhiteSpace(gameShortName) ? "零境" : gameShortName.Trim();
        var normalized = version?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return $"{shortName}V{platformLabel}";
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return $"{shortName}V{platformLabel}";
        }

        var display = segments.Length >= 2
            ? $"{segments[0]}.{segments[1]}"
            : segments[0];
        return $"{shortName}V{display}{platformLabel}";
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
    /// 压缩包名前缀，例如 <c>零境V0.6PC</c>；空串时就是 <c>基础碎片001.zip</c>。
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
}
