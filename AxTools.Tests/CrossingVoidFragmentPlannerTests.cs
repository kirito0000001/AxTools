using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 《生成碎片》分类规则的守卫（2026-09-21 用户拍板的三组口径）。
/// 用的是真实清单里的路径样本 —— 判定必须跟着清单走，不能跟着 `pakchunkN` 这种名字走。
/// </summary>
public class CrossingVoidFragmentPlannerTests
{
    [Theory]
    // chunk：Content/Paks 下的 pak / ucas / utoc
    [InlineData("CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", CrossingVoidFragmentKind.Chunk)]
    [InlineData("CrossingVoid/Content/Paks/pakchunk1-Windows.pak", CrossingVoidFragmentKind.Chunk)]
    [InlineData("CrossingVoid/Content/Paks/pakchunk1-Windows.utoc", CrossingVoidFragmentKind.Chunk)]
    [InlineData("CrossingVoid/Content/Paks/global.ucas", CrossingVoidFragmentKind.Chunk)]
    [InlineData("CrossingVoid/Content/Paks/pakchunk0-Android.ucas", CrossingVoidFragmentKind.Chunk)]
    // 影片：Content/Movies 下的 mp4
    [InlineData("CrossingVoid/Content/Movies/Login_1.mp4", CrossingVoidFragmentKind.Video)]
    [InlineData("CrossingVoid/Content/Movies/Login_1_Low.mp4", CrossingVoidFragmentKind.Video)]
    // 其余
    [InlineData("CrossingVoid.exe", CrossingVoidFragmentKind.Remainder)]
    [InlineData("CrossingVoid/Binaries/Win64/CrossingVoid-Win64-Shipping.exe", CrossingVoidFragmentKind.Remainder)]
    [InlineData("Engine/Binaries/ThirdParty/DbgHelp/dbghelp.dll", CrossingVoidFragmentKind.Remainder)]
    [InlineData("CrossingVoid.version.json", CrossingVoidFragmentKind.Remainder)]
    [InlineData("CrossingVoid-Android-Shipping-arm64.apk", CrossingVoidFragmentKind.Remainder)]
    // Paks 目录里但不是 pak/ucas/utoc → 不算 chunk（别把清单从 Paks 里捞出来的杂项当成分片）
    [InlineData("CrossingVoid/Content/Paks/notes.txt", CrossingVoidFragmentKind.Remainder)]
    // Movies 里的非 mp4 → 走其余
    [InlineData("CrossingVoid/Content/Movies/cover.png", CrossingVoidFragmentKind.Remainder)]
    public void ClassifiesFilesIntoTheThreeGroups(string path, CrossingVoidFragmentKind expected)
    {
        Assert.Equal(expected, CrossingVoidFragmentPlanner.Classify(path));
    }

    [Fact]
    public void KeepsDirectoryStructureAndSumsEachGroup()
    {
        var plan = CrossingVoidFragmentPlanner.Plan(new (string, long)[]
        {
            ("CrossingVoid/Content/Paks/pakchunk0-Windows.ucas", 900),
            ("CrossingVoid/Content/Paks/pakchunk1-Windows.ucas", 100),
            ("CrossingVoid/Content/Movies/Login_1.mp4", 30),
            ("CrossingVoid/Content/Movies/Login_2.mp4", 20),
            ("CrossingVoid.exe", 5),
            ("Engine/Config/StagedBuild.ini", 1),
        });

        Assert.Equal(2, plan.Chunks.Count);
        Assert.Equal(2, plan.Videos.Count);
        Assert.Equal(2, plan.Remainder.Count);
        Assert.Equal(1000, plan.ChunkBytes);
        Assert.Equal(50, plan.VideoBytes);
        Assert.Equal(6, plan.RemainderBytes);
        Assert.Equal(1056, plan.TotalBytes);
        Assert.Equal(6, plan.TotalCount);
        // 路径原样保留（下游要按它摆出和游戏一样的目录树）
        Assert.Contains(plan.Chunks, entry => entry.Path == "CrossingVoid/Content/Paks/pakchunk0-Windows.ucas");
    }

    [Fact]
    public void HandlesWindowsSeparatorsAndEmptyInput()
    {
        Assert.Equal(
            CrossingVoidFragmentKind.Chunk,
            CrossingVoidFragmentPlanner.Classify(@"CrossingVoid\Content\Paks\pakchunk3-Windows.ucas"));

        var empty = CrossingVoidFragmentPlanner.Plan(Array.Empty<(string, long)>());
        Assert.Empty(empty.Chunks);
        Assert.Empty(empty.Videos);
        Assert.Empty(empty.Remainder);
        Assert.Equal(0, empty.TotalBytes);
    }
}
