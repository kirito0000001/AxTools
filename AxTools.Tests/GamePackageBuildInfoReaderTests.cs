using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class GamePackageBuildInfoReaderTests
{
    private static string CreateArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "axtools-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Version", "Win64-Client"));
        Directory.CreateDirectory(Path.Combine(root, "Version", "Android-Client"));
        return root;
    }

    [Fact]
    public void Read_FollowsLatestPointerRelativeToArchiveRoot()
    {
        var root = CreateArchive();
        File.WriteAllText(
            Path.Combine(root, "Version", "Win64-Client", "Version-Full.json"),
            """
            {
              "schemaVersion": 2,
              "kind": "Full",
              "version": "0.6.2",
              "mode": "Base",
              "platform": "Win64",
              "artifactDir": "Windows",
              "releaseVersion": "0.6",
              "scope": "Win64-Client",
              "baseVersion": "",
              "generatedAt": "2026-10-27T15:21:19+08:00",
              "files": [
                { "path": "Windows/CrossingVoid.exe", "sizeBytes": 444928, "sha256": "8FD9", "modified": "2026-10-27T15:19:02" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Version", "Win64-Client", "Latest.json"),
            """
            {
              "schemaVersion": 2,
              "kind": "Latest",
              "scope": "Win64-Client",
              "latestFile": "Version/Win64-Client/Version-Full.json",
              "latestKind": "Full",
              "version": "0.6.2",
              "platform": "Win64",
              "generatedAt": "2026-10-27T15:21:19+08:00"
            }
            """);

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("0.6.2", info!.Version);
            Assert.Equal("Full", info.Kind);
            Assert.Equal("Win64-Client", info.Scope);
            Assert.False(info.IsPatch);
            var file = Assert.Single(info.Files);
            Assert.Equal("Windows/CrossingVoid.exe", file.Path);
            Assert.Equal(444928, file.SizeBytes);
            Assert.Equal("8fd9", file.Sha256);
            Assert.Equal("2026-10-27T15:19:02", file.Modified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_KeepsPatchKindAndBaseVersion()
    {
        var root = CreateArchive();
        File.WriteAllText(
            Path.Combine(root, "Version", "Win64-Client", "Version-Patch.json"),
            """
            {
              "kind": "Patch",
              "version": "0.6.2",
              "mode": "Patch",
              "platform": "Win64",
              "scope": "Win64-Client",
              "baseVersion": "0.6",
              "files": [
                { "path": "Windows/CrossingVoid.exe", "sizeBytes": 10, "sha256": "aa" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Version", "Win64-Client", "Latest.json"),
            """
            { "scope": "Win64-Client", "latestFile": "Version/Win64-Client/Version-Patch.json", "latestKind": "Patch" }
            """);

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.True(info!.IsPatch);
            Assert.Equal("0.6", info.BaseVersion);
            Assert.Equal(10, info.TotalBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_PicksAndroidScope()
    {
        var root = CreateArchive();
        File.WriteAllText(
            Path.Combine(root, "Version", "Android-Client", "Version-Full.json"),
            """{ "version": "0.6.2", "kind": "Full", "platform": "Android", "scope": "Android-Client", "files": [] }""");
        File.WriteAllText(
            Path.Combine(root, "Version", "Android-Client", "Latest.json"),
            """{ "scope": "Android-Client", "latestFile": "Version/Android-Client/Version-Full.json", "latestKind": "Full" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Android");

            Assert.Null(error);
            Assert.Equal("Android-Client", info!.Scope);
            Assert.Equal("Android", info.Platform);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_RejectsPointerOutsideScope()
    {
        var root = CreateArchive();
        File.WriteAllText(
            Path.Combine(root, "Version", "Android-Client", "Version-Patch.json"),
            """{ "version": "0.6.2", "kind": "Patch", "platform": "Android", "files": [] }""");
        File.WriteAllText(
            Path.Combine(root, "Version", "Win64-Client", "Latest.json"),
            """{ "scope": "Win64-Client", "latestFile": "Version/Android-Client/Version-Patch.json", "latestKind": "Patch" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(info);
            Assert.Contains("跨了 scope", error);
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
            """{ "version": "9.9.9", "mode": "Full", "platform": "Win64" }""");

        try
        {
            var info = GamePackageBuildInfoReader.Read(root, out var error, "Windows");

            Assert.Null(error);
            Assert.Equal("9.9.9", info!.Version);
            Assert.Empty(info.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
