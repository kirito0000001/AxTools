using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedProjectCloneServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AxTools-CloneService-{Guid.NewGuid():N}");

    [Fact]
    public void CreatePlan_UsesFixedGitHubRepositoryAndSelectedParent()
    {
        Directory.CreateDirectory(_root);
        var service = new ManagedProjectCloneService(Path.Combine(_root, "Scripts"));

        var plan = service.CreatePlan(ManagedToolKey.GalExcleTools, _root);

        Assert.Equal(
            "https://github.com/kirito0000001/TFACStorybox.git",
            plan.RepositoryUrl);
        Assert.Equal(Path.Combine(_root, "GalExcleTools"), plan.TargetDirectory);
        Assert.Contains("GalExcleTools.csproj", plan.ExpectedMarkers);
        Assert.Equal(ManagedToolKey.GalExcleTools, plan.ToolKey);
    }

    [Fact]
    public void CreatePlan_RejectsNonEmptyTargetDirectory()
    {
        var target = Path.Combine(_root, "FantasyTools");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "keep");
        var service = new ManagedProjectCloneService(Path.Combine(_root, "Scripts"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.CreatePlan(ManagedToolKey.FantasyTools, _root));

        Assert.Contains("不是空目录", exception.Message);
        Assert.True(File.Exists(Path.Combine(target, "existing.txt")));
    }

    [Fact]
    public void CreateTask_PassesPlanAsStructuredArguments()
    {
        Directory.CreateDirectory(_root);
        var service = new ManagedProjectCloneService(Path.Combine(_root, "Scripts"));
        var plan = service.CreatePlan(ManagedToolKey.CrossingVoidAndroid, _root);

        var task = service.CreateTask(plan);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "Scripts", "Git", "Clone-ManagedProject.ps1")),
            task.ScriptPath);
        Assert.Contains(plan.RepositoryUrl, task.Arguments);
        Assert.Contains(plan.TargetDirectory, task.Arguments);
        Assert.Contains("package.json", task.Arguments.Single(argument => argument.StartsWith("[", StringComparison.Ordinal)));
        Assert.True(task.IsHeavy);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
