namespace AxTools.Core.Models;

/// <summary>
/// 游戏页里的一个代码项目（本体 / 启动器）。
///
/// 三个游戏固定各自两个项目，页面只认这个模型，不再为某个游戏写分支。
/// <see cref="DefaultPath"/> 是首次使用时的默认位置；用户在设置里改过之后以设置为准（后续接）。
/// </summary>
public sealed record GameProjectDefinition(
    string Id,
    string DisplayName,
    string Role,
    string DefaultPath,
    string RepositoryUrl,
    bool IsAndroidLauncher = false);

/// <summary>一个游戏的工程档案；三个游戏共用同一套页面，只在目录里各写一条。</summary>
public sealed record GameWorkbenchProfile(
    ManagedToolKey GameToolKey,
    string DisplayName,
    IReadOnlyList<GameProjectDefinition> Projects,
    bool HasAndroidLauncherByDefault = false,
    bool HasAndroidGameBuildByDefault = false);

/// <summary>
/// 项目在磁盘上的当前状态，供游戏页的通用面板直接绑定。
/// StateKey 取值：exists / missing / unconfigured。
/// </summary>
public sealed class GameProjectStatus
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>“本体项目”“启动器”这类角色说明。</summary>
    public required string Role { get; init; }

    /// <summary>本地路径；未配置时是空串。</summary>
    public required string Path { get; init; }

    public required string RepositoryUrl { get; init; }

    public required string StateKey { get; init; }

    public required string StateText { get; init; }

    /// <summary>路径那一行的展示文本：已配置给路径，未配置给一句说明。</summary>
    public required string DetailText { get; init; }

    public bool HasRepository => !string.IsNullOrWhiteSpace(RepositoryUrl);
}
