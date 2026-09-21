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
    /// 《生成碎片》的输出目录。留空时按工具页给默认（`D:\零境交错PC碎片` / `D:\零境交错安卓碎片`）——
    /// 不预填死值，是因为默认值要等到用户真去点那个按钮时才算得准。
    /// </summary>
    public string FragmentOutputDirectory { get; set; } = string.Empty;

    public string GamePublishVersion { get; set; } = string.Empty;

    public string GamePublishChannel { get; set; } = "stable";

    public string GamePublishPlatform { get; set; } = "Windows";

    public string GamePublishReleaseNotes { get; set; } = string.Empty;
}
