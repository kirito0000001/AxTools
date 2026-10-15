using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class GalExcleToolsAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.GalExcleTools,
    scriptsRoot,
    "GalExcleTools",
    ActionsList,
    "GalExcleTools.csproj",
    "GalExcleTools.sln",
    Path.Combine("Scripts", "Test-SourceHealth.ps1"),
    Path.Combine("Scripts", "Package-App.ps1"))
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildAndRun, "编译并启动", ManagedToolActionSection.Development, "\uE895", true),
        Action(ManagedToolAction.ForceBuildAndRun, "强制重新编译并启动", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.Build, "仅编译", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.SourceHealthCheck, "源码健康检查", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.RunRelease, "启动正式版", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.PackageX64, "x64 正式打包", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.ValidatePackage, "校验打包产物", ManagedToolActionSection.Publish, "\uE9D5", true),
        // 热更新发布链（真相源在 TFACStorybox 工程自己的 Scripts\ 下，见
        // GalExcleTools\Scripts\Publish-GalExcelRelease.ps1）：出包 / 演练 / 完整发布。
        // 版本号由 AX 设置保存、按 -Version 传给脚本，所以 RequiresVersion=false，
        // 不再走"线上版本门禁"（脚本侧的幂等本身就能挡住重复发布）。
        Action(ManagedToolAction.BuildUpdatePackage, "出更新包", ManagedToolActionSection.Publish, "\uE7B8", true),
        // 演练不写远端，和其它工具一致**不做**二次确认。
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, requiresConfirmation: true)
    ];

    private readonly string _publishWrapperPath = Path.GetFullPath(Path.Combine(
        scriptsRoot,
        "Adapters",
        "GalExcleTools",
        "Invoke-GalExcleToolsPublish.ps1"));

    protected override string GetDisplayName() => "工具-剧情填表";

    /// <summary>
    /// 这个工具的动作分成两套脚本：开发/打包/校验走原来的
    /// `Invoke-GalExcleToolsAction.ps1`（ValidateSet 里没有发布动作，转过去会直接哑掉），
    /// 热更新发布链走新的 `Invoke-GalExcleToolsPublish.ps1`。
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
        // 环境变量 AXTOOLS_GALEXCEL_ALLOW_STUB_UPDATER 非空即放行（带占位更新器的包绝不可发布）。
        if (request.Action == ManagedToolAction.PublishDryRun &&
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("AXTOOLS_GALEXCEL_ALLOW_STUB_UPDATER")))
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
