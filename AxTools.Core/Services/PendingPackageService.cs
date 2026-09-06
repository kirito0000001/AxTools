using System.Security.Cryptography;
using System.Text.Json;

namespace AxTools.Core.Services;

public sealed class PendingPackageService
{
    private readonly string _recordRoot;
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PendingPackageService(string? recordRoot = null)
    {
        _recordRoot = string.IsNullOrWhiteSpace(recordRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AxTools",
                "PendingPackages")
            : Path.GetFullPath(recordRoot);
    }

    public bool IsValid(string toolKey, string outputRoot, out string message) =>
        TryGetValidSummary(toolKey, outputRoot, out _, out message);

    public bool TryGetValidSummary(
        string toolKey,
        string outputRoot,
        out PendingPackageSummary? summary,
        out string message)
    {
        summary = null;
        message = "没有已验证的待替换包。";
        if (string.IsNullOrWhiteSpace(toolKey) || string.IsNullOrWhiteSpace(outputRoot))
        {
            return false;
        }

        try
        {
            var recordPath = Path.Combine(_recordRoot, $"{toolKey}.json");
            if (!File.Exists(recordPath))
            {
                return false;
            }

            var record = JsonSerializer.Deserialize<PendingPackageRecord>(
                File.ReadAllText(recordPath),
                _options);
            if (record is null || record.SchemaVersion != 1 || record.Files.Count == 0)
            {
                message = "待替换记录无效，请重新打包验证。";
                return false;
            }

            var packageRoot = Normalize(record.PackageRoot);
            var stagingRoot = Normalize(Path.Combine(outputRoot, ".axtools-staging"));
            if (!IsDescendant(packageRoot, stagingRoot))
            {
                message = "待替换包不在当前输出根目录的暂存目录内。";
                return false;
            }

            foreach (var identity in record.Files)
            {
                var filePath = Normalize(identity.Path);
                if (!IsDescendant(filePath, packageRoot) || !File.Exists(filePath))
                {
                    message = "待替换包文件缺失或越过暂存目录。";
                    return false;
                }

                var info = new FileInfo(filePath);
                if (info.Length != identity.Length ||
                    !string.Equals(ComputeSha256(filePath), identity.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    message = $"待替换包文件已发生变化：{filePath}";
                    return false;
                }
            }

            summary = new PendingPackageSummary(
                packageRoot,
                Normalize(record.TargetRoot),
                record.Version,
                record.ValidatedAtUtc);
            message = $"已验证待替换包 {record.Version}。";
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = $"待替换记录无法验证：{exception.Message}";
            return false;
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsDescendant(string candidate, string root) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class PendingPackageRecord
    {
        public int SchemaVersion { get; set; }
        public string PackageRoot { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime ValidatedAtUtc { get; set; }
        public List<PendingPackageFileIdentity> Files { get; set; } = [];
    }

    private sealed class PendingPackageFileIdentity
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}

public sealed record PendingPackageSummary(
    string PackageRoot,
    string TargetRoot,
    string Version,
    DateTime ValidatedAtUtc);
