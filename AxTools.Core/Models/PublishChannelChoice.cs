namespace AxTools.Core.Models;

/// <summary>本次发布选中的渠道；只对这一次生效，不改设置。</summary>
public sealed record PublishChannelChoice(bool DownloadSite, bool GitHub)
{
    public static PublishChannelChoice Both { get; } = new(true, true);

    public bool IsValid => DownloadSite || GitHub;

    public string Describe() => (DownloadSite, GitHub) switch
    {
        (true, true) => "野草云 + GitHub",
        (true, false) => "仅野草云",
        (false, true) => "仅 GitHub",
        _ => "未选择渠道"
    };
}
