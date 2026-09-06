using CommunityToolkit.Mvvm.ComponentModel;
using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

public sealed class DeveloperReleaseViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ManagedToolPaths _paths;
    private string _status = "等待执行本地发布检查。";
    private string _currentPublishedVersion = "尚未检查线上发布版本。";
    private string _publishedVersionStatus = "打开发布中心后将自动检查。";
    private bool _isPublishedVersionChecking;
    private readonly PublishedVersionService _publishedVersionService = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    public DeveloperReleaseViewModel(AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
        _paths = _settings.ManagedTools["AxTools"];
        if (string.IsNullOrWhiteSpace(_paths.PublishReleaseNotes) &&
            !string.IsNullOrWhiteSpace(_settings.AxToolsReleaseNotes))
        {
            _paths.PublishReleaseNotes = _settings.AxToolsReleaseNotes;
        }
    }

    public string TargetVersion
    {
        get => _paths.PublishVersion;
        set
        {
            if (SetProperty(
                _paths.PublishVersion,
                value?.Trim() ?? string.Empty,
                _paths,
                static (model, newValue) => model.PublishVersion = newValue))
            {
                OnPropertyChanged(nameof(TargetVersionMajor));
                OnPropertyChanged(nameof(TargetVersionFeature));
                OnPropertyChanged(nameof(TargetVersionBugFix));
                if (string.IsNullOrWhiteSpace(_settings.AxToolsReleaseNotes))
                {
                    OnPropertyChanged(nameof(ReleaseDescription));
                }
            }
        }
    }

    public double TargetVersionMajor
    {
        get => ThreePartVersion.ParseOrDefault(TargetVersion).Major;
        set => TargetVersion = ThreePartVersion.ParseOrDefault(TargetVersion).WithMajor(value).ToString();
    }

    public double TargetVersionFeature
    {
        get => ThreePartVersion.ParseOrDefault(TargetVersion).Feature;
        set => TargetVersion = ThreePartVersion.ParseOrDefault(TargetVersion).WithFeature(value).ToString();
    }

    public double TargetVersionBugFix
    {
        get => ThreePartVersion.ParseOrDefault(TargetVersion).BugFix;
        set => TargetVersion = ThreePartVersion.ParseOrDefault(TargetVersion).WithBugFix(value).ToString();
    }
    public string Channel
    {
        get => _paths.PublishChannel;
        set => SetProperty(
            _paths.PublishChannel,
            value is "stable" or "beta" ? value : "stable",
            _paths,
            static (model, newValue) => model.PublishChannel = newValue);
    }
    public string ReleaseDescription
    {
        get => string.IsNullOrWhiteSpace(_paths.PublishReleaseNotes)
            ? CreateDefaultReleaseDescription()
            : _paths.PublishReleaseNotes;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(
                _paths.PublishReleaseNotes,
                normalized,
                _paths,
                static (model, newValue) => model.PublishReleaseNotes = newValue))
            {
                _settings.AxToolsReleaseNotes = normalized;
            }
        }
    }

    private string CreateDefaultReleaseDescription() =>
        $"## AxTools V{(string.IsNullOrWhiteSpace(TargetVersion) ? "待填写版本" : TargetVersion)}\n\n" +
        "统一管理工具的启动、构建、打包、发布、热更新与开发环境。";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string CurrentPublishedVersion { get => _currentPublishedVersion; private set => SetProperty(ref _currentPublishedVersion, value); }
    public string PublishedVersionStatus { get => _publishedVersionStatus; private set => SetProperty(ref _publishedVersionStatus, value); }
    public bool IsPublishedVersionChecking { get => _isPublishedVersionChecking; private set => SetProperty(ref _isPublishedVersionChecking, value); }
    public bool HasGiteeToken => FindGiteeTokenSource() is not null;
    public string GiteeTokenStatus => FindGiteeTokenSource() switch
    {
        "AXTOOLS_GITEE_TOKEN" => "AxTools Gitee Token 已配置。",
        "FANTASYTOOLS_GITEE_TOKEN" => "正在复用幻杀工具箱 Gitee Token。",
        "GITEE_TOKEN" or "GITEE_ACCESS_TOKEN" => "正在复用通用 Gitee Token。",
        _ => "尚未保存 Gitee Token。"
    };

    public void RefreshTokenStatus()
    {
        OnPropertyChanged(nameof(HasGiteeToken));
        OnPropertyChanged(nameof(GiteeTokenStatus));
    }

    private static string? FindGiteeTokenSource()
    {
        foreach (var name in new[]
        {
            "AXTOOLS_GITEE_TOKEN",
            "FANTASYTOOLS_GITEE_TOKEN",
            "GITEE_TOKEN",
            "GITEE_ACCESS_TOKEN"
        })
        {
            foreach (var target in new[]
            {
                EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.User,
                EnvironmentVariableTarget.Machine
            })
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, target)))
                {
                    return name;
                }
            }
        }
        return null;
    }

    public void RefreshReleaseSettings()
    {
        OnPropertyChanged(nameof(TargetVersion));
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(ReleaseDescription));
    }

    public async Task CheckPublishedVersionAsync(CancellationToken cancellationToken)
    {
        if (IsPublishedVersionChecking)
        {
            return;
        }

        IsPublishedVersionChecking = true;
        PublishedVersionStatus = "正在读取 AxTools 线上发布页...";
        try
        {
            var result = await _publishedVersionService.CheckLauncherAsync(
                ManagedToolKey.AxTools,
                Channel,
                cancellationToken);
            CurrentPublishedVersion = result.IsAvailable
                ? $"线上当前版本：{result.Version}"
                : "线上当前版本：暂无";
            PublishedVersionStatus = $"{result.SourceName} · {result.Status}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            CurrentPublishedVersion = "线上当前版本：检查失败";
            PublishedVersionStatus = exception is TaskCanceledException
                ? "检查超时，请稍后重试。"
                : $"检查失败：{exception.Message}";
        }
        finally
        {
            IsPublishedVersionChecking = false;
        }
    }
}
