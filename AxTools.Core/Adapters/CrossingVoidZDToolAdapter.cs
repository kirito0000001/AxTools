using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class CrossingVoidZDToolAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.CrossingVoidZDTool,
    scriptsRoot,
    "CrossingVoidZDTool",
    ActionsList,
    "CrossingVoidZDTool.csproj",
    Path.Combine(
        "Tests",
        "CrossingVoidZDTool.RegressionTests",
        "CrossingVoidZDTool.RegressionTests.csproj"),
    "Pakout.ps1")
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildAndRun, "编译并启动", ManagedToolActionSection.Development, "\uE768", true),
        Action(ManagedToolAction.ForceBuildAndRun, "强制重新编译并启动", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.Test, "运行回归测试", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.CheckUnrealSyncEnvironment, "检查 Unreal 同步环境", ManagedToolActionSection.Development, "\uE90F", false),
        Action(ManagedToolAction.InspectArtifacts, "查看版本与产物时间", ManagedToolActionSection.Development, "\uE946", false),
        Action(ManagedToolAction.OpenDevelopmentOutput, "打开开发输出目录", ManagedToolActionSection.Development, "\uE838", false),
        Action(ManagedToolAction.OpenWorkspace, "打开用户工作区", ManagedToolActionSection.Development, "\uE838", false),
        Action(ManagedToolAction.RunRelease, "启动正式版", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.OpenReleaseDirectory, "打开正式版目录", ManagedToolActionSection.Release, "\uE838", false),
        Action(ManagedToolAction.ValidateAndStagePackage, "临时打包并验证", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.ReplaceRelease, "替换正式版", ManagedToolActionSection.Publish, "\uE74E", true, requiresConfirmation: true),
        // 热更新发布链（去 GitHub 化：只发 Gitee，见 Docs\热更新-发布侧需求.md §1）：
        // 版本号由 AX 设置保存、按 -Version 传给脚本，所以 RequiresVersion=false，
        // 不再走"线上版本门禁"（§6 的幂等本身就能挡住重复发布）。
        Action(ManagedToolAction.BuildUpdatePackage, "出更新包", ManagedToolActionSection.Publish, "\uE7B8", true),
        // 演练不写远端，和其它工具一致**不做**二次确认（回归测试 RealPublishingRequiresConfirmationButDryRunDoesNot 钉着这条）。
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, requiresConfirmation: true)
    ];

    private readonly string _publishWrapperPath = Path.GetFullPath(Path.Combine(
        scriptsRoot,
        "Adapters",
        "CrossingVoidZDTool",
        "Invoke-CrossingVoidZDToolPublish.ps1"));

    protected override string GetDisplayName() => "工具-零境素材";

    /// <summary>
    /// 这个工具的 12 个动作分成两套脚本：开发/替换正式版走原来的
    /// `Invoke-CrossingVoidZDToolAction.ps1`（ValidateSet 里没有发布动作，转过去会直接哑掉），
    /// 热更新发布链走新的 `Invoke-CrossingVoidZDToolPublish.ps1`。
    /// </summary>
    public override AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths)
    {
        var task = base.CreateTask(request, paths);
        if (!IsPublishPipelineAction(request.Action))
        {
            return task;
        }

        var arguments = task.Arguments;
        // 客户端更新器还是占位文件时，出包会被安全闸挡住（这是有意的）。
        // 但「发布演练」本来就不写远端，得能整条彩排 —— 给它开一道显式的口子：
        // 环境变量 AXTOOLS_ZD_ALLOW_STUB_UPDATER 非空即放行（带占位更新器的包绝不可发布）。
        if (request.Action == ManagedToolAction.PublishDryRun &&
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("AXTOOLS_ZD_ALLOW_STUB_UPDATER")))
        {
            arguments = [.. arguments, "-AllowStubUpdater"];
        }

        return task with { ScriptPath = _publishWrapperPath, Arguments = arguments };
    }

    internal static bool IsPublishPipelineAction(ManagedToolAction action) => action is
        ManagedToolAction.BuildUpdatePackage or
        ManagedToolAction.PublishDryRun or
        ManagedToolAction.Publish;
}
