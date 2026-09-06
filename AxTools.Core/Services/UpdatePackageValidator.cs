using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class UpdatePackageValidator
{
    public async Task<ValidatedUpdatePackage> ValidateAsync(
        string packagePath,
        string expectedVersion,
        UpdateAsset asset,
        CancellationToken cancellationToken)
    {
        var package = Path.GetFullPath(packagePath);
        var info = new FileInfo(package);
        if (!info.Exists) throw new FileNotFoundException("更新包不存在。", package);
        if (asset.SizeBytes > 0 && info.Length != asset.SizeBytes) throw new InvalidDataException("更新包大小不匹配。");
        if (!string.IsNullOrWhiteSpace(asset.Sha256))
        {
            await using var input = File.OpenRead(package);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 不匹配。");
        }

        using var archive = ZipFile.OpenRead(package);
        var manifestEntry = archive.GetEntry("update-package.json") ?? throw new InvalidDataException("更新包缺少 update-package.json。");
        await using var manifestStream = manifestEntry.Open();
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var stableKey = root.GetProperty("toolboxStableKey").GetString();
        var version = root.GetProperty("version").GetString();
        var runtime = root.GetProperty("runtime").GetString();
        var entryExe = root.GetProperty("entryExe").GetString();
        if (stableKey != "AxTools" || version != expectedVersion || runtime != "win-x64" || string.IsNullOrWhiteSpace(entryExe)) throw new InvalidDataException("更新包身份、版本或运行平台不匹配。");
        if (Path.IsPathRooted(entryExe) || entryExe.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) throw new InvalidDataException("更新包入口路径不安全。");
        var normalizedEntry = entryExe.Replace('\\', '/');
        if (archive.Entries.All(item => !string.Equals(item.FullName.Replace('\\', '/'), normalizedEntry, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新包缺少入口程序。");
        return new ValidatedUpdatePackage(version, runtime, entryExe);
    }
}
