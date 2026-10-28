using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 组装游戏本体发布请求：把平台、产物目录、补丁限制、版本与通道集中判断一次。
/// 适配器造任务和页面做门禁都走这里，避免两处规则分叉。
/// </summary>
public static class GamePublishRequestBuilder
{
    public static bool TryBuild(
        ManagedToolKey toolKey,
        ManagedToolPaths paths,
        ManagedToolAction action,
        string releaseNotes,
        out GamePublishRequest request,
        out string error)
    {
        request = null!;
        error = string.Empty;

        var platform = paths.GamePublishPlatform is "Android" ? "Android" : "Windows";
        var isPatch = action is ManagedToolAction.PublishGamePatch or
            ManagedToolAction.GenerateGamePatchFragments;

        if (isPatch && platform != "Windows")
        {
            error = action == ManagedToolAction.PublishGamePatch
                ? "Windows 补丁渠道目前只支持 PC 游戏包。"
                : "补丁碎片目前只支持 PC 游戏包。";
            return false;
        }

        var packageRoot = platform == "Android"
            ? paths.GamePackageRootAndroid
            : paths.GamePackageRoot;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            error = platform == "Android"
                ? "请先选择安卓游戏包目录。"
                : "请先选择 PC 游戏包目录。";
            return false;
        }

        request = new GamePublishRequest(
            toolKey,
            platform,
            packageRoot,
            paths.GamePublishVersion,
            string.IsNullOrWhiteSpace(paths.GamePublishChannel) ? "stable" : paths.GamePublishChannel,
            releaseNotes,
            isPatch);
        return true;
    }
}
