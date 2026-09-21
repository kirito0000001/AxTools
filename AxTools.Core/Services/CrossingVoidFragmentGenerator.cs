using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 《生成碎片》的完整流程：扫游戏包目录 → 按清单分类 → 落盘。
///
/// 输入就是**打包产物目录**（用户在页面上选的那个），不读任何额外的清单文件：
/// 玩家拿到的是散件+压缩包，导入安装由启动器按它自己的清单校验。
///
/// 排除规则直接复用 <see cref="CrossingVoidPackageInspector.IsExcluded"/> —— 发布脚本和这里
/// 必须是同一套（.pdb / _download / .git / Saved\Logs / Saved\Crashes），
/// 否则碎片里会混进发布时根本不存在的东西，两边对不上。
/// </summary>
public sealed class CrossingVoidFragmentGenerator
{
    private readonly ICrossingVoidArchiver archiver;

    public CrossingVoidFragmentGenerator(ICrossingVoidArchiver archiver)
    {
        this.archiver = archiver;
    }

    /// <summary>
    /// 扫出要参与分发的文件（相对路径统一用 <c>/</c>，和清单里的写法一致）。
    /// 按路径排序：同一个目录重复生成时结果稳定，方便对拍。
    /// </summary>
    public static IReadOnlyList<(string Path, long SizeBytes)> ScanPackageFiles(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏包目录不能为空。", nameof(gameDirectory));
        }

        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"游戏包目录不存在：{root}");
        }

        var files = new List<(string Path, long SizeBytes)>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (CrossingVoidPackageInspector.IsExcluded(relativePath))
            {
                continue;
            }

            files.Add((relativePath, new FileInfo(path).Length));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
    }

    /// <summary>只分类不落盘（界面上的"本次内容"就用它）。</summary>
    public static CrossingVoidFragmentPlan BuildPlan(string gameDirectory) =>
        CrossingVoidFragmentPlanner.Plan(ScanPackageFiles(gameDirectory));

    public CrossingVoidFragmentResult Generate(
        string gameDirectory,
        string outputDirectory,
        Action<CrossingVoidFragmentProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Report(onProgress, "scan", "正在扫描游戏包目录…", 0);
        var files = ScanPackageFiles(gameDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("游戏包目录里没有可发出去的文件。");
        }

        var plan = CrossingVoidFragmentPlanner.Plan(files);
        Report(
            onProgress,
            "plan",
            $"已分类：分片 {plan.Chunks.Count} 个、影片 {plan.Videos.Count} 个、其余 {plan.Remainder.Count} 个。",
            2);

        var result = new CrossingVoidFragmentExecutor(archiver).Execute(
            gameDirectory,
            outputDirectory,
            plan,
            onProgress,
            cancellationToken);

        Report(onProgress, "completed", "碎片已生成。", 100);
        return result;
    }

    private static void Report(
        Action<CrossingVoidFragmentProgress>? onProgress,
        string stage,
        string message,
        int percent) =>
        onProgress?.Invoke(new CrossingVoidFragmentProgress(stage, message, Math.Clamp(percent, 0, 100)));
}
