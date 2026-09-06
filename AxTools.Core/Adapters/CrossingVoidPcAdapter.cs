using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public sealed class CrossingVoidPcAdapter(string scriptsRoot) : ManagedToolAdapterBase(
    ManagedToolKey.CrossingVoidPc,
    scriptsRoot,
    "CrossingVoidPc",
    ActionsList,
    "package.json",
    Path.Combine("src-tauri", "Cargo.toml"),
    Path.Combine("Scripts", "Build-LauncherUpdaterPackage.ps1"),
    Path.Combine("Scripts", "Publish-LauncherGiteePackage.ps1"))
{
    private const string UnrealProjectRoot = @"D:\UnrealMap\CrossingVoid";

    private static readonly IReadOnlyList<ManagedToolActionDefinition> ActionsList =
    [
        Action(ManagedToolAction.CheckEnvironment, "检查环境", ManagedToolActionSection.Development, "\uE9D9", false),
        Action(ManagedToolAction.RunDevelopment, "编译并启动", ManagedToolActionSection.Development, "\uE768", true),
        Action(ManagedToolAction.ForceBuildAndRun, "强制重新编译并启动", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.BuildFrontend, "构建前端", ManagedToolActionSection.Development, "\uE896", true),
        Action(ManagedToolAction.TestFrontend, "前端测试", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.TestRust, "Rust 测试", ManagedToolActionSection.Development, "\uE9D5", true),
        Action(ManagedToolAction.RunRelease, "启动正式版", ManagedToolActionSection.Release, "\uE768", false),
        Action(ManagedToolAction.BuildLauncherPackage, "构建启动器包", ManagedToolActionSection.Publish, "\uE7B8", true, true),
        Action(ManagedToolAction.ValidatePackage, "校验启动器包", ManagedToolActionSection.Publish, "\uE9D5", true),
        Action(ManagedToolAction.UploadDryRun, "上传演练", ManagedToolActionSection.Publish, "\uE898", true, true),
        Action(ManagedToolAction.Upload, "单独上传", ManagedToolActionSection.Publish, "\uE898", true, true, true),
        Action(ManagedToolAction.PublishDryRun, "发布演练", ManagedToolActionSection.Publish, "\uE72D", true, true),
        Action(ManagedToolAction.Publish, "完整发布", ManagedToolActionSection.Publish, "\uE72D", true, true, true)
    ];

    public override ManagedToolValidationResult Validate(ManagedToolPaths paths)
    {
        if (IsInsideUnrealProject(paths.SourceRoot) || IsInsideUnrealProject(paths.OutputRoot))
        {
            return ManagedToolValidationResult.Failure(
                "拒绝访问虚幻项目目录；这里只能管理零境启动器 PC。");
        }

        return base.Validate(paths);
    }

    public override AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths)
    {
        if (IsInsideUnrealProject(paths.SourceRoot) || IsInsideUnrealProject(paths.OutputRoot))
        {
            throw new InvalidOperationException(
                "拒绝访问虚幻项目目录；未创建启动器任务。");
        }

        var task = base.CreateTask(request, paths);
        return task;
    }

    protected override string GetDisplayName() => "零境启动器 PC";

    private static bool IsInsideUnrealProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var forbidden = Path.GetFullPath(UnrealProjectRoot).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(candidate, forbidden, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(forbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
