using System.Text;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class TaskHistoryServiceTests
{
    [Fact]
    public async Task SaveAsync_WritesAtomicSummaryAndUtf8LogUnderProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"AxToolsHistory-{Guid.NewGuid():N}");
        var service = new TaskHistoryService(root, new AtomicJsonFileService());
        var started = new DateTimeOffset(2026, 7, 24, 12, 34, 56, TimeSpan.FromHours(8));
        var result = new AxTaskResult(
            "诊断:success/unsafe",
            AxTaskStatus.Succeeded,
            0,
            started,
            started.AddSeconds(2),
            "任务成功。",
            new[] { "普通输出", "中文日志" },
            new[] { new AxTaskEvent(AxTaskEventType.Result, ResultStatus: AxTaskResultStatus.Succeeded, ExitCode: 0) });

        try
        {
            var paths = await service.SaveAsync(result, CancellationToken.None);

            Assert.StartsWith(Path.Combine(root, "TaskHistory", "20260724"), paths.SummaryPath);
            Assert.DoesNotContain(':', Path.GetFileName(paths.SummaryPath));
            Assert.DoesNotContain('/', Path.GetFileName(paths.SummaryPath));
            Assert.True(File.Exists(paths.SummaryPath));
            Assert.True(File.Exists(paths.LogPath));
            Assert.Contains("\"status\": \"succeeded\"", await File.ReadAllTextAsync(paths.SummaryPath));
            Assert.Equal("普通输出\r\n中文日志", await File.ReadAllTextAsync(paths.LogPath));
            var logBytes = await File.ReadAllBytesAsync(paths.LogPath);
            Assert.False(logBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths.SummaryPath)!, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rebind_WritesNextHistoryOnlyUnderNewProjectRoot()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"AxToolsHistory-{Guid.NewGuid():N}");
        var oldRoot = Path.Combine(testRoot, "old");
        var newRoot = Path.Combine(testRoot, "new");
        var service = new TaskHistoryService(oldRoot, new AtomicJsonFileService());
        var started = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.FromHours(8));
        var result = new AxTaskResult(
            "after-migration",
            AxTaskStatus.Succeeded,
            0,
            started,
            started.AddSeconds(1),
            "成功",
            Array.Empty<string>(),
            Array.Empty<AxTaskEvent>());

        try
        {
            service.Rebind(newRoot);
            var paths = await service.SaveAsync(result, CancellationToken.None);

            Assert.StartsWith(
                Path.GetFullPath(newRoot),
                paths.SummaryPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(oldRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
