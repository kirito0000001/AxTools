namespace AxTools.Core.Models;

public sealed class ManagedToolPaths
{
    public string SourceRoot { get; set; } = string.Empty;

    public string DevelopmentExecutable { get; set; } = string.Empty;

    public string ReleaseExecutable { get; set; } = string.Empty;

    public string OutputRoot { get; set; } = string.Empty;

    public string PublishVersion { get; set; } = string.Empty;

    public string PublishChannel { get; set; } = "stable";

    public string PublishReleaseNotes { get; set; } = string.Empty;

    /// <summary>游戏包目录（PC）。安卓那份单独记，切平台时自动换。</summary>
    public string GamePackageRoot { get; set; } = string.Empty;

    /// <summary>游戏包目录（安卓）。和 PC 那份分开记，因为两边是两个不同的产物目录。</summary>
    public string GamePackageRootAndroid { get; set; } = string.Empty;

    /// <summary>
    /// 《生成碎片》的源目录（PC/安卓各自记）。留空时沿用对应游戏包目录，
    /// 这样旧配置不需要迁移；换过产物目录后可以单独改这里。
    /// </summary>
    public string FragmentSourceDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 《生成碎片》的输出目录。留空时按工具页给默认（`D:\零境交错PC碎片` / `D:\零境交错安卓碎片`）——
    /// 不预填死值，是因为默认值要等到用户真去点那个按钮时才算得准。
    /// </summary>
    public string FragmentOutputDirectory { get; set; } = string.Empty;

    public string GamePublishVersion { get; set; } = string.Empty;

    public string GamePublishChannel { get; set; } = "stable";

    public string GamePublishPlatform { get; set; } = "Windows";

    public string GamePublishReleaseNotes { get; set; } = string.Empty;

    /// <summary>
    /// 这个游戏是否拥有安卓启动器。
    /// null = 还没改过，按 GameWorkbenchCatalog 的默认值算；改过之后以这里为准。
    /// 关掉以后安卓那个项目不再参与开发环境检查，也不需要填路径。
    /// </summary>
    public bool? HasAndroidLauncher { get; set; }

    /// <summary>
    /// 这个游戏是否有安卓版本（安卓游戏包）。和「是否有安卓启动器」是两件事：
    /// 安卓游戏指的是游戏本体的 Android 产物，启动器是另一套工程。
    /// null = 按目录默认值。
    /// </summary>
    public bool? HasAndroidGameBuild { get; set; }

    /// <summary>发布开关：PC 游戏包（第三片）。null = 开启。</summary>
    public bool? PublishPcGame { get; set; }

    /// <summary>发布开关：安卓游戏包（第四片）。null = 跟随是否有安卓游戏。</summary>
    public bool? PublishAndroidGame { get; set; }

    /// <summary>上传渠道开关：是否允许发布到下载站（野草云）。null = 开启。</summary>
    public bool? PublishGameToDownloadSite { get; set; }

    /// <summary>上传渠道开关：是否允许发布到 GitHub Release。null = 开启。</summary>
    public bool? PublishGameToGitHub { get; set; }

    /// <summary>
    /// 游戏项目的本地路径，按项目 Id 记（client = 本体，android = 安卓启动器）。
    /// 没写过的 Id 用 GameWorkbenchCatalog 里的默认路径；写过（哪怕写成空串）就以这里为准。
    /// </summary>
    public Dictionary<string, string> ProjectPaths { get; set; } = new(StringComparer.Ordinal);
}
