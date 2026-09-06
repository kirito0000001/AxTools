using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ProjectRootMigrationServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyAndVerifyAsync_CopiesFilesWithoutDeletingSource()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        var nestedFile = Path.Combine(oldRoot, "TaskHistory", "20260728", "task.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
        await File.WriteAllTextAsync(nestedFile, "迁移测试");

        var service = new ProjectRootMigrationService();
        var result = await service.CopyAndVerifyAsync(
            oldRoot,
            newRoot,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        Assert.Equal("迁移测试", await File.ReadAllTextAsync(
            Path.Combine(newRoot, "TaskHistory", "20260728", "task.json")));
        Assert.True(Directory.Exists(oldRoot));
        Assert.True(File.Exists(nestedFile));
    }

    [Fact]
    public async Task CopyAndVerifyAsync_RejectsNonEmptyTargetDirectory()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        var existingFile = Path.Combine(newRoot, "keep.txt");
        await File.WriteAllTextAsync(existingFile, "不能覆盖");

        var service = new ProjectRootMigrationService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyAndVerifyAsync(
                oldRoot,
                newRoot,
                progress: null,
                CancellationToken.None));

        Assert.Contains("不是空目录", exception.Message);
        Assert.Equal("不能覆盖", await File.ReadAllTextAsync(existingFile));
    }

    [Fact]
    public async Task CopyAndVerifyAsync_RejectsTargetInsideSourceDirectory()
    {
        var oldRoot = Path.Combine(_testRoot, "Ax工具箱项目");
        var newRoot = Path.Combine(oldRoot, "moved", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProjectRootMigrationService().CopyAndVerifyAsync(
                oldRoot,
                newRoot,
                progress: null,
                CancellationToken.None));

        Assert.Contains("不能位于旧整体项目目录内部", exception.Message);
        Assert.False(Directory.Exists(newRoot));
    }

    [Fact]
    public async Task CopyAndVerifyAsync_RejectsTargetContainingSourceDirectory()
    {
        var newRoot = Path.Combine(_testRoot, "Ax工具箱项目");
        var oldRoot = Path.Combine(newRoot, "legacy", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProjectRootMigrationService().CopyAndVerifyAsync(
                oldRoot,
                newRoot,
                progress: null,
                CancellationToken.None));

        Assert.Contains("不能包含旧整体项目目录", exception.Message);
    }

    [Fact]
    public async Task CopyAndVerifyAsync_CancellationRemovesNewTargetAndKeepsSource()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "second.txt"), "second");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<AxTools.Core.Models.ProgressUpdate>(update =>
        {
            if (update.Message.Contains("复制", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProjectRootMigrationService().CopyAndVerifyAsync(
                oldRoot,
                newRoot,
                progress,
                cancellation.Token));

        Assert.True(File.Exists(Path.Combine(oldRoot, "first.txt")));
        Assert.True(File.Exists(Path.Combine(oldRoot, "second.txt")));
        Assert.False(Directory.Exists(newRoot));
    }

    [Fact]
    public async Task CopyAndVerifyAsync_RejectsInsufficientTargetFreeSpace()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "settings.json"), "data");
        var service = new ProjectRootMigrationService(_ => 0);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.CopyAndVerifyAsync(
                oldRoot,
                newRoot,
                progress: null,
                CancellationToken.None));

        Assert.Contains("可用空间不足", exception.Message);
        Assert.False(Directory.Exists(newRoot));
    }

    [Fact]
    public async Task CopyAndVerifyAsync_CreatesTargetWhenSourceDoesNotExist()
    {
        var oldRoot = Path.Combine(_testRoot, "missing", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");

        var result = await new ProjectRootMigrationService().CopyAndVerifyAsync(
            oldRoot,
            newRoot,
            progress: null,
            CancellationToken.None);

        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.DirectoryCount);
        Assert.True(Directory.Exists(newRoot));
    }

    [Fact]
    public void TryDeleteDirectory_RemovesCommittedSourceDirectory()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        Directory.CreateDirectory(oldRoot);
        File.WriteAllText(Path.Combine(oldRoot, "old.txt"), "old");

        var deleted = new ProjectRootMigrationService().TryDeleteDirectory(
            oldRoot,
            out var error);

        Assert.True(deleted);
        Assert.Null(error);
        Assert.False(Directory.Exists(oldRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
