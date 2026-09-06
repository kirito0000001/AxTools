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
        Action(ManagedToolAction.ReplaceRelease, "替换正式版", ManagedToolActionSection.Publish, "\uE74E", true, requiresConfirmation: true)
    ];

    protected override string GetDisplayName() => "ZD空界幻境";
}
