using AxTools.Core.Models;

namespace AxTools.Core.Services;

/// <summary>
/// 发布相关文案与目标映射：原来写在 MainWindow 的确认弹窗里，挪到 Core 供各处复用。
/// </summary>
public static class PublishTargetCatalog
{
    public static string GetPublishTarget(ManagedToolKey key) => key switch
    {
        ManagedToolKey.FantasyTools => "FantasyTools 的 GitHub / Gitee 发布资产",
        ManagedToolKey.CrossingVoidPc => "零境启动器 PC 的 Gitee 发布资产",
        ManagedToolKey.CrossingVoidAndroid => "零境启动器 Android 的 Gitee 发布资产",
        _ => "该工具配置的远端发布目标"
    };

    public static bool IsGamePublishingAction(ManagedToolAction action) => action is
        ManagedToolAction.CheckGamePackage or
        ManagedToolAction.PublishGameVersion or
        ManagedToolAction.PublishGameVersionToGitHub or
        ManagedToolAction.PublishGamePatch or
        ManagedToolAction.GenerateGamePatchFragments or
        ManagedToolAction.RebuildGameDownloadRepo;

    /// <summary>动作在确认弹窗里配的说明，讲清"这一步会不会真的联网上传"。</summary>
    public static string GetIntent(ManagedToolAction action) => action switch
    {
        ManagedToolAction.CheckGamePackage =>
            "这一步只在本机扫描产物、算哈希、生成清单，不会提交也不会联网。",
        ManagedToolAction.PublishGameVersionToGitHub =>
            "这一步只在本机生成清单和版本目录，然后上传到 GitHub Release；不连下载站、不走 SFTP。",
        ManagedToolAction.PublishGamePatch =>
            "这一步只读取打包插件的 Version.json 和补丁文件，生成 Windows 补丁包并发布到 GitHub；不上传完整游戏包。",
        ManagedToolAction.GenerateGamePatchFragments =>
            "这一步只在本机按线上基线的变化文件生成补丁碎片 zip，不上传任何文件。",
        ManagedToolAction.RebuildGameDownloadRepo =>
            "这一步会重建本机暂存仓库、压成单提交（清掉历史对象）；代价是这次必须全量重传一次，耗时最长。只在仓库体积顶不住时用。",
        _ => "这是实际上传操作：按文件清单把这一版增量推到下载站，没有变化的文件不会重复上传。"
    };
}
