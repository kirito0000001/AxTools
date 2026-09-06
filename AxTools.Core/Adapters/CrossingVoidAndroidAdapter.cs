using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class CrossingVoidAndroidAdapter : ManagedToolAdapterBase
{
    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.TestFrontend, "前端测试", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.BuildFrontend, "构建前端", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.BuildAndroidDebug, "构建 Debug APK", ManagedToolActionSection.Development, "\uE7B8", true),
        Action(ManagedToolAction.BuildAndroidAndInstall, "编译并安装开发版", ManagedToolActionSection.Development, "\uE768", true),
        Action(ManagedToolAction.BuildAndroidRelease, "构建 Release APK", ManagedToolActionSection.Release, "\uE7B8", true),
        Action(ManagedToolAction.ListAndroidDevices, "查看 ADB 设备", ManagedToolActionSection.Release, "\u9D9A", false),
        Action(ManagedToolAction.StopAndroidApp, "停止设备端启动器", ManagedToolActionSection.Release, "\uE71A", false),
        Action(ManagedToolAction.RunRelease, "启动已安装版本", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true, true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    private readonly ToolchainSettings _toolchains;

    public CrossingVoidAndroidAdapter(string scriptsRoot, ToolchainSettings toolchains)
        : base(
            ManagedToolKey.CrossingVoidAndroid,
            scriptsRoot,
            "CrossingVoidAndroid",
            ActionsList,
            "package.json",
            Path.Combine("android", "gradlew.bat"),
            Path.Combine("android", "app", "build.gradle"),
            Path.Combine("android", "gradle", "wrapper", "gradle-wrapper.properties"),
            Path.Combine("Scripts", "Publish-AndroidLauncher.ps1"))
    {
        _toolchains = toolchains;
    }

    public override AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths)
    {
        var task = base.CreateTask(request, paths);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathPrefixes = new List<string>();

        var androidJavaHome = !string.IsNullOrWhiteSpace(_toolchains.AndroidJavaHome)
            ? _toolchains.AndroidJavaHome
            : _toolchains.JavaHome;
        if (!string.IsNullOrWhiteSpace(androidJavaHome))
        {
            environment["JAVA_HOME"] = androidJavaHome;
            pathPrefixes.Add(Path.Combine(androidJavaHome, "bin"));
        }

        if (!string.IsNullOrWhiteSpace(_toolchains.AndroidSdkRoot))
        {
            environment["ANDROID_HOME"] = _toolchains.AndroidSdkRoot;
            environment["ANDROID_SDK_ROOT"] = _toolchains.AndroidSdkRoot;
            pathPrefixes.Add(Path.Combine(_toolchains.AndroidSdkRoot, "platform-tools"));
        }

        environment["PATH"] = string.Join(
            Path.PathSeparator,
            pathPrefixes.Append(Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
        return task with { EnvironmentVariables = environment };
    }

    protected override string GetDisplayName() => "零境启动器 Android";

}
