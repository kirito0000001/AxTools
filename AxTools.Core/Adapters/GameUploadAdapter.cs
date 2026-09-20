using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

/// <summary>
/// 三个游戏（零境 / 幻杀 / 火影）共用的一套「游戏包发布」适配器。
///
/// 动作集合、校验（必须先选产物目录）和命令行拼装完全一样；
/// 子类只提供自己的**产品段名**（决定 dl 上的目录 `games/&lt;产品段&gt;/…`
/// 和裸仓库 `game-&lt;产品段&gt;.git`）与**版本标记文件名**。
/// 安卓那一份的产品段固定是 `&lt;产品段&gt;-android`（零境已经这么上线了，保持一致）。
///
/// 真正的发布逻辑在通用内核 `Scripts\Publishing\Games\Publish-GameDownloadRelease.ps1`，
/// 这里只负责把动作翻译成参数。
/// </summary>
public abstract class GameUploadAdapter : ManagedToolAdapterBase
{
    protected GameUploadAdapter(
        ManagedToolKey key,
        string scriptsRoot,
        string wrapperDirectory,
        string displayName,
        string productSegment,
        string versionMarkerName)
        : base(key, scriptsRoot, wrapperDirectory, ActionsList)
    {
        DisplayName = displayName;
        ProductSegment = productSegment;
        VersionMarkerName = versionMarkerName;
    }

    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.CheckGamePackage, "只检查，不发布", ManagedToolActionSection.Publish, "\uE9D5", false, true),
        Action(ManagedToolAction.PublishGameVersion, "发布新版本", ManagedToolActionSection.Publish, "\uE72D", true, true, true),
        Action(ManagedToolAction.RebuildGameDownloadRepo, "重整仓库（压平历史）", ManagedToolActionSection.Publish, "\uE896", true, true, true)
    ];

    protected string DisplayName { get; }

    /// <summary>PC 侧的产品段名；安卓侧自动加 `-android` 后缀。</summary>
    protected string ProductSegment { get; }

    /// <summary>跟着产物上线的版本标记文件名（每个游戏一个，格式和零境一样）。</summary>
    protected string VersionMarkerName { get; }

    protected override string GetDisplayName() => DisplayName;

    /// <summary>
    /// 这个工具不构建源码，它的"项目"就是打包产物目录；因此校验以游戏包目录为准，
    /// 不再要求先配置源码目录（否则页面上所有按钮永远是灰的）。
    /// </summary>
    public override ManagedToolValidationResult Validate(ManagedToolPaths paths)
    {
        var packageRoot = paths.GamePublishPlatform is "Android"
            ? paths.GamePackageRootAndroid
            : paths.GamePackageRoot;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return ManagedToolValidationResult.Failure(
                paths.GamePublishPlatform is "Android" ? "请先选择安卓游戏包目录。" : "请先选择 PC 游戏包目录。");
        }

        try
        {
            if (!Directory.Exists(Path.GetFullPath(packageRoot)))
            {
                return ManagedToolValidationResult.Failure($"游戏包目录不存在：{packageRoot}");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ManagedToolValidationResult.Failure("游戏包目录格式无效。");
        }

        return ManagedToolValidationResult.Success("游戏包目录检查通过。");
    }

    public override AxTaskDefinition CreateTask(ManagedToolActionRequest request, ManagedToolPaths paths)
    {
        var task = base.CreateTask(request, paths);
        if (request.Action is not (
                ManagedToolAction.CheckGamePackage or
                ManagedToolAction.PublishGameVersion or
                ManagedToolAction.RebuildGameDownloadRepo))
        {
            return task;
        }

        var platform = paths.GamePublishPlatform is "Android" ? "Android" : "Windows";
        var packageRoot = platform is "Android" ? paths.GamePackageRootAndroid : paths.GamePackageRoot;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            throw new InvalidOperationException(platform is "Android"
                ? "请先选择安卓游戏包目录。"
                : "请先选择 PC 游戏包目录。");
        }

        var segment = platform is "Android" ? $"{ProductSegment}-android" : ProductSegment;
        return task with
        {
            Arguments = task.Arguments.Concat(
            [
                "-Platform", platform,
                "-GamePackageRoot", packageRoot,
                "-ReleaseNotes", request.ReleaseNotes,
                "-ProductSegment", segment,
                "-VersionMarkerName", VersionMarkerName
            ]).ToArray()
        };
    }
}
