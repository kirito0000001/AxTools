using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class UpdateViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private string _status;
    private UpdateRelease? _availableRelease;
    private string _downloadedPackagePath = string.Empty;

    public UpdateViewModel(AppSettings settings)
    {
        _settings = settings;
        _status = string.IsNullOrWhiteSpace(settings.UpdateLastStatus) ? "尚未检查更新。" : settings.UpdateLastStatus;
    }

    public Array Sources => Enum.GetValues<UpdateSource>();
    public Array Channels => Enum.GetValues<UpdateChannel>();
    public UpdateSource Source { get => _settings.UpdateSource; set => SetProperty(_settings.UpdateSource, value, _settings, static (m, v) => m.UpdateSource = v); }
    public UpdateChannel Channel { get => _settings.UpdateChannel; set => SetProperty(_settings.UpdateChannel, value, _settings, static (m, v) => m.UpdateChannel = v); }
    public bool AutoCheckEnabled { get => _settings.UpdateAutoCheckEnabled; set => SetProperty(_settings.UpdateAutoCheckEnabled, value, _settings, static (m, v) => m.UpdateAutoCheckEnabled = v); }
    public bool CheckOnStartup { get => _settings.UpdateCheckOnStartup; set => SetProperty(_settings.UpdateCheckOnStartup, value, _settings, static (m, v) => m.UpdateCheckOnStartup = v); }
    public int ConnectionTimeoutSeconds { get => _settings.UpdateConnectionTimeoutSeconds; set => SetProperty(_settings.UpdateConnectionTimeoutSeconds, Math.Clamp(value, 10, 600), _settings, static (m, v) => m.UpdateConnectionTimeoutSeconds = v); }
    public string LastCheckText => _settings.UpdateLastCheckAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未检查";
    public string LastCheckDisplay => $"最近检查：{LastCheckText}";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public UpdateRelease? AvailableRelease { get => _availableRelease; private set { if (SetProperty(ref _availableRelease, value)) OnPropertyChanged(nameof(CanDownload)); } }
    public string DownloadedPackagePath { get => _downloadedPackagePath; private set { if (SetProperty(ref _downloadedPackagePath, value)) OnPropertyChanged(nameof(CanInstall)); } }
    public bool CanDownload => AvailableRelease is not null;
    public bool CanInstall => AvailableRelease is not null && File.Exists(DownloadedPackagePath);

    public void ApplyCheckResult(UpdateCheckResult result)
    {
        AvailableRelease = result.Release;
        Status = result.Status;
        _settings.UpdateLastCheckAt = DateTimeOffset.Now;
        _settings.UpdateLastStatus = result.Status;
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(LastCheckDisplay));
    }

    public void SetFailure(string message) { Status = message; _settings.UpdateLastStatus = message; }
    public void SetDownloaded(string path) { DownloadedPackagePath = Path.GetFullPath(path); Status = $"更新包已下载并通过校验：{DownloadedPackagePath}"; }
}
