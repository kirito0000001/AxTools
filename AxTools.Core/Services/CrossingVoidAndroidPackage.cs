using System.IO.Compression;

namespace AxTools.Core.Services;

/// <summary>OBB 里的一个条目（路径就是清单里的 path）。</summary>
public sealed record CrossingVoidAndroidObbEntry(string Path, long SizeBytes);

/// <summary>
/// 安卓产物目录的入口：一个主 APK + 一个 OBB。
/// 真正的散件在 OBB 里，所以不能像 PC 那样扫目录。
/// </summary>
public sealed record CrossingVoidAndroidPackage(
    string GameDirectory,
    string ObbPath,
    string ApkPath,
    IReadOnlyList<CrossingVoidAndroidObbEntry> ObbEntries)
{
    public string ObbFileName => Path.GetFileName(ObbPath);

    public string ApkFileName => Path.GetFileName(ApkPath);

    public long ObbBytes => ObbEntries.Sum(entry => entry.SizeBytes);

    /// <summary>
    /// 定位 OBB 和主 APK，并读出 OBB 的条目表。
    /// 符号目录、AFS 调试 apk、bat、win-x64 工具都不参与，所以这里只认这两种文件。
    /// </summary>
    public static CrossingVoidAndroidPackage Locate(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("安卓产物目录不能为空。", nameof(gameDirectory));
        }

        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"安卓产物目录不存在：{root}");
        }

        var obbFiles = Directory
            .EnumerateFiles(root, "main.*.obb", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (obbFiles.Length == 0)
        {
            throw new InvalidOperationException($"安卓产物目录里没有 main.*.obb：{root}");
        }

        if (obbFiles.Length > 1)
        {
            throw new InvalidOperationException(
                $"安卓产物目录里有多个 OBB，无法确定用哪个：{string.Join("; ", obbFiles.Select(Path.GetFileName))}");
        }

        var apkFiles = Directory
            .EnumerateFiles(root, "*-Android-Shipping-*.apk", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("AFS_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (apkFiles.Length == 0)
        {
            throw new InvalidOperationException($"安卓产物目录里没有主 APK（*-Android-Shipping-*.apk）：{root}");
        }

        if (apkFiles.Length > 1)
        {
            throw new InvalidOperationException(
                $"安卓产物目录里有多个主 APK，无法确定用哪个：{string.Join("; ", apkFiles.Select(Path.GetFileName))}");
        }

        var entries = ReadObbEntries(obbFiles[0]);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"OBB 里一个文件都没有：{obbFiles[0]}");
        }

        return new CrossingVoidAndroidPackage(root, obbFiles[0], apkFiles[0], entries);
    }

    private static IReadOnlyList<CrossingVoidAndroidObbEntry> ReadObbEntries(string obbPath)
    {
        using var archive = ZipFile.OpenRead(obbPath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !entry.FullName.EndsWith('/'))
            .Select(entry => new CrossingVoidAndroidObbEntry(
                entry.FullName.Replace('\\', '/'),
                entry.Length))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        return entries;
    }
}
