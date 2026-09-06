using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class CrossingVoidGameAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.CrossingVoidGame,
    scriptsRoot,
    "CrossingVoidGame",
    ActionsList)
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildGameChunks, "制作游戏分片", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.UploadGameChunks, "上传游戏分片", ManagedToolActionSection.Publish, "\uE898", true, true, true),
        Action(ManagedToolAction.PublishGamePackage, "制作并上传游戏", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    protected override string GetDisplayName() => "零境交错：空界幻境";

    public override AxTaskDefinition CreateTask(ManagedToolActionRequest request, ManagedToolPaths paths)
    {
        var task = base.CreateTask(request, paths);
        if (request.Action is not (ManagedToolAction.BuildGameChunks or ManagedToolAction.UploadGameChunks or ManagedToolAction.PublishGamePackage))
        {
            return task;
        }

        if (string.IsNullOrWhiteSpace(paths.GamePackageRoot))
        {
            throw new InvalidOperationException("请先选择游戏包目录。 ");
        }

        var platform = paths.GamePublishPlatform is "Android" ? "Android" : "Windows";
        return task with
        {
            Arguments = task.Arguments.Concat(["-Platform", platform, "-GamePackageRoot", paths.GamePackageRoot, "-ReleaseNotes", request.ReleaseNotes]).ToArray()
        };
    }
}
