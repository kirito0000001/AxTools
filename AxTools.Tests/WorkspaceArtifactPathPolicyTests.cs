using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class WorkspaceArtifactPathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxTools.WorkspaceArtifacts",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Apply_RoutesEveryManagedToolBelowWorkspaceArtifacts()
    {
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var managedTools = AppSettings.CreateManagedTools();
        foreach (var (key, paths) in managedTools)
        {
            paths.OutputRoot = Path.Combine(_root, "Legacy", key);
        }

        var changed = WorkspaceArtifactPathPolicy.Apply(projectRoot, managedTools);

        Assert.True(changed);
        foreach (var descriptor in ManagedToolCatalog.All)
        {
            Assert.Equal(
                Path.Combine(projectRoot, "Artifacts", descriptor.StableKey),
                managedTools[descriptor.StableKey].OutputRoot);
        }
    }

    [Fact]
    public void Apply_IsIdempotentAndDoesNotCreateDirectories()
    {
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");
        var managedTools = AppSettings.CreateManagedTools();

        Assert.True(WorkspaceArtifactPathPolicy.Apply(projectRoot, managedTools));
        Assert.False(WorkspaceArtifactPathPolicy.Apply(projectRoot, managedTools));
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "Artifacts")));
    }

    [Fact]
    public void GetToolOutputRoot_RejectsUnknownOrTraversalKeys()
    {
        var projectRoot = Path.Combine(_root, "Ax工具箱项目");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkspaceArtifactPathPolicy.GetToolOutputRoot(projectRoot, "UnknownTool"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkspaceArtifactPathPolicy.GetToolOutputRoot(projectRoot, @"..\Outside"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
