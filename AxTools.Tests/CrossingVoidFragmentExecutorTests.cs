using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 《生成碎片》落盘执行的守卫：chunk 原地散件、影片逐个打包、其余合成一个包。
/// 打包走假实现，所以这里不需要真的装 Bandizip。
/// </summary>
public class CrossingVoidFragmentExecutorTests
{
    private sealed class RecordingArchiver : ICrossingVoidArchiver
    {
        public List<(string ArchivePath, IReadOnlyList<string> Paths, CrossingVoidArchiveCompression Compression)> Calls
        { get; } = new();

        public void CreateArchive(
            string archivePath,
            string sourceRoot,
            IReadOnlyList<string> relativePaths,
            CrossingVoidArchiveCompression compression)
        {
            Calls.Add((archivePath, relativePaths, compression));
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            File.WriteAllText(archivePath, "zip:" + string.Join(";", relativePaths));
        }
    }

    private static string NewTempDirectory(string name) =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-fragments-{name}-{Guid.NewGuid():N}")).FullName;

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void CopiesChunksIntoTheSameTreeAndArchivesVideosAndRemainderSeparately()
    {
        var game = NewTempDirectory("game");
        var output = NewTempDirectory("out");
        try
        {
            WriteFile(game, "CrossingVoid/Content/Paks/pakchunk1-Windows.ucas", "chunk");
            WriteFile(game, "CrossingVoid/Content/Movies/Login_1.mp4", "video");
            WriteFile(game, "CrossingVoid.exe", "exe");
            WriteFile(game, "Engine/Config/StagedBuild.ini", "ini");

            var plan = CrossingVoidFragmentPlanner.Plan(new (string, long)[]
            {
                ("CrossingVoid/Content/Paks/pakchunk1-Windows.ucas", 5),
                ("CrossingVoid/Content/Movies/Login_1.mp4", 5),
                ("CrossingVoid.exe", 3),
                ("Engine/Config/StagedBuild.ini", 3),
            });

            var archiver = new RecordingArchiver();
            var result = new CrossingVoidFragmentExecutor(archiver).Execute(game, output, plan);

            // chunk：原样落在同一棵树下，内容一致
            var chunkPath = Path.Combine(output, "CrossingVoid", "Content", "Paks", "pakchunk1-Windows.ucas");
            Assert.True(File.Exists(chunkPath));
            Assert.Equal("chunk", File.ReadAllText(chunkPath));

            // 影片：单独一个包，包名挂在它在树里的位置后面
            var videoArchive = Path.Combine(output, "CrossingVoid", "Content", "Movies", "Login_1.mp4.zip");
            Assert.True(File.Exists(videoArchive));
            var videoCall = archiver.Calls.Single(call => call.ArchivePath == videoArchive);
            Assert.Equal(new[] { "CrossingVoid/Content/Movies/Login_1.mp4" }, videoCall.Paths);
            // 影片仅存储：压过的视频再压一遍只多花时间（实测多省 0.0%）
            Assert.Equal(CrossingVoidArchiveCompression.Store, videoCall.Compression);

            // 其余：一个包，装剩下的两个（内部保持相对路径）
            var groups = result.Groups!;
            Assert.Equal(CrossingVoidFragmentLayout.ThreeGroups, result.Layout);
            var remainderArchivePath = Path.Combine(
                output,
                CrossingVoidFragmentExecutor.RemainderArchiveName);
            var remainderCall = archiver.Calls.Single(call => call.ArchivePath == remainderArchivePath);
            Assert.Equal(
                new[] { "CrossingVoid.exe", "Engine/Config/StagedBuild.ini" },
                remainderCall.Paths);
            // 其余文件走标准档（不开极限：实测只多省 0.0%~0.3%，不值那点 CPU）
            Assert.Equal(CrossingVoidArchiveCompression.Standard, remainderCall.Compression);

            Assert.Equal(1, groups.Chunks.Count);
            Assert.Equal(5, groups.ChunkBytes);
            Assert.Equal(1, groups.Videos.Count);
            Assert.Equal(2, groups.Remainder.Count);
            Assert.Equal(2, archiver.Calls.Count);
            // 只有 chunk 会散件落盘，别的文件不许跑出来
            Assert.False(File.Exists(Path.Combine(output, "CrossingVoid.exe")));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void SkipsTheRemainderArchiveWhenThereIsNothingLeft()
    {
        var game = NewTempDirectory("game2");
        var output = NewTempDirectory("out2");
        try
        {
            WriteFile(game, "CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", "chunk");

            var plan = CrossingVoidFragmentPlanner.Plan(new (string, long)[]
            {
                ("CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", 5),
            });

            var archiver = new RecordingArchiver();
            var result = new CrossingVoidFragmentExecutor(archiver).Execute(game, output, plan);

            Assert.Empty(result.Groups!.Remainder);
            Assert.Empty(archiver.Calls);
            Assert.True(File.Exists(Path.Combine(output, "CrossingVoid", "Content", "Paks", "pakchunk0-Windows.ucas")));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void RejectsAMissingGameDirectory()
    {
        var output = NewTempDirectory("out3");
        try
        {
            var executor = new CrossingVoidFragmentExecutor(new RecordingArchiver());
            Assert.Throws<DirectoryNotFoundException>(() =>
                executor.Execute(Path.Combine(output, "没有这个目录"), output, new CrossingVoidFragmentPlan([], [], [])));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
