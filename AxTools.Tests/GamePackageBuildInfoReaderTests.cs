using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class GamePackageBuildInfoReaderTests
{
    [Fact]
    public void Read_UsesLatestJsonAndResolvesFilesRelativeToArchiveRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "axtools-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Version", "Windows"));

        var manifestRelative = "Version/Windows/1.2.3.json";
        File.WriteAllText(
            Path.Combine(root, manifestRelative),
            """
            {
              "version": "1.2.3",
              "releaseVersion": "1.2.3",
              "mode": "Full",
              "platform": "Windows",
              "artifactDir": "WindowsNoEditor",
              "files": [
                { "path": "WindowsNoEditor/Game.exe", "sizeBytes": 1024, "sha256": "ABC" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Version", "Windows", "Latest.json"),
            """{ "latestKind": "Full", "latestFile": "Version/Windows/1.2.3.json" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("1.2.3", info!.Version);
            Assert.Equal("Full", info.Kind);
            Assert.False(info.IsPatch);
            var file = Assert.Single(info.Files);
            Assert.Equal("WindowsNoEditor/Game.exe", file.Path);
            Assert.Equal(1024, file.Size);
            Assert.Equal("abc", file.Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_ReportsKindForPatchBuilds()
    {
        var root = Path.Combine(Path.GetTempPath(), "axtools-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Version", "Windows"));
        File.WriteAllText(
            Path.Combine(root, "Version", "Windows", "1.2.3-patch.json"),
            """{ "version": "1.2.3", "mode": "Patch", "platform": "Windows", "files": [] }""");
        File.WriteAllText(
            Path.Combine(root, "Version", "Windows", "Latest.json"),
            """{ "latestKind": "Patch", "latestFile": "Version/Windows/1.2.3-patch.json" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("Patch", info!.Kind);
            Assert.True(info.IsPatch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_FallsBackToLegacyVersionJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "axtools-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "Version.json"),
            """{ "version": "9.9.9", "mode": "Full", "platform": "Windows" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("9.9.9", info!.Version);
            Assert.Empty(info.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
