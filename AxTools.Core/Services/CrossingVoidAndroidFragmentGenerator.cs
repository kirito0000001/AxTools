namespace AxTools.Core.Services;

/// <summary>
/// 安卓《生成碎片》的编排：定位 OBB/APK → 建计划 → 流式写片。
/// 规则在 <see cref="CrossingVoidAndroidFragmentPlanner"/>，落盘在
/// <see cref="CrossingVoidAndroidFragmentExecutor"/>；这里只负责把进度文字串起来。
/// </summary>
public static class CrossingVoidAndroidFragmentGenerator
{
    public static CrossingVoidAndroidFragmentPlan BuildPlan(
        string gameDirectory,
        string version,
        string archiveNamePrefix,
        string productKey = CrossingVoidAndroidObbSidecar.ProductKey,
        string versionMarkerFileName = CrossingVoidVersionMarker.FileName,
        string obbSidecarFileName = CrossingVoidAndroidObbSidecar.FileName) =>
        CrossingVoidAndroidFragmentPlanner.Build(
            gameDirectory,
            version,
            archiveNamePrefix,
            productKey: productKey,
            versionMarkerFileName: versionMarkerFileName,
            obbSidecarFileName: obbSidecarFileName);

    public static CrossingVoidFragmentResult Execute(
        CrossingVoidAndroidFragmentPlan plan,
        string outputDirectory,
        Action<CrossingVoidFragmentProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Report(
            onProgress,
            "scan",
            $"已定位 {plan.Package.ObbFileName}（{CrossingVoidFragmentExecutor.FormatBytes(plan.Package.ObbBytes)}）与主 APK。",
            2);
        var result = new CrossingVoidAndroidFragmentExecutor().Execute(
            plan,
            outputDirectory,
            onProgress,
            cancellationToken);
        Report(onProgress, "completed", "碎片已生成。", 100);
        return result;
    }

    public static CrossingVoidFragmentResult Generate(
        string gameDirectory,
        string outputDirectory,
        string version,
        string archiveNamePrefix,
        Action<CrossingVoidFragmentProgress>? onProgress = null,
        CancellationToken cancellationToken = default,
        string productKey = CrossingVoidAndroidObbSidecar.ProductKey,
        string versionMarkerFileName = CrossingVoidVersionMarker.FileName,
        string obbSidecarFileName = CrossingVoidAndroidObbSidecar.FileName)
    {
        var plan = BuildPlan(
            gameDirectory,
            version,
            archiveNamePrefix,
            productKey,
            versionMarkerFileName,
            obbSidecarFileName);
        return Execute(plan, outputDirectory, onProgress, cancellationToken);
    }

    private static void Report(
        Action<CrossingVoidFragmentProgress>? onProgress,
        string stage,
        string message,
        int percent) =>
        onProgress?.Invoke(new CrossingVoidFragmentProgress(stage, message, Math.Clamp(percent, 0, 100)));
}
