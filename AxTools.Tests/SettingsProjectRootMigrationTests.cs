using System.Text.Json;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class SettingsProjectRootMigrationTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.SettingsMigration",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChangeProjectRootAsync_CommitsSettingsRebindsHistoryAndDeletesOldRoot()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        var bootstrapPath = Path.Combine(_testRoot, "AppData", "AxTools", "bootstrap.json");
        var writer = new AtomicJsonFileService();
        var settingsService = new AppSettingsService(writer, bootstrapPath, () => oldRoot);
        var settings = new AppSettings { ProjectRootPath = oldRoot };
        WorkspaceArtifactPathPolicy.Apply(oldRoot, settings.ManagedTools);
        await settingsService.SaveAsync(settings, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(oldRoot, "TaskHistory"));
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "keep.txt"), "keep");
        var historyService = new TaskHistoryService(oldRoot, writer);
        var viewModel = new SettingsViewModel(
            settings,
            settingsService,
            Array.Empty<ToolPageViewModel>(),
            new RunnerDiagnosticsViewModel(null, "测试"),
            new ProjectRootMigrationService(),
            historyService);

        var result = await viewModel.ChangeProjectRootAsync(
            newRoot,
            progress: null,
            CancellationToken.None);

        Assert.Equal(newRoot, viewModel.ProjectRootPath);
        Assert.All(settings.ManagedTools, item => Assert.Equal(
            Path.Combine(newRoot, "Artifacts", item.Key),
            item.Value.OutputRoot));
        Assert.Equal(
            Path.Combine(newRoot, "Artifacts", "CrossingVoidinitiator-PC"),
            settings.CrossingVoidPackage.OutputDirectory);
        Assert.True(result.OldDirectoryDeleted);
        Assert.Equal(OperationNoticeSeverity.Success, viewModel.ProjectRootStatusSeverity);
        Assert.Equal("目录迁移完成", viewModel.ProjectRootStatusTitle);
        Assert.True(File.Exists(Path.Combine(newRoot, "keep.txt")));
        Assert.False(Directory.Exists(oldRoot));
        using var bootstrap = JsonDocument.Parse(await File.ReadAllTextAsync(bootstrapPath));
        Assert.Equal(newRoot, bootstrap.RootElement.GetProperty("projectRootPath").GetString());

        var started = DateTimeOffset.Now;
        var history = await historyService.SaveAsync(
            new AxTaskResult(
                "after-move",
                AxTaskStatus.Succeeded,
                0,
                started,
                started,
                "ok",
                Array.Empty<string>(),
                Array.Empty<AxTaskEvent>()),
            CancellationToken.None);
        Assert.StartsWith(newRoot, history.SummaryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeProjectRootAsync_BootstrapFailureRestoresOldRootAndRemovesTarget()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        var bootstrapPath = Path.Combine(_testRoot, "AppData", "AxTools", "bootstrap.json");
        var realWriter = new AtomicJsonFileService();
        var initialService = new AppSettingsService(realWriter, bootstrapPath, () => oldRoot);
        var settings = new AppSettings { ProjectRootPath = oldRoot };
        await initialService.SaveAsync(settings, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "keep.txt"), "keep");
        var failingWriter = new FailOnceForPathWriter(realWriter, bootstrapPath);
        var settingsService = new AppSettingsService(failingWriter, bootstrapPath, () => oldRoot);
        WorkspaceArtifactPathPolicy.Apply(oldRoot, settings.ManagedTools);
        var historyService = new TaskHistoryService(oldRoot, realWriter);
        var viewModel = new SettingsViewModel(
            settings,
            settingsService,
            Array.Empty<ToolPageViewModel>(),
            new RunnerDiagnosticsViewModel(null, "测试"),
            new ProjectRootMigrationService(),
            historyService);

        await Assert.ThrowsAsync<IOException>(() => viewModel.ChangeProjectRootAsync(
            newRoot,
            progress: null,
            CancellationToken.None));

        Assert.Equal(oldRoot, viewModel.ProjectRootPath);
        Assert.All(settings.ManagedTools, item => Assert.Equal(
            Path.Combine(oldRoot, "Artifacts", item.Key),
            item.Value.OutputRoot));
        Assert.Equal(
            Path.Combine(oldRoot, "Artifacts", "CrossingVoidinitiator-PC"),
            settings.CrossingVoidPackage.OutputDirectory);
        Assert.True(File.Exists(Path.Combine(oldRoot, "keep.txt")));
        Assert.False(Directory.Exists(newRoot));
        using var bootstrap = JsonDocument.Parse(await File.ReadAllTextAsync(bootstrapPath));
        Assert.Equal(oldRoot, bootstrap.RootElement.GetProperty("projectRootPath").GetString());
    }

    [Fact]
    public async Task ChangeProjectRootAsync_DeleteFailureKeepsCommittedNewRootAndReportsWarning()
    {
        var oldRoot = Path.Combine(_testRoot, "old", "Ax工具箱项目");
        var newRoot = Path.Combine(_testRoot, "new", "Ax工具箱项目");
        var bootstrapPath = Path.Combine(_testRoot, "AppData", "AxTools", "bootstrap.json");
        var writer = new AtomicJsonFileService();
        var settingsService = new AppSettingsService(writer, bootstrapPath, () => oldRoot);
        var settings = new AppSettings { ProjectRootPath = oldRoot };
        await settingsService.SaveAsync(settings, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "keep.txt"), "keep");
        var migrationService = new DeleteFailureMigrationService(oldRoot);
        var viewModel = new SettingsViewModel(
            settings,
            settingsService,
            Array.Empty<ToolPageViewModel>(),
            new RunnerDiagnosticsViewModel(null, "测试"),
            migrationService,
            new TaskHistoryService(oldRoot, writer));

        var result = await viewModel.ChangeProjectRootAsync(
            newRoot,
            progress: null,
            CancellationToken.None);

        Assert.Equal(newRoot, viewModel.ProjectRootPath);
        Assert.False(result.OldDirectoryDeleted);
        Assert.Equal("旧目录正在使用", result.CleanupError);
        Assert.Equal(OperationNoticeSeverity.Warning, viewModel.ProjectRootStatusSeverity);
        Assert.Equal("目录迁移完成，旧目录待清理", viewModel.ProjectRootStatusTitle);
        Assert.True(Directory.Exists(oldRoot));
        Assert.True(File.Exists(Path.Combine(newRoot, "keep.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class FailOnceForPathWriter(
        IAtomicTextFileService inner,
        string failingPath) : IAtomicTextFileService
    {
        private bool _hasFailed;

        public Task WriteTextAsync(
            string path,
            string content,
            CancellationToken cancellationToken)
        {
            if (!_hasFailed && string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(failingPath),
                StringComparison.OrdinalIgnoreCase))
            {
                _hasFailed = true;
                throw new IOException("模拟 bootstrap 写入失败");
            }

            return inner.WriteTextAsync(path, content, cancellationToken);
        }
    }

    private sealed class DeleteFailureMigrationService(string oldRoot)
        : IProjectRootMigrationService
    {
        private readonly ProjectRootMigrationService _inner = new();

        public Task<ProjectRootMigrationResult> CopyAndVerifyAsync(
            string sourceRoot,
            string targetRoot,
            IProgress<ProgressUpdate>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndVerifyAsync(
                sourceRoot,
                targetRoot,
                progress,
                cancellationToken);

        public bool TryDeleteDirectory(string path, out string? error)
        {
            if (ProjectRootMigrationService.PathsEqual(path, oldRoot))
            {
                error = "旧目录正在使用";
                return false;
            }

            return _inner.TryDeleteDirectory(path, out error);
        }
    }
}
