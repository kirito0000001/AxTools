using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 《生成碎片》外围两条口径的守卫：
/// ① 压缩档位必须落成 Bandizip 认的数字（影片 0=仅存储、其余 5=标准档）；
/// ② 扫源目录时排除规则必须和发布脚本一致，否则碎片里会混进发布时根本不存在的东西。
/// </summary>
public sealed class CrossingVoidFragmentGenerationTests
{
    // ---------- Bandizip 参数 ----------

    [Fact]
    public void VideoArchiveUsesStoreAndEverythingElseUsesTheStandardLevel()
    {
        Assert.Equal(5, BandizipCrossingVoidArchiver.ResolveLevel(CrossingVoidArchiveCompression.Standard));
        Assert.Equal(0, BandizipCrossingVoidArchiver.ResolveLevel(CrossingVoidArchiveCompression.Store));
    }

    [Fact]
    public void CompressArgumentsKeepTheShapeThatWasAlreadyWorkingInTheScript()
    {
        var arguments = BandizipCrossingVoidArchiver.BuildCompressArguments(
            @"D:\out\其余文件.zip",
            new[] { @"CrossingVoid.exe", @"Engine\Config\StagedBuild.ini" },
            CrossingVoidArchiveCompression.Standard);

        // 前六个是固定开关，顺序和 Invoke-CrossingVoidGamePackage.ps1 里那条一致；
        // -l: 后面直接跟档位，千万别改成极限（实测只多省 0.0%~0.3%）。
        Assert.Equal(
            new[] { "c", "-fmt:zip", "-l:5", "-r", "-y", @"D:\out\其余文件.zip" },
            arguments.Take(6).ToArray());
        Assert.Equal(new[] { @"CrossingVoid.exe", @"Engine\Config\StagedBuild.ini" }, arguments.Skip(6).ToArray());
        Assert.DoesNotContain(arguments, argument => argument == "-l:9");
    }

    // ---------- 扫源目录 ----------

    [Fact]
    public void ScanSkipsWhatThePublishScriptSkipsAndNormalizesSeparators()
    {
        var game = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-fragscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            WriteFile(game, @"CrossingVoid\Content\Paks\pakchunk0-Windows.ucas", "chunk");
            WriteFile(game, @"CrossingVoid\Content\Movies\Login_1.mp4", "video");
            WriteFile(game, @"CrossingVoid.exe", "exe");
            // 下面这些发布脚本都会排除，碎片也不能带上
            WriteFile(game, @"CrossingVoid\Binaries\Win64\CrossingVoid.pdb", "pdb");
            WriteFile(game, @"Manifest_DebugFiles_Win64.txt", "debug");
            WriteFile(game, @"_download\pakchunk0-Windows.ucas.part", "partial");
            WriteFile(game, @"Saved\Logs\CrossingVoid.log", "log");
            WriteFile(game, @"Saved\Crashes\crash.dmp", "dump");
            WriteFile(game, @".git\config", "git");

            var files = CrossingVoidFragmentGenerator.ScanPackageFiles(game);

            // 只比集合，不比扫描器给出的顺序（顺序只是为了让重复生成的结果稳定）。
            Assert.Equal(
                new[]
                {
                    "CrossingVoid/Content/Movies/Login_1.mp4",
                    "CrossingVoid/Content/Paks/pakchunk0-Windows.ucas",
                    "CrossingVoid.exe",
                }.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
            Assert.All(files, file => Assert.DoesNotContain('\\', file.Path));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void PlanFromAScannedDirectoryUsesSizeSlices()
    {
        var game = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-fragplan-{Guid.NewGuid():N}")).FullName;
        try
        {
            WriteFile(game, @"CrossingVoid\Content\Paks\pakchunk0-Windows.ucas", "chunk");
            WriteFile(game, @"CrossingVoid\Content\Paks\pakchunk1-Windows.pak", "chunk");
            WriteFile(game, @"CrossingVoid\Content\Movies\Login_1.mp4", "video");
            WriteFile(game, @"CrossingVoid.exe", "exe");

            var plan = CrossingVoidFragmentGenerator.BuildSizePlan(game, "零境V0.6PC");

            // 四个小文件都在 1 GiB 以内，按体积切片只应该有一片。
            Assert.Single(plan.Slices);
            Assert.Equal(4, plan.TotalCount);
            Assert.Equal("零境V0.6PC基础碎片001.zip", plan.Slices[0].ArchiveName);
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void ScanningAMissingDirectoryFailsLoudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"axtools-fragmissing-{Guid.NewGuid():N}");
        Assert.Throws<DirectoryNotFoundException>(
            () => CrossingVoidFragmentGenerator.ScanPackageFiles(missing));
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
