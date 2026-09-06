using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedReleaseDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AxTools-ReleaseDownload-{Guid.NewGuid():N}");

    [Fact]
    public void CreatePlan_UsesFixedRepositoryAndIntegratedReleaseDirectory()
    {
        Directory.CreateDirectory(_root);
        var service = new ManagedReleaseDownloadService(Path.Combine(_root, "Scripts"));

        var plan = service.CreatePlan(ManagedToolKey.FantasyTools, _root);

        Assert.Equal("kirito0000001/FantasyTools", plan.RepositoryName);
        Assert.Equal(Path.Combine(_root, "FantasyTools-Release"), plan.TargetDirectory);
    }

    [Fact]
    public void CreatePlan_RejectsNonEmptyReleaseDirectory()
    {
        var target = Path.Combine(_root, "AxTools-Release");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.zip"), "keep");
        var service = new ManagedReleaseDownloadService(Path.Combine(_root, "Scripts"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.CreatePlan(ManagedToolKey.AxTools, _root));

        Assert.Contains("不是空目录", exception.Message);
        Assert.True(File.Exists(Path.Combine(target, "keep.zip")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
