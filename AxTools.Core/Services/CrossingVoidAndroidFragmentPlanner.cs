using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>安卓碎片条目的来源。</summary>
public enum CrossingVoidAndroidFragmentSource
{
    /// <summary>OBB 里的条目（流式读出来写进新 zip）。</summary>
    ObbEntry,

    /// <summary>磁盘上的文件（主 APK、版本标记）。</summary>
    LocalFile,

    /// <summary>内存里的内容（obb.json 旁车）。</summary>
    InMemory,
}

/// <summary>安卓碎片里的一个条目（路径必须和清单 path 逐字符一致）。</summary>
public sealed record CrossingVoidAndroidFragmentEntry(
    string Path,
    long SizeBytes,
    CrossingVoidAndroidFragmentSource Source,
    string? LocalPath = null,
    byte[]? Content = null);

/// <summary>安卓碎片的一片：安装包片 + 若干 OBB 基础碎片片。</summary>
public sealed record CrossingVoidAndroidSlice(
    int Index,
    string ArchiveName,
    IReadOnlyList<CrossingVoidAndroidFragmentEntry> Entries,
    long SourceBytes);

/// <summary>
/// 安卓的分片计划：
///   第 0 片是「安装包」（主 APK + CrossingVoid.version.json + CrossingVoid.obb.json），
///   后面是按 1 GiB 切开的 OBB 条目。
/// </summary>
public sealed record CrossingVoidAndroidFragmentPlan(
    CrossingVoidAndroidPackage Package,
    byte[] ObbSidecarBytes,
    IReadOnlyList<CrossingVoidAndroidSlice> Slices,
    IReadOnlyList<string> OversizedFiles)
{
    public long TotalSourceBytes => Slices.Sum(slice => slice.SourceBytes);

    public int TotalCount => Slices.Sum(slice => slice.Entries.Count);

    public int BaseSliceCount => Math.Max(0, Slices.Count - 1);

    public CrossingVoidAndroidSlice Installer => Slices[0];
}

/// <summary>
/// 安卓的切片规则：APK/版本标记/obb.json 固定一片，OBB 条目复用
/// <see cref="CrossingVoidFragmentPlanner.PlanBySize"/>。
/// </summary>
public static class CrossingVoidAndroidFragmentPlanner
{
    public const string InstallerSuffix = "安装包.zip";

    public static CrossingVoidAndroidFragmentPlan Build(
        string gameDirectory,
        string version,
        string archiveNamePrefix,
        long maxSliceBytes = CrossingVoidFragmentPlanner.DefaultSliceBytes,
        string productKey = CrossingVoidAndroidObbSidecar.ProductKey,
        string versionMarkerFileName = CrossingVoidVersionMarker.FileName,
        string obbSidecarFileName = CrossingVoidAndroidObbSidecar.FileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveNamePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(productKey);
        ValidateFileName(versionMarkerFileName, nameof(versionMarkerFileName));
        ValidateFileName(obbSidecarFileName, nameof(obbSidecarFileName));

        var package = CrossingVoidAndroidPackage.Locate(gameDirectory);

        var versionMarkerPath = Path.Combine(
            package.GameDirectory,
            versionMarkerFileName);
        if (!File.Exists(versionMarkerPath))
        {
            throw new InvalidOperationException(
                $"安卓产物目录里缺少版本标记：{versionMarkerPath}（先生成/补齐一次）");
        }

        var sidecarBytes = CrossingVoidAndroidObbSidecar.Build(
            package.ObbFileName,
            version,
            package.ObbEntries.Select(entry => entry.Path).ToArray(),
            productKey);

        var installerEntries = new List<CrossingVoidAndroidFragmentEntry>
        {
            new(
                package.ApkFileName,
                new FileInfo(package.ApkPath).Length,
                CrossingVoidAndroidFragmentSource.LocalFile,
                package.ApkPath),
            new(
                versionMarkerFileName,
                new FileInfo(versionMarkerPath).Length,
                CrossingVoidAndroidFragmentSource.LocalFile,
                versionMarkerPath),
            new(
                obbSidecarFileName,
                sidecarBytes.Length,
                CrossingVoidAndroidFragmentSource.InMemory,
                Content: sidecarBytes),
        };

        var slices = new List<CrossingVoidAndroidSlice>
        {
            new(
                1,
                $"{archiveNamePrefix}{InstallerSuffix}",
                installerEntries,
                installerEntries.Sum(entry => entry.SizeBytes)),
        };

        var obbPlan = CrossingVoidFragmentPlanner.PlanBySize(
            package.ObbEntries.Select(entry => (entry.Path, entry.SizeBytes)),
            maxSliceBytes,
            archiveNamePrefix);
        foreach (var slice in obbPlan.Slices)
        {
            var entries = slice.Files
                .Select(file => new CrossingVoidAndroidFragmentEntry(
                    file.Path,
                    file.SizeBytes,
                    CrossingVoidAndroidFragmentSource.ObbEntry))
                .ToArray();
            slices.Add(new CrossingVoidAndroidSlice(
                slice.Index + 1,
                slice.ArchiveName,
                entries,
                slice.SourceBytes));
        }

        return new CrossingVoidAndroidFragmentPlan(
            package,
            sidecarBytes,
            slices,
            obbPlan.OversizedFiles);
    }

    private static void ValidateFileName(string fileName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName is "." or "..")
        {
            throw new ArgumentException("文件名不合法。", parameterName);
        }
    }
}
