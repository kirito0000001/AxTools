using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class UpdatePackageValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsMatchingPackage()
    {
        var package = CreatePackage("AxTools", "1.0.1-beta.1", includeEntry: true);
        try
        {
            var asset = new UpdateAsset("win-x64", Path.GetFileName(package), "", Hash(package), new FileInfo(package).Length);
            var result = await new UpdatePackageValidator().ValidateAsync(package, "1.0.1-beta.1", asset, CancellationToken.None);
            Assert.Equal("AxTools.exe", result.EntryExe);
        }
        finally { File.Delete(package); }
    }

    [Theory]
    [InlineData("Wrong", "1.0.1-beta.1", true)]
    [InlineData("AxTools", "1.0.2", true)]
    [InlineData("AxTools", "1.0.1-beta.1", false)]
    public async Task ValidateAsync_RejectsIdentityVersionOrMissingEntry(string stableKey, string version, bool includeEntry)
    {
        var package = CreatePackage(stableKey, version, includeEntry);
        try
        {
            var asset = new UpdateAsset("win-x64", Path.GetFileName(package), "", Hash(package), new FileInfo(package).Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageValidator().ValidateAsync(package, "1.0.1-beta.1", asset, CancellationToken.None));
        }
        finally { File.Delete(package); }
    }

    private static string CreatePackage(string stableKey, string version, bool includeEntry)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (includeEntry) archive.CreateEntry("AxTools.exe");
        var manifestEntry = archive.CreateEntry("update-package.json");
        using var writer = new StreamWriter(manifestEntry.Open());
        writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, toolboxStableKey = stableKey, version, runtime = "win-x64", entryExe = "AxTools.exe", files = Array.Empty<object>() }));
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
