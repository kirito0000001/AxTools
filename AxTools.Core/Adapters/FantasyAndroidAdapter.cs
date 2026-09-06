using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class FantasyAndroidAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.FantasyAndroid,
    scriptsRoot,
    "CrossingVoidAndroid",
    ActionsList,
    "package.json")
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.BuildAndroidDebug, "构建 Debug APK", ManagedToolActionSection.Development, "\uE7B8", true),
        Action(ManagedToolAction.BuildAndroidAndInstall, "编译并安装开发版", ManagedToolActionSection.Development, "\uE768", true),
        Action(ManagedToolAction.BuildAndroidRelease, "构建 Release APK", ManagedToolActionSection.Release, "\uE7B8", true),
        Action(ManagedToolAction.RunRelease, "启动已安装版本", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    protected override string GetDisplayName() => "幻杀启动器-安卓";
}
