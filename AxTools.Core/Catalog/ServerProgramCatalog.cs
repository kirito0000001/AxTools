using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 服务端更新档案。三个程序各自独立成库：一个裸仓库接收推送，一个外部工作树
/// 指向真正的运行目录。仓库目录名保持 ASCII，因为路径要经 Windows OpenSSH 传给远端。
/// </summary>
public static class ServerProgramCatalog
{
    public const string DefaultLocalRoot = @"D:\DabaoV\服务器Git合集";

    public const string RemoteRepoRoot = "/C:/Users/Administrator/Desktop/ServerRepos";

    public const string SshTarget = "crossing-server";

    public static IReadOnlyList<ServerProgramProfile> All { get; } =
    [
        new ServerProgramProfile(
            "crossingvoid",
            "零境交错",
            "零境交错",
            "crossingvoid",
            ["CrossingVoid-Login", "CrossingVoid-Main"],
            "登录大厅与主界面大厅共用同一份目录，更新或回退会一起重启。"),
        new ServerProgramProfile(
            "narutobp",
            "火影BP",
            "火影BP",
            "narutobp",
            ["NarutoBP"],
            "玩家创建的房间是同名进程派生的实例，切换时会一起结束。"),
        new ServerProgramProfile(
            "fantasyproject",
            "幻杀",
            "幻杀",
            "fantasyproject",
            ["FantasyProject"],
            "玩家创建的房间是同名进程派生的实例，切换时会一起结束。")
    ];

    public static ServerProgramProfile Get(string key) =>
        All.FirstOrDefault(profile => string.Equals(profile.Key, key, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(key), key, "未知的服务端程序。");

    public static string GetLocalRepoPath(ServerProgramProfile profile, string? localRoot = null) =>
        Path.Combine(localRoot ?? DefaultLocalRoot, profile.LocalFolderName);

    public static string GetRemoteRepoUrl(ServerProgramProfile profile) =>
        $"{SshTarget}:{RemoteRepoRoot}/{profile.RepositoryName}.git";
}
