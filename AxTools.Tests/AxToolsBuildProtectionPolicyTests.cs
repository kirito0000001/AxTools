using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AxToolsBuildProtectionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AxToolsBuildProtection-{Guid.NewGuid():N}");

    [Fact]
    public void GetProtectedExecutablePaths_DebugCurrentProcessIsProtected()
    {
        var currentExecutable = CreateExecutable(
            "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "AxTools.exe");

        var protectedPaths = InvokePolicy(
            ManagedToolAction.Build,
            currentExecutable);

        Assert.Equal(new[] { currentExecutable }, protectedPaths);
    }

    [Fact]
    public void GetProtectedExecutablePaths_ReleaseCurrentProcessIsNotProtected()
    {
        var currentExecutable = CreateExecutable(
            "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "AxTools.exe");

        var protectedPaths = InvokePolicy(
            ManagedToolAction.BuildAndRun,
            currentExecutable);

        Assert.Empty(protectedPaths);
    }

    [Fact]
    public void GetProtectedExecutablePaths_CurrentProcessOutsideSourceIsNotProtected()
    {
        Directory.CreateDirectory(_root);
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"OutsideAxTools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var currentExecutable = Path.Combine(outsideRoot, "AxTools.exe");
        File.WriteAllText(currentExecutable, string.Empty);
        try
        {
            var protectedPaths = InvokePolicy(
                ManagedToolAction.Build,
                currentExecutable);

            Assert.Empty(protectedPaths);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void GetProtectedExecutablePaths_NonBuildActionIsNotProtected()
    {
        var currentExecutable = CreateExecutable(
            "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "AxTools.exe");

        var protectedPaths = InvokePolicy(
            ManagedToolAction.RunRelease,
            currentExecutable);

        Assert.Empty(protectedPaths);
    }

    [Fact]
    public void GetConflictExecutablePaths_ReleaseCurrentProcessIsNotABuildTarget()
    {
        var developmentExecutable = CreateExecutable(
            "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "AxTools.exe");
        var releaseExecutable = CreateExecutable(
            "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "AxTools.exe");
        var detector = new ProcessConflictDetector(() =>
            new[] { new ProcessSnapshot(80, "AxTools", releaseExecutable) });
        var targets = AxToolsBuildProtectionPolicy.GetConflictExecutablePaths(
            ManagedToolAction.Build,
            _root,
            developmentExecutable,
            releaseExecutable);
        var conflicts = detector.FindConflicts(targets);

        Assert.Empty(conflicts);
        Assert.Contains(developmentExecutable, targets);
        Assert.DoesNotContain(releaseExecutable, targets);
    }

    private IReadOnlyList<string> InvokePolicy(
        ManagedToolAction action,
        string currentExecutable) =>
        AxToolsBuildProtectionPolicy.GetProtectedExecutablePaths(
            action,
            _root,
            currentExecutable);

    private string CreateExecutable(params string[] relativeParts)
    {
        var path = relativeParts.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
