using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

/// <summary>
/// 三个游戏（零境 / 幻杀 / 火影）共用的一套「游戏包发布」适配器。
///
/// 动作集合、校验（必须先选产物目录）和命令行拼装完全一样；
/// 子类只提供自己的**产品段名**（决定 dl 上的目录 `games/&lt;产品段&gt;/…`、
/// 裸仓库 `game-&lt;产品段&gt;.git`，以及"线上版本"要查哪个清单目录）。
///
/// 真正的发布逻辑在通用内核 `Scripts\Publishing\Games\Publish-GameDownloadRelease.ps1`，
/// 这里只负责把动作翻译成参数。**产品段名和版本标记文件名不从这里下发**：
/// 每个游戏的包装脚本自己带给内核（`Scripts\Adapters\&lt;游戏&gt;\Invoke-&lt;游戏&gt;Action.ps1`），
/// 两边都发会打架（踩过：包装脚本不认识 -ProductSegment，绑参直接失败）。
/// </summary>
public abstract class GameUploadAdapter : ManagedToolAdapterBase
{
    protected GameUploadAdapter(
        ManagedToolKey key,
        string scriptsRoot,
        string wrapperDirectory,
        string displayName,
        string productSegment,
        string? gitHubRepository = null)
        : base(key, scriptsRoot, wrapperDirectory, BuildActions(gitHubRepository))
    {
        DisplayName = displayName;
        ProductSegment = productSegment;
        GitHubRepository = gitHubRepository;
    }

    private static IReadOnlyList<ManagedToolActionDefinition> BuildActions(string? gitHubRepository)
    {
        var actions = new List<ManagedToolActionDefinition>
        {
            Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
            Action(ManagedToolAction.CheckGamePackage, "只检查，不发布", ManagedToolActionSection.Publish, "\uE9D5", false, true),
            Action(ManagedToolAction.PublishGameVersion, "发布到服务器", ManagedToolActionSection.Publish, "\uE72D", true, true, true),
            Action(ManagedToolAction.PublishGamePatch, "发布补丁包", ManagedToolActionSection.Publish, "\uE8B7", true, true, true),
            Action(ManagedToolAction.GenerateGamePatchFragments, "生成补丁碎片", ManagedToolActionSection.Publish, "\uE7B8", true, true)
        };
        if (!string.IsNullOrWhiteSpace(gitHubRepository))
        {
            actions.Add(Action(
                ManagedToolAction.PublishGameVersionToGitHub,
                "发布到 GitHub",
                ManagedToolActionSection.Publish,
                "\uE72D",
                true,
                true,
                true));
        }

        return actions;
    }

    protected string DisplayName { get; }

    /// <summary>这个游戏配置的 GitHub 发布仓库；为空则界面不显示「发布到 GitHub」。</summary>
    public string? GitHubRepository { get; }

    /// <summary>PC 侧的产品段名；安卓侧自动加 `-android` 后缀。</summary>
    protected string ProductSegment { get; }

    /// <summary>给通用逻辑（线上版本检查）用的产品段。</summary>
    public override string? GameProductSegment => ProductSegment;

    /// <summary>
    /// 游戏工具没有源码目录，任务的 -ProjectRoot 就是当前平台的打包产物目录
    /// （内核只在"检查环境"里用它，真正的发布参数是 -GamePackageRoot）。
    /// </summary>
    protected override string ResolveProjectRootArgument(ManagedToolPaths paths) =>
        paths.GamePublishPlatform is "Android"
            ? paths.GamePackageRootAndroid
            : paths.GamePackageRoot;

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
                ManagedToolAction.PublishGameVersionToGitHub or
                ManagedToolAction.PublishGamePatch or
                ManagedToolAction.GenerateGamePatchFragments or
                ManagedToolAction.RebuildGameDownloadRepo))
        {
            return task;
        }

        var platform = paths.GamePublishPlatform is "Android" ? "Android" : "Windows";
        if (request.Action == ManagedToolAction.PublishGamePatch && platform != "Windows")
        {
            throw new InvalidOperationException("Windows 补丁渠道目前只支持 PC 游戏包。");
        }
        if (request.Action == ManagedToolAction.GenerateGamePatchFragments && platform != "Windows")
        {
            throw new InvalidOperationException("补丁碎片目前只支持 PC 游戏包。");
        }
        var packageRoot = platform is "Android" ? paths.GamePackageRootAndroid : paths.GamePackageRoot;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            throw new InvalidOperationException(platform is "Android"
                ? "请先选择安卓游戏包目录。"
                : "请先选择 PC 游戏包目录。");
        }

        // 产品段名和版本标记文件名**不从这里下发**：每个游戏的包装脚本自己带
        // （Scripts\Adapters\<游戏>\Invoke-<游戏>Action.ps1），内核再从包装脚本接手。
        // 同一份信息下发两次会打架——踩过的坑：包装脚本不认识 -ProductSegment，直接绑参失败。
        return task with
        {
            Arguments = task.Arguments.Concat(
            [
                "-Platform", platform,
                "-GamePackageRoot", packageRoot,
                "-ReleaseNotes", request.ReleaseNotes,
                "-FragmentOutputDirectory", paths.FragmentOutputDirectory
            ]).ToArray()
        };
    }
}
