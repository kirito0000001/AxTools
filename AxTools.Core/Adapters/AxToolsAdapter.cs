using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class AxToolsAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.AxTools,
    scriptsRoot,
    "AxTools",
    ActionsList,
    "AxTools.csproj",
    "AxTools.sln")
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildAndRun, "编译并启动", ManagedToolActionSection.Development, "\uE895", true),
        Action(ManagedToolAction.ForceBuildAndRun, "强制重新编译并启动", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.Build, "仅编译", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.Test, "执行测试", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.RunRelease, "启动正式版", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.PackageStable, "打包正式版", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.PackageBeta, "打包测试版", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.ValidatePackage, "校验产物", ManagedToolActionSection.Publish, "\uE9D5", true),
        Action(ManagedToolAction.UploadDryRun, "上传演练", ManagedToolActionSection.Publish, "\uE898", true, true),
        Action(ManagedToolAction.Upload, "单独上传", ManagedToolActionSection.Publish, "\uE898", true, true, true),
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true, true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    public override AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths)
    {
        var task = base.CreateTask(request, paths);
        if (request.Action is not (
            ManagedToolAction.PackageStable or
            ManagedToolAction.PackageBeta or
            ManagedToolAction.UploadDryRun or
            ManagedToolAction.Upload or
            ManagedToolAction.PublishDryRun or
            ManagedToolAction.Publish))
        {
            return task;
        }

        return task with
        {
            Arguments = [.. task.Arguments, "-ReleaseNotes", request.ReleaseNotes]
        };
    }

    protected override string GetDisplayName() => "Ax工具箱";
}
