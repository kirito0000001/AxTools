using System.Text.Json;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class DevelopmentBuildStateCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsBuildStateCleanupTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InvalidateForDeletedPaths_RemovesOverlappingAndMalformedStatesOnly()
    {
        var stateRoot = Path.Combine(_root, "BuildState");
        var firstProject = Path.Combine(_root, "FirstProject");
        var firstOutput = Path.Combine(firstProject, "bin", "Debug");
        var secondProject = Path.Combine(_root, "SecondProject");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(firstOutput);
        Directory.CreateDirectory(secondProject);

        var firstState = WriteState(
            stateRoot,
            "First",
            firstProject,
            Path.Combine(firstOutput, "First.exe"));
        var secondState = WriteState(
            stateRoot,
            "Second",
            secondProject,
            Path.Combine(secondProject, "target", "Second.exe"));
        var malformedState = Path.Combine(stateRoot, "Malformed.json");
        File.WriteAllText(malformedState, "{invalid json");

        var failures = new DevelopmentBuildStateCleanupService(stateRoot)
            .InvalidateForDeletedPaths([firstOutput]);

        Assert.Empty(failures);
        Assert.False(File.Exists(firstState));
        Assert.False(File.Exists(malformedState));
        Assert.True(File.Exists(secondState));
    }

    [Fact]
    public void InvalidateForDeletedPaths_RemovesStateWhenWholeProjectIsDeleted()
    {
        var stateRoot = Path.Combine(_root, "BuildState");
        var projectRoot = Path.Combine(_root, "Project");
        Directory.CreateDirectory(stateRoot);
        var statePath = WriteState(
            stateRoot,
            "Tool",
            projectRoot,
            Path.Combine(projectRoot, "target", "debug", "Tool.exe"));

        var failures = new DevelopmentBuildStateCleanupService(stateRoot)
            .InvalidateForDeletedPaths([projectRoot]);

        Assert.Empty(failures);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void InvalidateForDeletedPaths_KeepsDevelopmentStateWhenOnlyTestOutputIsDeleted()
    {
        var stateRoot = Path.Combine(_root, "BuildState");
        var projectRoot = Path.Combine(_root, "CrossingVoidZDTool");
        Directory.CreateDirectory(stateRoot);
        var statePath = WriteState(
            stateRoot,
            "CrossingVoidZDTool",
            projectRoot,
            Path.Combine(projectRoot, "bin", "x64", "Release", "win-x64", "ZD.exe"));

        new DevelopmentBuildStateCleanupService(stateRoot).InvalidateForDeletedPaths(
            [Path.Combine(projectRoot, "Tests", "CrossingVoidZDTool.RegressionTests", "bin")]);

        Assert.True(File.Exists(statePath));
    }

    private static string WriteState(
        string stateRoot,
        string key,
        string projectRoot,
        string executablePath)
    {
        var path = Path.Combine(stateRoot, $"{key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            toolKey = key,
            projectRoot,
            executable = new { path = executablePath }
        }));
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
