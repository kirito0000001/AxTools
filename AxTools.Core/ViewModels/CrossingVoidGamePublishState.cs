using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

/// <summary>
/// 一个游戏（零境 / 幻杀 / 火影）的游戏包发布状态：版本号、Release 介绍、
/// 以及"线上最新版本"的检查结果。三个游戏各持有一份，PC 与安卓共用同一份。
/// </summary>
public sealed class CrossingVoidGamePublishState : ObservableObject
{
    private readonly ManagedToolPaths _pcPaths;
    private readonly ManagedToolPaths _androidPaths;
    private string _version;
    private string _channel;
    private string _releaseNotes;
    private bool _isChecking;
    private bool? _onlineVersionsAligned;
    private bool _publishedCheckSucceeded;
    private bool _hasNoPublishedVersion;
    private string? _onlineVersion;
    private string? _pcOnlineVersion;
    private string? _androidOnlineVersion;
    private string _publishedVersionText = "尚未检查线上双端版本。";
    private string _versionCheckStatus = "进入发布页后将自动检查 PC 与 Android。";

    public CrossingVoidGamePublishState(
        ManagedToolPaths pcPaths,
        ManagedToolPaths androidPaths)
    {
        _pcPaths = pcPaths;
        _androidPaths = androidPaths;
        _version = ResolveSharedValue(pcPaths.GamePublishVersion, androidPaths.GamePublishVersion);
        _channel = "stable";
        _releaseNotes = ResolveSharedValue(
            pcPaths.GamePublishReleaseNotes,
            androidPaths.GamePublishReleaseNotes,
            StringComparison.Ordinal);
        SynchronizePaths();
    }

    public string Version
    {
        get => _version;
        set
        {
            if (SetProperty(ref _version, value?.Trim() ?? string.Empty))
            {
                SynchronizePaths();
            }
        }
    }

    /// <summary>
    /// 渠道暂时固定 stable：目前没有余力开测试服，界面上那个下拉也一起撤掉了。
    /// 等真有测试服时，这里和界面上的下拉一起加回来。
    /// </summary>
    public string Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, "stable"))
            {
                SynchronizePaths();
            }
        }
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        set
        {
            if (SetProperty(ref _releaseNotes, value ?? string.Empty))
            {
                SynchronizePaths();
            }
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        set => SetProperty(ref _isChecking, value);
    }

    public bool? OnlineVersionsAligned
    {
        get => _onlineVersionsAligned;
        private set => SetProperty(ref _onlineVersionsAligned, value);
    }

    public bool PublishedCheckSucceeded
    {
        get => _publishedCheckSucceeded;
        private set => SetProperty(ref _publishedCheckSucceeded, value);
    }

    public bool HasNoPublishedVersion
    {
        get => _hasNoPublishedVersion;
        private set => SetProperty(ref _hasNoPublishedVersion, value);
    }

    public string? OnlineVersion
    {
        get => _onlineVersion;
        private set => SetProperty(ref _onlineVersion, value);
    }

    public string? PcOnlineVersion
    {
        get => _pcOnlineVersion;
        private set => SetProperty(ref _pcOnlineVersion, value);
    }

    public string? AndroidOnlineVersion
    {
        get => _androidOnlineVersion;
        private set => SetProperty(ref _androidOnlineVersion, value);
    }

    /// <summary>
    /// 当前平台的线上版本。PC 与安卓各发各的，所以版本门禁按平台比，
    /// 另一个平台落后不影响这个平台。
    /// </summary>
    public string? OnlineVersionFor(bool android) => android ? AndroidOnlineVersion : PcOnlineVersion;

    internal SemaphoreSlim CheckLock { get; } = new(1, 1);

    public string PublishedVersionText
    {
        get => _publishedVersionText;
        private set => SetProperty(ref _publishedVersionText, value);
    }

    public string VersionCheckStatus
    {
        get => _versionCheckStatus;
        set => SetProperty(ref _versionCheckStatus, value);
    }

    public void ApplyPublishedVersions(CrossingVoidGamePublishedVersionResult result)
    {
        if (result.CheckFailed)
        {
            MarkPublishedCheckFailed(result.Status);
            return;
        }

        PublishedCheckSucceeded = true;
        HasNoPublishedVersion = result.PcVersion is null && result.AndroidVersion is null;
        OnlineVersion = result.UnifiedVersion;
        PcOnlineVersion = result.PcVersion;
        AndroidOnlineVersion = result.AndroidVersion;
        OnlineVersionsAligned = result.IsAligned;
        PublishedVersionText = result.IsAligned
            ? $"线上当前统一版本：{result.UnifiedVersion}"
            : $"线上版本：PC {DescribeVersion(result.PcVersion)}；Android {DescribeVersion(result.AndroidVersion)}";
        VersionCheckStatus = result.Status;
    }

    /// <summary>没读到版本号时不要写成"未发布"——也可能是检查失败，具体原因在下面那行状态里。</summary>
    private static string DescribeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "未读取到" : version;

    public void MarkPublishedCheckFailed(string message)
    {
        PublishedCheckSucceeded = false;
        HasNoPublishedVersion = false;
        OnlineVersion = null;
        PcOnlineVersion = null;
        AndroidOnlineVersion = null;
        OnlineVersionsAligned = null;
        PublishedVersionText = "线上版本：检查失败";
        VersionCheckStatus = message;
    }

    private void SynchronizePaths()
    {
        _pcPaths.GamePublishVersion = _version;
        _androidPaths.GamePublishVersion = _version;
        _pcPaths.GamePublishChannel = _channel;
        _androidPaths.GamePublishChannel = _channel;
        _pcPaths.GamePublishReleaseNotes = _releaseNotes;
        _androidPaths.GamePublishReleaseNotes = _releaseNotes;
    }

    private static string ResolveSharedValue(
        string pc,
        string android,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        string.Equals(pc?.Trim(), android?.Trim(), comparison)
            ? pc?.Trim() ?? string.Empty
            : string.IsNullOrWhiteSpace(pc)
                ? android?.Trim() ?? string.Empty
                : string.IsNullOrWhiteSpace(android)
                    ? pc.Trim()
                    : string.Empty;

}
