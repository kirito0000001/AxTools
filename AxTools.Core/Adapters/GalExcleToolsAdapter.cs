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
        Action(ManagedToolAction.ValidatePackage, "校验打包产物", ManagedToolActionSection.Publish, "\uE9D5", true)
    ];

    protected override string GetDisplayName() => "剧情工具箱";
}
