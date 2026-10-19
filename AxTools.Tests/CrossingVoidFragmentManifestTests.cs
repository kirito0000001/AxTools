using System.Text.Json;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 碎片清单的守卫：字段名、总字节、每个包的大小/文件数必须和实际产物一致；
/// 中文包名不能被转义成 \uXXXX（服务端和日志里要能直接读）。
/// </summary>
public sealed class CrossingVoidFragmentManifestTests
{
    [Fact]
    public void WritesCamelCaseManifestWithArchiveSizes()
    {
        var root = NewTempDirectory("manifest");
        try
        {
            var archives = new[]
            {
                new CrossingVoidFragmentArchive(
                    Path.Combine(root, "零境V0.6基础碎片001.zip"), 40, 1_000_000_000, 862_273_823),
                new CrossingVoidFragmentArchive(
                    Path.Combine(root, "零境V0.6基础碎片002.zip"), 75, 900_000_000, 807_000_000),
            };

            var path = CrossingVoidFragmentManifestWriter.Write(root, "0.6.0", archives);
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var manifest = document.RootElement;

            Assert.Equal(CrossingVoidFragmentManifestWriter.CurrentSchemaVersion, manifest.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("0.6.0", manifest.GetProperty("version").GetString());
            Assert.Equal(1_669_273_823L, manifest.GetProperty("totalBytes").GetInt64());

            var fragments = manifest.GetProperty("fragments");
            Assert.Equal(2, fragments.GetArrayLength());
            Assert.Equal("零境V0.6基础碎片001.zip", fragments[0].GetProperty("fileName").GetString());
            Assert.Equal(862_273_823L, fragments[0].GetProperty("sizeBytes").GetInt64());
            Assert.Equal(40, fragments[0].GetProperty("fileCount").GetInt32());

            // 中文包名保持原样，不变成 \u96f6\u5883...
            Assert.Contains("零境V0.6基础碎片001.zip", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyArchiveListProducesAnEmptyManifest()
    {
        var manifest = CrossingVoidFragmentManifestWriter.Create("0.6.0", []);

        Assert.Equal(CrossingVoidFragmentManifestWriter.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(0L, manifest.TotalBytes);
        Assert.Empty(manifest.Fragments);
    }

    [Fact]
    public void UsesTheSameSizeAndCountAsTheArchives()
    {
        var archives = new[]
        {
            new CrossingVoidFragmentArchive("a.zip", 3, 100, 50),
            new CrossingVoidFragmentArchive("b.zip", 7, 200, 150),
        };

        var manifest = CrossingVoidFragmentManifestWriter.Create("0.6.0", archives);

        Assert.Equal(200L, manifest.TotalBytes);
        Assert.Equal(new[] { "a.zip", "b.zip" }, manifest.Fragments.Select(fragment => fragment.FileName));
        Assert.Equal(new[] { 50L, 150L }, manifest.Fragments.Select(fragment => fragment.SizeBytes));
        Assert.Equal(new[] { 3, 7 }, manifest.Fragments.Select(fragment => fragment.FileCount));
    }

    private static string NewTempDirectory(string name) =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-{name}-{Guid.NewGuid():N}")).FullName;
}
