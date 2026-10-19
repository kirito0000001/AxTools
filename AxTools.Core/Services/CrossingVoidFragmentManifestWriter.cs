using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AxTools.Core.Services;

/// <summary>碎片清单里的一个包（名字 + 压缩后大小 + 包内文件数）。</summary>
public sealed record CrossingVoidFragmentManifestFragment(
    string FileName,
    long SizeBytes,
    int FileCount);

/// <summary>
/// 碎片清单：告诉启动器/页面"这一版分成几个包、每个包多大、装了多少文件"。
/// **不含 sha256**：整包 2.5 GB 算一遍要十几秒，而导入时每个文件本来就要过 sha256，
/// 包坏了必然当场暴露；fileCount 只读 zip 中央目录就有，几十毫秒，够判断"是不是同一个包"。
/// </summary>
public sealed record CrossingVoidFragmentManifest(
    int SchemaVersion,
    string Version,
    long TotalBytes,
    IReadOnlyList<CrossingVoidFragmentManifestFragment> Fragments);

/// <summary>
/// 把本次生成的基础碎片写成 <c>fragments.json</c>（放在碎片输出目录根）。
///
/// 上传到下载站时把它放到 <c>games/&lt;产品段&gt;/fragments/&lt;版本&gt;.json</c>，
/// 再在 <c>latest.json</c> 里加 <c>fragmentsUrl</c>；本工具只负责产出这份文件，不动服务端。
/// 字段名用小驼峰、中文文件名不转义，和发布侧其它 JSON 的读法一致。
/// </summary>
public static class CrossingVoidFragmentManifestWriter
{
    public const string FileName = "fragments.json";

    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CrossingVoidFragmentManifest Create(
        string version,
        IReadOnlyList<CrossingVoidFragmentArchive> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var fragments = archives
            .Select(archive => new CrossingVoidFragmentManifestFragment(
                Path.GetFileName(archive.ArchivePath),
                archive.ArchiveBytes,
                archive.FileCount))
            .ToArray();

        return new CrossingVoidFragmentManifest(
            CurrentSchemaVersion,
            version?.Trim() ?? string.Empty,
            fragments.Sum(fragment => fragment.SizeBytes),
            fragments);
    }

    public static string BuildJson(CrossingVoidFragmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    /// <summary>写到碎片输出目录，返回完整路径。</summary>
    public static string Write(
        string outputDirectory,
        string version,
        IReadOnlyList<CrossingVoidFragmentArchive> archives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var manifest = Create(version, archives);
        var path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(path, BuildJson(manifest), new UTF8Encoding(false));
        return path;
    }
}
