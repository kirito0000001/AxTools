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
}
