using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// PC 按体积切片的守卫：1 GiB 边界、单片超限、每片一个独立 zip，
/// 以及"生成前清空输出目录"的安全边界（盘根/源目录/用户目录一律拒绝）。
/// </summary>
public sealed class CrossingVoidFragmentSizeTests
{
    private const long MiB = 1024 * 1024;

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

    // ---------- 分组 ----------

    [Fact]
    public void FillsEachSliceUpToTheLimitAndStartsANewOne()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(new (string, long)[]
        {
            ("b.bin", 600 * MiB),
            ("a.bin", 600 * MiB),
            ("c.bin", 100 * MiB),
        });

        // 按路径排序：a(600) 一片；b(600)+c(100) 一片。
        Assert.Equal(2, plan.Slices.Count);
        Assert.Equal(new[] { "a.bin" }, plan.Slices[0].Files.Select(file => file.Path));
        Assert.Equal(new[] { "b.bin", "c.bin" }, plan.Slices[1].Files.Select(file => file.Path));
        Assert.Equal(600 * MiB, plan.Slices[0].SourceBytes);
        Assert.Equal(700 * MiB, plan.Slices[1].SourceBytes);
        Assert.All(plan.Slices, slice => Assert.True(
            slice.SourceBytes <= CrossingVoidFragmentPlanner.DefaultSliceBytes));
        Assert.Empty(plan.OversizedFiles);
    }

    [Fact]
    public void AllowsASliceExactlyAtTheLimit()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(new (string, long)[]
        {
            ("exact.bin", CrossingVoidFragmentPlanner.DefaultSliceBytes),
        });

        Assert.Single(plan.Slices);
        Assert.Equal(CrossingVoidFragmentPlanner.DefaultSliceBytes, plan.Slices[0].SourceBytes);
        Assert.Empty(plan.OversizedFiles);
    }

    [Fact]
    public void SingleFileOverTheLimitGetsItsOwnSliceAndIsReported()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(new (string, long)[]
        {
            ("big.ucas", 1500 * MiB),
            ("small.txt", 1),
        });

        Assert.Equal(2, plan.Slices.Count);
        Assert.Equal("big.ucas", plan.Slices[0].Files.Single().Path);
        Assert.Equal("small.txt", plan.Slices[1].Files.Single().Path);
        Assert.Contains("big.ucas", plan.OversizedFiles);
    }

    [Fact]
    public void KeepsSlicesUnderTheBandizipCommandLineBudget()
    {
        // 200 个 200 字符的路径 → 光路径就超过 40000 字符，必须拆片。
        var files = Enumerable.Range(0, 200)
            .Select(index => ($"{new string('x', 200)}/{index:000}.bin", 1L))
            .ToArray();

        var plan = CrossingVoidFragmentPlanner.PlanBySize(files);

        Assert.True(plan.Slices.Count >= 2);
        Assert.All(plan.Slices, slice => Assert.True(
            slice.Files.Sum(file => file.Path.Length + 3) <= 30000));
        Assert.Equal(200, plan.TotalCount);
    }

    [Fact]
    public void EmptyInputProducesNoSlices()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(Array.Empty<(string, long)>());

        Assert.Empty(plan.Slices);
        Assert.Equal(0, plan.TotalCount);
        Assert.Equal(0, plan.TotalSourceBytes);
    }

    [Fact]
    public void NamesSlicesWithThreeDigits()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(new (string, long)[]
        {
            ("a.bin", 2),
            ("b.bin", 2),
        }, maxSliceBytes: 2);

        Assert.Equal(2, plan.Slices.Count);
        Assert.Equal("基础碎片001.zip", plan.Slices[0].ArchiveName);
        Assert.Equal("基础碎片002.zip", plan.Slices[1].ArchiveName);
    }

    [Fact]
    public void PutsTheVersionPrefixInFrontOfEveryArchiveName()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(
            new (string, long)[]
            {
                ("a.bin", 2),
                ("b.bin", 2),
            },
            maxSliceBytes: 2,
            archiveNamePrefix: CrossingVoidFragmentPlanner.BuildArchiveNamePrefix("0.6.0"));

        Assert.Equal("零境V0.6基础碎片001.zip", plan.Slices[0].ArchiveName);
        Assert.Equal("零境V0.6基础碎片002.zip", plan.Slices[1].ArchiveName);
    }

    [Theory]
    [InlineData("0.6.0", "零境V0.6")]
    [InlineData("0.6", "零境V0.6")]
    [InlineData("1.2.3.4", "零境V1.2")]
    [InlineData("1", "零境V1")]
    [InlineData("", "零境V")]
    public void ArchiveNamePrefixKeepsOnlyMajorAndMinorVersion(string version, string expected) =>
        Assert.Equal(expected, CrossingVoidFragmentPlanner.BuildArchiveNamePrefix(version));

    [Fact]
    public void ReplacesIllegalCharactersInTheArchiveNamePrefix()
    {
        var plan = CrossingVoidFragmentPlanner.PlanBySize(
            new (string, long)[] { ("a.bin", 1) },
            archiveNamePrefix: "零境 0.6/0:");

        Assert.Equal("零境-0.6-0--基础碎片001.zip", plan.Slices[0].ArchiveName);
    }

    // ---------- 落盘 ----------

    [Fact]
    public void WritesEachSliceAsItsOwnArchive()
    {
        var game = NewTempDirectory("size-game");
        var output = NewTempDirectory("size-out");
        try
        {
            WriteFile(game, "CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", "aaa");
            WriteFile(game, "CrossingVoid/Content/Movies/Login_1.mp4", "bbb");
            WriteFile(game, "CrossingVoid.exe", "ccc");

            var plan = CrossingVoidFragmentPlanner.PlanBySize(
                new (string, long)[]
                {
                    ("CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", 3),
                    ("CrossingVoid/Content/Movies/Login_1.mp4", 3),
                    ("CrossingVoid.exe", 3),
                },
                maxSliceBytes: 6);

            var archiver = new RecordingArchiver();
            var result = new CrossingVoidFragmentExecutor(archiver)
                .ExecuteSizeSlices(game, output, plan);

            Assert.Equal(CrossingVoidFragmentLayout.SizeSlices, result.Layout);
            Assert.Null(result.Groups);
            Assert.Equal(2, result.Archives.Count);
            Assert.Equal("基础碎片001.zip", Path.GetFileName(result.Archives[0].ArchivePath));
            Assert.Equal("基础碎片002.zip", Path.GetFileName(result.Archives[1].ArchivePath));
            Assert.All(result.Archives, archive => Assert.True(File.Exists(archive.ArchivePath)));
            Assert.Equal(2, Directory.EnumerateFiles(output).Count());
            Assert.All(
                archiver.Calls,
                call => Assert.Equal(CrossingVoidArchiveCompression.Standard, call.Compression));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    // ---------- 清空输出目录 ----------

    [Fact]
    public void ClearRemovesEverythingInsideButKeepsTheFolder()
    {
        var game = NewTempDirectory("clear-game");
        var output = NewTempDirectory("clear-out");
        try
        {
            WriteFile(output, "基础碎片001.zip", "a");
            WriteFile(output, "CrossingVoid/Content/Paks/x.ucas", "b");

            var stats = CrossingVoidFragmentOutputCleaner.Measure(output);
            Assert.Equal(2, stats.FileCount);
            Assert.True(stats.TotalBytes > 0);
            Assert.False(stats.IsEmpty);

            CrossingVoidFragmentOutputCleaner.Clear(output, game);

            Assert.True(Directory.Exists(output));
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
            Assert.True(CrossingVoidFragmentOutputCleaner.Measure(output).IsEmpty);
        }
        finally
        {
            Directory.Delete(game, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void RefusesToClearInsideOrAboveTheGameDirectory()
    {
        var game = NewTempDirectory("guard-game");
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(game, "out")).FullName;
            Assert.Throws<InvalidOperationException>(
                () => CrossingVoidFragmentOutputCleaner.Clear(inside, game));
            Assert.Throws<InvalidOperationException>(
                () => CrossingVoidFragmentOutputCleaner.Clear(Path.GetDirectoryName(game)!, game));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void RefusesToClearADriveRootOrTheUserProfile()
    {
        var game = NewTempDirectory("guard-game2");
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
            Assert.Throws<InvalidOperationException>(
                () => CrossingVoidFragmentOutputCleaner.Clear(driveRoot, game));

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Throws<InvalidOperationException>(
                () => CrossingVoidFragmentOutputCleaner.Clear(profile, game));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void MeasureOfAMissingDirectoryIsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"axtools-missing-{Guid.NewGuid():N}");
        Assert.True(CrossingVoidFragmentOutputCleaner.Measure(missing).IsEmpty);
    }

    // ---------- helpers ----------

    private static string NewTempDirectory(string name) =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-{name}-{Guid.NewGuid():N}")).FullName;

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
