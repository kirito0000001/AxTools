namespace AxTools.Core.Models;

/// <summary>
/// 游戏本体发布请求——把原来散在三处的参数收成一份：
/// 平台（PC/安卓）、产物目录、版本、通道、发布说明、是否补丁。
///
/// 以前这些字段分别从 ManagedToolPaths、页面属性、执行管道里各取各的，
/// 平台靠字符串比较、补丁与整包靠参数区分，很容易改一处漏一处。
/// 现在统一由 GamePublishRequestBuilder 组装，适配器与页面都只认这一份。
/// </summary>
public sealed record GamePublishRequest(
    ManagedToolKey ToolKey,
    string Platform,
    string PackageRoot,
    string Version,
    string Channel,
    string ReleaseNotes,
    bool IsPatch)
{
    public bool IsAndroid => string.Equals(Platform, "Android", StringComparison.Ordinal);

    /// <summary>给界面用的一句话摘要，避免各页面自己拼。</summary>
    public string Summary =>
        $"{(IsAndroid ? "安卓" : "PC")} · 版本 {(string.IsNullOrWhiteSpace(Version) ? "未填" : Version)}" +
        $" · 通道 {(string.IsNullOrWhiteSpace(Channel) ? "stable" : Channel)}" +
        (IsPatch ? " · 补丁包" : string.Empty);
}
