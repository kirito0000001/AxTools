using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

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
    private string _publishedVersionText = "尚未检查线上双端版本。";
    private string _versionCheckStatus = "进入发布页后将自动检查 PC 与 Android。";

    public CrossingVoidGamePublishState(
        ManagedToolPaths pcPaths,
        ManagedToolPaths androidPaths)
    {
        _pcPaths = pcPaths;
        _androidPaths = androidPaths;
        _version = ResolveSharedValue(pcPaths.GamePublishVersion, androidPaths.GamePublishVersion);
        _channel = ResolveSharedChannel(pcPaths.GamePublishChannel, androidPaths.GamePublishChannel);
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

    public string Channel
    {
        get => _channel;
        set
        {
            var normalized = value is "beta" ? "beta" : "stable";
            if (SetProperty(ref _channel, normalized))
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
        PublishedCheckSucceeded = true;
        HasNoPublishedVersion = result.PcVersion is null && result.AndroidVersion is null;
        OnlineVersion = result.UnifiedVersion;
        OnlineVersionsAligned = result.IsAligned;
        PublishedVersionText = result.IsAligned
            ? $"线上当前统一版本：{result.UnifiedVersion}"
            : $"线上版本：PC {result.PcVersion ?? "未发布"}；Android {result.AndroidVersion ?? "未发布"}";
        VersionCheckStatus = result.Status;
    }

    public void MarkPublishedCheckFailed(string message)
    {
        PublishedCheckSucceeded = false;
        HasNoPublishedVersion = false;
        OnlineVersion = null;
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

    private static string ResolveSharedChannel(string pc, string android) =>
        string.Equals(pc, android, StringComparison.OrdinalIgnoreCase) && pc is "beta"
            ? "beta"
            : "stable";
}
