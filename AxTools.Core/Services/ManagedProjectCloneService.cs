using System.Text.Json;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record ManagedProjectClonePlan(
    ManagedToolKey ToolKey,
    string RepositoryUrl,
    string TargetDirectory,
    IReadOnlyList<string> ExpectedMarkers);

public sealed class ManagedProjectCloneService
{
    private readonly string _scriptPath;

    public ManagedProjectCloneService(string scriptsRoot)
    {
        _scriptPath = Path.GetFullPath(Path.Combine(
            scriptsRoot,
            "Git",
            "Clone-ManagedProject.ps1"));
    }

    public ManagedProjectClonePlan CreatePlan(ManagedToolKey key, string selectedParentDirectory)
    {
        var repository = ManagedRepositoryCatalog.Get(key);
        if (string.IsNullOrWhiteSpace(selectedParentDirectory))
        {
            throw new ArgumentException("请选择项目下载位置。", nameof(selectedParentDirectory));
        }

        var parent = Path.GetFullPath(selectedParentDirectory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"选择的下载位置不存在：{parent}");
        }
        var target = Path.Combine(parent, repository.DirectoryName);
        if (File.Exists(target))
        {
            throw new InvalidOperationException($"目标路径已经被文件占用：{target}");
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException($"目标路径不是空目录：{target}");
        }

        return new ManagedProjectClonePlan(
            key,
            repository.RepositoryUrl,
            target,
            repository.ExpectedMarkers);
    }

    public AxTaskDefinition CreateTask(ManagedProjectClonePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new AxTaskDefinition(
            $"Clone-{plan.ToolKey}-{Guid.NewGuid():N}",
            $"{plan.ToolKey} · 从 GitHub 下载项目",
            _scriptPath,
            [
                "-RepositoryUrl", plan.RepositoryUrl,
                "-DestinationPath", plan.TargetDirectory,
                "-ExpectedDirectoryName", Path.GetFileName(plan.TargetDirectory),
                "-ExpectedMarkersJson", JsonSerializer.Serialize(plan.ExpectedMarkers)
            ],
            IsHeavy: true);
    }

}
