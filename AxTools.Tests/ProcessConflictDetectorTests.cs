using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ProcessConflictDetectorTests
{
    [Fact]
    public void FindConflicts_MatchesNormalizedPathIgnoringCase()
    {
        var snapshots = new[]
        {
            new ProcessSnapshot(10, "FantasyTools", @"D:\Apps\FantasyTools\FantasyTools.exe"),
            new ProcessSnapshot(11, "FantasyTools", @"E:\Other\FantasyTools.exe")
        };
        var detector = new ProcessConflictDetector(() => snapshots);

        var conflicts = detector.FindConflicts(
            new[] { @"d:\apps\FantasyTools\.\FantasyTools.exe" });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(10, conflict.ProcessId);
    }

    [Fact]
    public void FindConflicts_DoesNotMatchSameProcessNameAtDifferentPath()
    {
        var detector = new ProcessConflictDetector(() =>
            new[] { new ProcessSnapshot(20, "AxTools", @"D:\Old\AxTools.exe") });

        var conflicts = detector.FindConflicts(new[] { @"D:\Current\AxTools.exe" });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_SupportsMultipleTargetsAndSkipsBlankPaths()
    {
        var snapshots = new[]
        {
            new ProcessSnapshot(30, "A", @"D:\Tools\A.exe"),
            new ProcessSnapshot(31, "B", @"D:\Tools\B.exe"),
            new ProcessSnapshot(32, "C", string.Empty)
        };
        var detector = new ProcessConflictDetector(() => snapshots);

        var conflicts = detector.FindConflicts(
            new[] { "", "  ", @"D:\Tools\B.exe", @"D:\Tools\A.exe" });

        Assert.Equal(new[] { 30, 31 }, conflicts.Select(item => item.ProcessId).Order());
    }

    [Fact]
    public void FindConflicts_InvalidTargetPathIsIgnored()
    {
        var detector = new ProcessConflictDetector(() =>
            new[] { new ProcessSnapshot(40, "A", @"D:\Tools\A.exe") });

        var conflicts = detector.FindConflicts(new[] { "\0invalid" });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_RelativeTargetPathIsIgnored()
    {
        var absolutePath = Path.Combine(
            Environment.CurrentDirectory,
            "RelativeTool.exe");
        var detector = new ProcessConflictDetector(() =>
            new[] { new ProcessSnapshot(41, "RelativeTool", absolutePath) });

        var conflicts = detector.FindConflicts(new[] { "RelativeTool.exe" });

        Assert.Empty(conflicts);
    }

    [Theory]
    [InlineData(ManagedToolAction.Build)]
    [InlineData(ManagedToolAction.BuildAndRun)]
    [InlineData(ManagedToolAction.ForceBuildAndRun)]
    [InlineData(ManagedToolAction.RunDevelopment)]
    [InlineData(ManagedToolAction.BuildFrontend)]
    [InlineData(ManagedToolAction.PackageStable)]
    [InlineData(ManagedToolAction.PackageBeta)]
    [InlineData(ManagedToolAction.BuildLauncherPackage)]
    [InlineData(ManagedToolAction.PublishDryRun)]
    [InlineData(ManagedToolAction.Publish)]
    [InlineData(ManagedToolAction.ReplaceRelease)]
    public void FindConflictsForAction_BlocksActionsThatCanReplaceExecutables(
        ManagedToolAction action)
    {
        var developmentExecutable = @"D:\Tools\Debug\Tool.exe";
        var releaseExecutable = @"D:\Tools\Release\Tool.exe";
        var detector = new ProcessConflictDetector(() =>
            new[]
            {
                new ProcessSnapshot(50, "Tool", developmentExecutable),
                new ProcessSnapshot(51, "Tool", releaseExecutable)
            });
        var conflicts = detector.FindConflictsForAction(
            action,
            developmentExecutable,
            releaseExecutable);
        Assert.Equal(new[] { 50, 51 }, conflicts.Select(item => item.ProcessId).Order());
    }

    [Theory]
    [InlineData(ManagedToolAction.CheckEnvironment)]
    [InlineData(ManagedToolAction.RunRelease)]
    [InlineData(ManagedToolAction.ValidatePackage)]
    [InlineData(ManagedToolAction.UploadDryRun)]
    [InlineData(ManagedToolAction.Upload)]
    public void FindConflictsForAction_SkipsActionsThatDoNotReplaceExecutables(
        ManagedToolAction action)
    {
        var executable = @"D:\Tools\Tool.exe";
        var detector = new ProcessConflictDetector(() =>
            new[] { new ProcessSnapshot(60, "Tool", executable) });
        var conflicts = detector.FindConflictsForAction(
            action,
            executable,
            executable);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflictsForAction_ProtectedExecutableBlocksBuildWhenConfiguredPathsDiffer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"AxToolsProtectedProcess-{Guid.NewGuid():N}");
        var developmentExecutable = CreateExecutable(root, "ConfiguredDebug.exe");
        var releaseExecutable = CreateExecutable(root, "ConfiguredRelease.exe");
        var currentExecutable = CreateExecutable(root, "CurrentAxTools.exe");
        try
        {
            var detector = new ProcessConflictDetector(() =>
                new[] { new ProcessSnapshot(70, "AxTools", currentExecutable) });
            var conflicts = detector.FindConflictsForAction(
                ManagedToolAction.Build,
                developmentExecutable,
                releaseExecutable,
                new[] { currentExecutable });
            var conflict = Assert.Single(conflicts);
            Assert.Equal(70, conflict.ProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindConflictsForAction_ProtectedExecutableDoesNotBlockNonBuildAction()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"AxToolsProtectedProcess-{Guid.NewGuid():N}");
        var developmentExecutable = CreateExecutable(root, "ConfiguredDebug.exe");
        var releaseExecutable = CreateExecutable(root, "ConfiguredRelease.exe");
        var currentExecutable = CreateExecutable(root, "CurrentAxTools.exe");
        try
        {
            var detector = new ProcessConflictDetector(() =>
                new[] { new ProcessSnapshot(71, "AxTools", currentExecutable) });
            var conflicts = detector.FindConflictsForAction(
                ManagedToolAction.RunRelease,
                developmentExecutable,
                releaseExecutable,
                new[] { currentExecutable });
            Assert.Empty(conflicts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateExecutable(string root, string fileName)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, string.Empty);
        Assert.True(File.Exists(path));
        return path;
    }
}
