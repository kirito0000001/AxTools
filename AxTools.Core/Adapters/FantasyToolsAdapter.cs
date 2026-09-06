using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class FantasyToolsAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.FantasyTools,
    scriptsRoot,
    "FantasyTools",
    ActionsList,
    "FantasyTools.csproj",
    Path.Combine("Scripts", "打包工具箱.ps1"),
    Path.Combine("Scripts", "发布新版本.ps1"))
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildAndRun, "编译并启动", ManagedToolActionSection.Development, "\uE895", true),
        Action(ManagedToolAction.ForceBuildAndRun, "强制重新编译并启动", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.Build, "仅编译", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.RunRelease, "启动正式版", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.PackageStable, "打包正式版", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.PackageBeta, "打包测试版", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.ValidatePackage, "校验产物", ManagedToolActionSection.Publish, "\uE9D5", true),
        Action(ManagedToolAction.UploadDryRun, "上传演练", ManagedToolActionSection.Publish, "\uE898", true, true),
        Action(ManagedToolAction.Upload, "单独上传", ManagedToolActionSection.Publish, "\uE898", true, true, true),
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true, true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    protected override string GetDisplayName() => "FantasyTools";
}
