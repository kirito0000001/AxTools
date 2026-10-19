using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 压缩档位。实测结论（2026-09-21，同一批产物各压一遍对比）：
/// 影片已经是压过的视频，再压只省 0.0%，却要白跑一遍 —— 直接**仅存储**；
/// 其余文件走**标准档**；**不开极限**（极限对 .pak/.ucas 也只多省 0.0%，对 exe/dll 多省 0.1%~0.3%，
/// 换来的 CPU 时间和风险不值）。
/// </summary>
public enum CrossingVoidArchiveCompression
{
    /// <summary>仅存储，不压缩（影片 / 安卓 OBB 条目）。</summary>
    Store,

    /// <summary>标准档（PC 的其余文件）。</summary>
    Standard,
}

/// <summary>
/// 把若干相对路径打进一个压缩包。PC 的实现是 Bandizip；测试用假的。
/// 安卓的 OBB 条目不经过这里（直接流式写 zip），见 <see cref="CrossingVoidAndroidFragmentExecutor"/>。
/// </summary>
public interface ICrossingVoidArchiver
{
    /// <param name="archivePath">要生成的压缩包完整路径。</param>
    /// <param name="sourceRoot">这些相对路径的根目录（游戏包目录）。</param>
    /// <param name="relativePaths">要打进去的相对路径；压缩包内部保持同样的相对路径。</param>
    /// <param name="compression">压缩档位（影片仅存储、其余标准档）。</param>
    void CreateArchive(
        string archivePath,
        string sourceRoot,
        IReadOnlyList<string> relativePaths,
        CrossingVoidArchiveCompression compression);
}

/// <summary>切片模式下落成的一个压缩包（<see cref="ArchiveBytes"/> 是压缩后的真实大小）。</summary>
public sealed record CrossingVoidFragmentArchive(
    string ArchivePath,
    int FileCount,
    long SourceBytes,
    long ArchiveBytes);

/// <summary>《生成碎片》的执行结果（PC 和安卓共用）。</summary>
public sealed record CrossingVoidFragmentResult(
    string OutputDirectory,
    IReadOnlyList<CrossingVoidFragmentArchive> Archives)
{
    public int ArchiveCount => Archives.Count;

    public long ArchiveBytes => Archives.Sum(archive => archive.ArchiveBytes);
}

/// <summary>《生成碎片》推进过程中回给界面的一个快照。</summary>
public sealed record CrossingVoidFragmentProgress(string Stage, string Message, int Percent);

/// <summary>
/// PC 的《生成碎片》落盘执行：每一片单独调用打包器生成 <c>零境V0.6PC基础碎片NNN.zip</c>。
///
/// 不在本方法里清空输出目录：清空是破坏性操作，由调用方在确认弹窗之后做
/// （<see cref="CrossingVoidFragmentOutputCleaner"/>）。这里只负责打包，方便单独测。
///
/// 进度按片数报：5~98。单片压缩期间 Bandizip 不吐进度，只能一片一片往前推。
/// </summary>
public sealed class CrossingVoidFragmentExecutor
{
    private readonly ICrossingVoidArchiver archiver;

    public CrossingVoidFragmentExecutor(ICrossingVoidArchiver archiver)
    {
        this.archiver = archiver;
    }

    public CrossingVoidFragmentResult ExecuteSizeSlices(
        string gameDirectory,
        string outputDirectory,
        CrossingVoidFragmentSizePlan plan,
        Action<CrossingVoidFragmentProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏包目录不能为空。", nameof(gameDirectory));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("碎片输出目录不能为空。", nameof(outputDirectory));
        }

        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException($"游戏包目录不存在：{gameDirectory}");
        }

        if (plan.Slices.Count == 0)
        {
            throw new InvalidOperationException("没有可分片的文件。");
        }

        Directory.CreateDirectory(outputDirectory);

        var archives = new List<CrossingVoidFragmentArchive>(plan.Slices.Count);
        for (var index = 0; index < plan.Slices.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = plan.Slices[index];
            var archivePath = Path.Combine(outputDirectory, slice.ArchiveName);
            Report(
                onProgress,
                "slice",
                $"正在压缩基础碎片 {index + 1} / {plan.Slices.Count}（{FormatBytes(slice.SourceBytes)}）…",
                SlicePercent(index, plan.Slices.Count));
            archiver.CreateArchive(
                archivePath,
                gameDirectory,
                slice.Files.Select(file => file.Path).ToList(),
                CrossingVoidArchiveCompression.Standard);

            archives.Add(new CrossingVoidFragmentArchive(
                archivePath,
                slice.Files.Count,
                slice.SourceBytes,
                new FileInfo(archivePath).Length));
            Report(
                onProgress,
                "slice",
                $"已生成基础碎片 {index + 1} / {plan.Slices.Count}（{Path.GetFileName(archivePath)}）。",
                SlicePercent(index + 1, plan.Slices.Count));
        }

        return new CrossingVoidFragmentResult(outputDirectory, archives);
    }

    private static int SlicePercent(int completed, int total) =>
        total <= 0 ? 98 : Math.Clamp(5 + (int)(93.0 * completed / total), 5, 98);

    private static void Report(
        Action<CrossingVoidFragmentProgress>? onProgress,
        string stage,
        string message,
        int percent) =>
        onProgress?.Invoke(new CrossingVoidFragmentProgress(stage, message, Math.Clamp(percent, 0, 100)));

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
