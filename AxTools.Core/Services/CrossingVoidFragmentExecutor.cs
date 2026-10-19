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
    /// <summary>仅存储，不压缩（影片）。</summary>
    Store,

    /// <summary>标准档（其余文件）。</summary>
    Standard,
}

/// <summary>
/// 把若干相对路径打进一个压缩包。真正实现是 Bandizip；测试用假的。
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

/// <summary>《生成碎片》的产物形态。</summary>
public enum CrossingVoidFragmentLayout
{
    /// <summary>
    /// 按类型分三组：分片散件 + 影片逐个压包 + 其余一个包。
    /// 安卓暂时走这条（它的散件在 OBB 里，等 OBB 拆解做完再切到 <see cref="SizeSlices"/>）。
    /// </summary>
    ThreeGroups,

    /// <summary>按体积切片：每片一个独立的 <c>基础碎片NNN.zip</c>（PC 走这条）。</summary>
    SizeSlices,
}

/// <summary>切片模式下落成的一个压缩包（<see cref="ArchiveBytes"/> 是压缩后的真实大小）。</summary>
public sealed record CrossingVoidFragmentArchive(
    string ArchivePath,
    int FileCount,
    long SourceBytes,
    long ArchiveBytes);

/// <summary>《生成碎片》的执行结果。</summary>
public sealed record CrossingVoidFragmentResult(
    string OutputDirectory,
    CrossingVoidFragmentLayout Layout,
    IReadOnlyList<CrossingVoidFragmentArchive> Archives,
    CrossingVoidFragmentPlan? Groups)
{
    public int ArchiveCount => Archives.Count;

    public long ArchiveBytes => Archives.Sum(archive => archive.ArchiveBytes);
}

/// <summary>《生成碎片》推进过程中回给界面的一个快照。</summary>
public sealed record CrossingVoidFragmentProgress(string Stage, string Message, int Percent);

/// <summary>
/// 《生成碎片》的落盘执行，两种模式：
///   · <see cref="ExecuteSizeSlices"/>（PC）：每片打成一个独立的 <c>基础碎片NNN.zip</c>；
///   · <see cref="Execute"/>（安卓暂时）：chunk 拷成散件、影片逐个压包、其余合成一个包。
///
/// 分组规则在 <see cref="CrossingVoidFragmentPlanner"/>，这里只管落盘；
/// 打包走 <see cref="ICrossingVoidArchiver"/>，所以本类不依赖 Bandizip、可以单独被测。
///
/// 旧的三组模式是**只覆盖、不删除**；切片模式由调用方在生成前清空输出目录
/// （<see cref="CrossingVoidFragmentOutputCleaner"/>），这里不碰清空逻辑。
///
/// 进度按 0~100 报：三组模式是 chunk 4~55、影片 55~85、其余 85~98；
/// 切片模式是 5~98（按片数）。落盘结束停在 98，最后那 2 分留给调用方收尾。
/// </summary>
public sealed class CrossingVoidFragmentExecutor
{
    public const string RemainderArchiveName = "其余文件.zip";

    /// <summary>流式拷贝的缓冲：8 MiB —— 大到不用频繁 syscall，小到取消检查够灵敏。</summary>
    private const int CopyBufferSize = 8 * 1024 * 1024;

    private readonly ICrossingVoidArchiver archiver;

    public CrossingVoidFragmentExecutor(ICrossingVoidArchiver archiver)
    {
        this.archiver = archiver;
    }

    /// <summary>影片的压缩包就挂在它在目录树里的位置后面（`…/Login_1.mp4.zip`），解开后正好回到原位。</summary>
    public static string VideoArchivePath(string outputDirectory, string videoRelativePath) =>
        Resolve(outputDirectory, videoRelativePath) + ".zip";

