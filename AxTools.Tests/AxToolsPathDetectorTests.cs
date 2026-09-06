using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AxToolsPathDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsPathDetectorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DetectAndApply_PreservesWorkspaceOwnedOutputRootEvenWhenItDoesNotExist()
    {
        var sourceRoot = CreateProject();
        var workspaceOutput = Path.Combine(_root, "Workspace", "Artifacts", "AxTools");
        var paths = new ManagedToolPaths { OutputRoot = workspaceOutput };

        new AxToolsPathDetector().DetectAndApply(sourceRoot, paths);

        Assert.Equal(workspaceOutput, paths.OutputRoot);
        Assert.False(Directory.Exists(workspaceOutput));
    }

    [Fact]
    public void DetectAndApply_FindsProjectFromNestedApplicationDirectory()
    {
        var sourceRoot = CreateProject();
        var applicationDirectory = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            "Release",
            "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(applicationDirectory);
        var developmentExecutable = CreateExecutable(sourceRoot, "Debug");
        var releaseExecutable = CreateExecutable(sourceRoot, "Release");
        var paths = new ManagedToolPaths();

        var result = new AxToolsPathDetector().DetectAndApply(
            applicationDirectory,
            paths);

        Assert.True(result.SourceFound);
        Assert.True(result.Changed);
        Assert.Equal("AxTools 路径已自动检测并保存。", result.Message);
        Assert.Equal(sourceRoot, paths.SourceRoot);
        Assert.Equal(developmentExecutable, paths.DevelopmentExecutable);
        Assert.Equal(releaseExecutable, paths.ReleaseExecutable);
        Assert.Equal(string.Empty, paths.OutputRoot);
    }

    [Fact]
    public void DetectAndApply_PreservesValidCustomPaths()
    {
        var sourceRoot = CreateProject();
        var customRoot = Path.Combine(_root, "Custom");
        var customDevelopment = Path.Combine(customRoot, "Dev", "AxTools.exe");
        var customRelease = Path.Combine(customRoot, "Release", "AxTools.exe");
        var customOutput = Path.Combine(customRoot, "Output");
        Directory.CreateDirectory(Path.GetDirectoryName(customDevelopment)!);
        Directory.CreateDirectory(Path.GetDirectoryName(customRelease)!);
        Directory.CreateDirectory(customOutput);
        File.WriteAllText(customDevelopment, string.Empty);
        File.WriteAllText(customRelease, string.Empty);
        var paths = new ManagedToolPaths
        {
            SourceRoot = sourceRoot,
            DevelopmentExecutable = customDevelopment,
            ReleaseExecutable = customRelease,
            OutputRoot = customOutput
        };

        var result = new AxToolsPathDetector().DetectAndApply(sourceRoot, paths);

        Assert.True(result.SourceFound);
        Assert.False(result.Changed);
        Assert.Equal("AxTools 路径已通过检查。", result.Message);
        Assert.Equal(customDevelopment, paths.DevelopmentExecutable);
        Assert.Equal(customRelease, paths.ReleaseExecutable);
        Assert.Equal(customOutput, paths.OutputRoot);
    }

    [Fact]
    public void DetectAndApply_ReplacesInvalidConfiguredPaths()
    {
        var sourceRoot = CreateProject();
        var developmentExecutable = CreateExecutable(sourceRoot, "Debug");
        var releaseExecutable = CreateExecutable(sourceRoot, "Release");
        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "MissingSource"),
            DevelopmentExecutable = Path.Combine(_root, "MissingDev.exe"),
            ReleaseExecutable = Path.Combine(_root, "MissingRelease.exe"),
            OutputRoot = Path.Combine(_root, "MissingOutput")
        };

        var result = new AxToolsPathDetector().DetectAndApply(sourceRoot, paths);

        Assert.True(result.Changed);
        Assert.Equal(sourceRoot, paths.SourceRoot);
        Assert.Equal(developmentExecutable, paths.DevelopmentExecutable);
        Assert.Equal(releaseExecutable, paths.ReleaseExecutable);
        Assert.Equal(Path.Combine(_root, "MissingOutput"), paths.OutputRoot);
    }

    [Fact]
    public void DetectAndApply_ReplacesLegacyBuildExecutablesWithWinX64Paths()
    {
        var sourceRoot = CreateProject();
        var developmentExecutable = CreateExecutable(sourceRoot, "Debug");
        var releaseExecutable = CreateExecutable(sourceRoot, "Release");
        var legacyDevelopment = CreateLegacyExecutable(sourceRoot, "Debug");
        var legacyRelease = CreateLegacyExecutable(sourceRoot, "Release");
        var paths = new ManagedToolPaths
        {
            SourceRoot = sourceRoot,
            DevelopmentExecutable = legacyDevelopment,
            ReleaseExecutable = legacyRelease,
            OutputRoot = Path.Combine(sourceRoot, "Artifacts")
        };

        var result = new AxToolsPathDetector().DetectAndApply(sourceRoot, paths);

        Assert.True(result.Changed);
        Assert.Equal(developmentExecutable, paths.DevelopmentExecutable);
        Assert.Equal(releaseExecutable, paths.ReleaseExecutable);
    }

    [Fact]
    public void DetectAndApply_WithoutProjectMarker_DoesNotGuessOrChangePaths()
    {
        var applicationDirectory = Path.Combine(_root, "NoProject", "Nested");
        Directory.CreateDirectory(applicationDirectory);
        var paths = new ManagedToolPaths
        {
            SourceRoot = Path.Combine(_root, "MissingSource"),
            DevelopmentExecutable = "missing-dev.exe",
            ReleaseExecutable = "missing-release.exe",
            OutputRoot = "missing-output"
        };

        var result = new AxToolsPathDetector().DetectAndApply(
            applicationDirectory,
            paths);

        Assert.False(result.SourceFound);
        Assert.False(result.Changed);
        Assert.Equal(
            "未找到 AxTools.csproj，请手动选择源码目录。",
            result.Message);
        Assert.Equal(Path.Combine(_root, "MissingSource"), paths.SourceRoot);
        Assert.Equal("missing-dev.exe", paths.DevelopmentExecutable);
        Assert.Equal("missing-release.exe", paths.ReleaseExecutable);
        Assert.Equal("missing-output", paths.OutputRoot);
    }

    private string CreateProject()
    {
        var sourceRoot = Path.Combine(_root, "AxTools");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "AxTools.csproj"), "<Project />");
        return sourceRoot;
    }

    private static string CreateExecutable(string sourceRoot, string configuration)
    {
        var executable = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            "net8.0-windows10.0.19041.0",
            "win-x64",
            "AxTools.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        return executable;
    }

    private static string CreateLegacyExecutable(string sourceRoot, string configuration)
    {
        var executable = Path.Combine(
            sourceRoot,
            "bin",
            "x64",
            configuration,
            "net8.0-windows10.0.19041.0",
            "AxTools.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        return executable;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
