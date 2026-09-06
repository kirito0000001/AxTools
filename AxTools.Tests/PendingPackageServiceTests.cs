using System.Text.Json;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class PendingPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AxToolsPendingTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsValid_ReturnsTrueOnlyWhileRecordedFilesAreUnchangedAndInsideStagingRoot()
    {
        var outputRoot = Path.Combine(_root, "DabaoV");
        var packageRoot = Path.Combine(outputRoot, ".axtools-staging", "run", "零境交错：ZD工具箱V1.2.0");
        var file = CreateFile(Path.Combine(packageRoot, "零境交错：ZD工具箱", "零境交错：ZD工具箱.exe"), "original");
        WriteRecord(packageRoot, Path.Combine(outputRoot, "零境交错：ZD工具箱V1.2.0"), file);
        var service = new PendingPackageService(_root);

        Assert.True(service.IsValid("CrossingVoidZDTool", outputRoot, out var message));
        Assert.Contains("已验证", message);
        Assert.True(service.TryGetValidSummary(
            "CrossingVoidZDTool",
            outputRoot,
            out var summary,
            out _));
        Assert.Equal(packageRoot, summary!.PackageRoot);
        Assert.Equal(Path.Combine(outputRoot, "零境交错：ZD工具箱V1.2.0"), summary.TargetRoot);

        File.AppendAllText(file, "changed");
        Assert.False(service.IsValid("CrossingVoidZDTool", outputRoot, out message));
        Assert.Contains("变化", message);
    }

    [Fact]
    public void IsValid_RejectsPackageOutsideConfiguredStagingRoot()
    {
        var outputRoot = Path.Combine(_root, "DabaoV");
        var packageRoot = Path.Combine(_root, "Elsewhere", "Package");
        var file = CreateFile(Path.Combine(packageRoot, "app.exe"), "content");
        WriteRecord(packageRoot, Path.Combine(outputRoot, "formal"), file);

        Assert.False(new PendingPackageService(_root).IsValid(
            "CrossingVoidZDTool",
            outputRoot,
            out var message));
        Assert.Contains("暂存目录", message);
    }

    private void WriteRecord(string packageRoot, string targetRoot, string file)
    {
        Directory.CreateDirectory(_root);
        var info = new FileInfo(file);
        var payload = new
        {
            schemaVersion = 1,
            packageRoot,
            targetRoot,
            version = "1.2.0",
            validatedAtUtc = DateTime.UtcNow,
            files = new[]
            {
                new
                {
                    path = file,
                    length = info.Length,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()
                }
            }
        };
        File.WriteAllText(
            Path.Combine(_root, "CrossingVoidZDTool.json"),
            JsonSerializer.Serialize(payload));
    }

    private static string CreateFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