    public CrossingVoidFragmentResult Execute(
        string gameDirectory,
        string outputDirectory,
        CrossingVoidFragmentPlan plan,
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

        Directory.CreateDirectory(outputDirectory);
        Report(onProgress, "chunk", "正在拷贝分片…", 4);

        long copiedBytes = 0;
        foreach (var entry in plan.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Resolve(gameDirectory, entry.Path);
            var destination = Resolve(outputDirectory, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            copiedBytes += CopyFile(
                source,
                destination,
                copiedBytes,
                plan.ChunkBytes,
                onProgress,
                cancellationToken);
        }

        if (plan.Chunks.Count > 0)
        {
            // 全是空文件时上面的字节进度动不了，这里保证至少走到 55。
            Report(onProgress, "chunk", $"分片已拷贝 {plan.Chunks.Count} 个。", 55);
        }

        for (var index = 0; index < plan.Videos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = plan.Videos[index];
            var archivePath = VideoArchivePath(outputDirectory, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            Report(
                onProgress,
                "video",
                $"正在压缩影片 {index + 1} / {plan.Videos.Count}（{Path.GetFileName(entry.Path)}）…",
                VideoPercent(index, plan.Videos.Count));
            archiver.CreateArchive(
                archivePath,
                gameDirectory,
                new[] { entry.Path },
                CrossingVoidArchiveCompression.Store);
        }

        if (plan.Videos.Count > 0)
        {
            Report(onProgress, "video", $"影片已打包 {plan.Videos.Count} 个。", 85);
        }

        if (plan.Remainder.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(
                onProgress,
                "remainder",
                $"正在压缩其余文件 {plan.Remainder.Count} 个…",
                88);
            var remainderArchivePath = Path.Combine(outputDirectory, RemainderArchiveName);
            archiver.CreateArchive(
                remainderArchivePath,
                gameDirectory,
                plan.Remainder.Select(entry => entry.Path).ToList(),
                CrossingVoidArchiveCompression.Standard);
        }
        else
        {
            // 没有"其余"也要把进度推到位，否则进度条会停在上一个阶段。
            Report(onProgress, "remainder", "没有需要合并的其他文件。", 88);
        }

        return new CrossingVoidFragmentResult(
            outputDirectory,
            CrossingVoidFragmentLayout.ThreeGroups,
            [],
            plan);
    }

    /// <summary>
    /// 按大小切片落盘：每一片单独调用打包器生成 <c>基础碎片NNN.zip</c>。
    ///
    /// 不在本方法里清空输出目录：清空是破坏性操作，由调用方在确认弹窗之后做
    /// （<see cref="CrossingVoidFragmentOutputCleaner"/>）。这里只负责打包，方便单独测。
    ///
    /// 进度按片数报：5~98。单片压缩期间 Bandizip 不吐进度，只能一片一片往前推。
    /// </summary>
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

        return new CrossingVoidFragmentResult(
            outputDirectory,
            CrossingVoidFragmentLayout.SizeSlices,
            archives,
            null);
    }

    private static int SlicePercent(int completed, int total) =>
        total <= 0 ? 98 : Math.Clamp(5 + (int)(93.0 * completed / total), 5, 98);

    private static int VideoPercent(int index, int total) =>
        total <= 0 ? 85 : 55 + (int)(30.0 * index / total);

    /// <summary>
    /// 流式拷贝：只有这样才能在拷一个 900 MB 的 ucas 时既报进度、又响应取消。
    /// </summary>
    private static long CopyFile(
        string source,
        string destination,
        long copiedBefore,
        long totalBytes,
        Action<CrossingVoidFragmentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        using (var input = File.OpenRead(source))
        using (var output = File.Create(destination))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied += read;
                if (totalBytes > 0)
                {
                    var done = copiedBefore + copied;
                    Report(
                        onProgress,
                        "chunk",
                        $"正在拷贝分片 {FormatBytes(done)} / {FormatBytes(totalBytes)}…",
                        4 + (int)(51.0 * done / totalBytes));
                }
            }
        }

        return copied;
    }

    private static void Report(
        Action<CrossingVoidFragmentProgress>? onProgress,
        string stage,
        string message,
        int percent) =>
        onProgress?.Invoke(new CrossingVoidFragmentProgress(stage, message, Math.Clamp(percent, 0, 100)));

    private static string FormatBytes(long bytes)
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

    private static string Resolve(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
