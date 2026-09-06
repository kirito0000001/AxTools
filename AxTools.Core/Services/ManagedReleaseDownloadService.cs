using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record ManagedReleaseDownloadPlan(
    ManagedToolKey ToolKey,
    string RepositoryName,
    string TargetDirectory);

public sealed class ManagedReleaseDownloadService
{
    private readonly string _scriptPath;

    public ManagedReleaseDownloadService(string scriptsRoot)
    {
        _scriptPath = Path.GetFullPath(Path.Combine(
            scriptsRoot,
            "Release",
            "Download-ManagedToolRelease.ps1"));
    }

    public ManagedReleaseDownloadPlan CreatePlan(ManagedToolKey key, string selectedParentDirectory)
    {
        var repository = ManagedRepositoryCatalog.Get(key);
        var parent = Path.GetFullPath(selectedParentDirectory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"选择的下载位置不存在：{parent}");
        }
        var target = Path.Combine(parent, $"{repository.DirectoryName}-Release");
        if (File.Exists(target))
        {
            throw new InvalidOperationException($"目标路径已经被文件占用：{target}");
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException($"目标路径不是空目录：{target}");
        }
        return new ManagedReleaseDownloadPlan(key, repository.RepositoryName, target);
    }

    public AxTaskDefinition CreateTask(ManagedReleaseDownloadPlan plan) =>
        new(
            $"ReleaseDownload-{plan.ToolKey}-{Guid.NewGuid():N}",
            $"{plan.ToolKey} · 下载正式版",
            _scriptPath,
            [
                "-RepositoryName", plan.RepositoryName,
                "-DestinationPath", plan.TargetDirectory,
                "-ExpectedDirectoryName", Path.GetFileName(plan.TargetDirectory)
            ],
            IsHeavy: true);
}
