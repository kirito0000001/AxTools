using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class CrossingVoidPackageInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_ExcludesOnlyDocumentedDebugAndTemporaryContent()
    {
        var game = CreateGameDirectory();
        WriteFile(game, "CrossingVoid.exe", 11);
        WriteFile(game, "Symbols/Game.PDB", 13);
        WriteFile(game, "Saved/Logs/latest.log", 17);
        WriteFile(game, "Saved/Crashes/crash.dmp", 19);
        WriteFile(game, "Content/_DOWNLOAD/partial.bin", 23);
        WriteFile(game, ".git/objects/a", 29);
        WriteFile(game, ".github/workflow.yml", 31);
        WriteFile(game, "Saved/LogsArchive/keep.log", 37);

        var result = new CrossingVoidPackageInspector().Inspect(
            game,
            Path.Combine(_root, "output"),
            "V0.5.14");

        Assert.True(result.IsValid);
        Assert.Equal(3, result.IncludedFileCount);
        Assert.Equal(79, result.IncludedBytes);
        Assert.Equal(5, result.ExcludedFileCount);
        Assert.Equal(101, result.ExcludedBytes);
    }

    [Fact]
    public void Inspect_ReadsVersionFileAndFallsBackToManualVersion()
    {
        var game = CreateGameDirectory();
        WriteFile(game, "CrossingVoid.exe", 1);
        File.WriteAllText(
            Path.Combine(game, "CrossingVoid.version.json"),
            "{\"version\":\"V0.5.15\"}");
        var inspector = new CrossingVoidPackageInspector();

        var fromFile = inspector.Inspect(game, Path.Combine(_root, "out-a"), "V0.5.14");
        File.Delete(Path.Combine(game, "CrossingVoid.version.json"));
        var fromManual = inspector.Inspect(game, Path.Combine(_root, "out-b"), "V0.5.14");

        Assert.Equal("V0.5.15", fromFile.ResolvedVersion);
        Assert.Equal("V0.5.14", fromManual.ResolvedVersion);
    }

    [Fact]
    public void Inspect_RejectsInvalidVersionAndNestedOutput()
    {
        var game = CreateGameDirectory();
        WriteFile(game, "CrossingVoid.exe", 1);
        File.WriteAllText(
            Path.Combine(game, "CrossingVoid.version.json"),
            "{broken");

        var result = new CrossingVoidPackageInspector().Inspect(
            game,
            Path.Combine(game, "GitChunks"),
            string.Empty);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("版本文件", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("输出目录", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(524_288_000L, 1)]
    [InlineData(524_288_001L, 2)]
    [InlineData(1_048_576_000L, 2)]
    public void EstimateChunkCount_UsesFiveHundredMebibytes(long bytes, int expected)
    {
        Assert.Equal(expected, CrossingVoidPackageInspector.EstimateChunkCount(bytes));
    }

    private string CreateGameDirectory()
    {
        var path = Path.Combine(_root, "Game");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, int size)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x41, size).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
