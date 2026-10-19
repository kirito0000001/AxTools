using System.IO.Compression;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 安卓的《生成碎片》落盘：不经过 Bandizip，直接用 <see cref="ZipArchive"/> 流式写。
///
/// 为什么：散件在 OBB 里，Bandizip 只能打磁盘文件，先解 2.19 GB 到临时目录既慢又占空间；
/// 直接从 OBB 条目读流写进新 zip，零临时文件，还能随时取消。
///
/// 全部条目用 <see cref="CompressionLevel.NoCompression"/>（仅存储）：
/// OBB 本身就是不压缩 ZIP，APK 内部也压过，再压一遍只多花时间。
/// </summary>
public sealed class CrossingVoidAndroidFragmentExecutor
{
    private const int CopyBufferSize = 8 * 1024 * 1024;

    public CrossingVoidFragmentResult Execute(
        CrossingVoidAndroidFragmentPlan plan,
        string outputDirectory,
        Action<CrossingVoidFragmentProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("碎片输出目录不能为空。", nameof(outputDirectory));
        }

        if (plan.Slices.Count == 0)
        {
            throw new InvalidOperationException("没有可分片的文件。");
        }

        Directory.CreateDirectory(outputDirectory);

        var totalBytes = plan.Slices.Sum(slice => slice.SourceBytes);
        long completedBytes = 0;
        using var obb = ZipFile.OpenRead(plan.Package.ObbPath);
        var archives = new List<CrossingVoidFragmentArchive>(plan.Slices.Count);
        for (var index = 0; index < plan.Slices.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = plan.Slices[index];
            var archivePath = Path.Combine(outputDirectory, slice.ArchiveName);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            Report(
                onProgress,
                "slice",
                $"正在写入 {slice.ArchiveName}（{FormatBytes(slice.SourceBytes)}）…",
                Percent(completedBytes, totalBytes));

            try
            {
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    var entryIndex = 0;
                    foreach (var entry in slice.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entryIndex++;
                        Report(
                            onProgress,
                            "entry",
                            $"正在写入 {slice.ArchiveName}（{entryIndex} / {slice.Entries.Count}）…",
                            Percent(completedBytes, totalBytes));

                        var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.NoCompression);
                        using var output = zipEntry.Open();
                        WriteEntry(obb, entry, output, cancellationToken);
                        completedBytes += entry.SizeBytes;
                    }
                }
            }
            catch
            {
                // 取消/失败时不要把半截 zip 留在结果目录里：下次生成虽然会清空，
                // 但用户当场看到的一个"看起来完整"的包更容易被误发出去。
                TryDelete(archivePath);
                throw;
            }

            archives.Add(new CrossingVoidFragmentArchive(
                archivePath,
                slice.Entries.Count,
                slice.SourceBytes,
                new FileInfo(archivePath).Length));
            Report(
                onProgress,
                "slice",
                $"已生成 {slice.ArchiveName}（{index + 1} / {plan.Slices.Count}）。",
                Percent(completedBytes, totalBytes));
        }

        return new CrossingVoidFragmentResult(outputDirectory, archives);
    }

    private static void WriteEntry(
        ZipArchive obb,
        CrossingVoidAndroidFragmentEntry entry,
        Stream output,
        CancellationToken cancellationToken)
    {
        switch (entry.Source)
        {
            case CrossingVoidAndroidFragmentSource.ObbEntry:
                var obbEntry = obb.GetEntry(entry.Path)
                    ?? throw new InvalidOperationException($"OBB 里找不到条目：{entry.Path}");
                using (var input = obbEntry.Open())
                {
                    CopyStream(input, output, cancellationToken);
                }

                break;

            case CrossingVoidAndroidFragmentSource.LocalFile:
                var localPath = entry.LocalPath
                    ?? throw new InvalidOperationException($"条目缺少本地路径：{entry.Path}");
                using (var input = File.OpenRead(localPath))
                {
                    CopyStream(input, output, cancellationToken);
                }

                break;

            case CrossingVoidAndroidFragmentSource.InMemory:
                output.Write(entry.Content ?? throw new InvalidOperationException(
                    $"条目缺少内存内容：{entry.Path}"));
                break;

            default:
                throw new InvalidOperationException($"未知的条目来源：{entry.Source}");
        }
    }

    private static void CopyStream(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    private static int Percent(long completed, long total) =>
        total <= 0 ? 98 : Math.Clamp(5 + (int)(93.0 * completed / total), 5, 98);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 清理半成品失败不掩盖原始异常。
        }
    }

    private static void Report(
        Action<CrossingVoidFragmentProgress>? onProgress,
        string stage,
        string message,
        int percent) =>
        onProgress?.Invoke(new CrossingVoidFragmentProgress(stage, message, Math.Clamp(percent, 0, 100)));

    private static string FormatBytes(long bytes) => CrossingVoidFragmentExecutor.FormatBytes(bytes);
}
