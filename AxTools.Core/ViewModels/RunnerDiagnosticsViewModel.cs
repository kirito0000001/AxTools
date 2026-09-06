using AxTools.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class RunnerDiagnosticsViewModel : ObservableObject
{
    private bool _isRunning;
    private bool _canStop;
    private string _statusTitle;
    private string _statusDetail;
    private string _lastResultText = "尚未运行自检。";

    public RunnerDiagnosticsViewModel(
        string? powerShellPath,
        string? environmentError = null)
    {
        PowerShellPath = powerShellPath ?? string.Empty;
        IsPowerShellAvailable = !string.IsNullOrWhiteSpace(powerShellPath);
        _statusTitle = IsPowerShellAvailable
            ? "PowerShell 7 已就绪"
            : "任务运行环境不可用";
        _statusDetail = IsPowerShellAvailable
            ? "可以运行标准脚本协议自检。"
            : environmentError ?? "未找到 PowerShell 7。";
    }

    public string PowerShellPath { get; }

    public bool IsPowerShellAvailable { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public bool CanStart => IsPowerShellAvailable && !IsRunning;

    public bool CanStop
    {
        get => _canStop;
        private set => SetProperty(ref _canStop, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string LastResultText
    {
        get => _lastResultText;
        private set => SetProperty(ref _lastResultText, value);
    }

    public void Begin(string scenario, string displayName)
    {
        IsRunning = true;
        CanStop = true;
        StatusTitle = $"正在运行：{displayName}";
        StatusDetail = $"场景 {scenario} 已进入全局重任务队列。";
    }

    public void ReportEvent(AxTaskEvent taskEvent)
    {
        if (!string.IsNullOrWhiteSpace(taskEvent.Message))
        {
            StatusTitle = taskEvent.Message;
        }

        if (!string.IsNullOrWhiteSpace(taskEvent.Detail))
        {
            StatusDetail = taskEvent.Detail;
        }

        if (taskEvent.CancellationMode is { } cancellationMode)
        {
            CanStop = cancellationMode != AxTaskCancellationMode.Locked;
            if (!string.IsNullOrWhiteSpace(taskEvent.Message))
            {
                StatusDetail = taskEvent.Message;
            }
        }
    }

    public void MarkStopRequested()
    {
        CanStop = false;
        StatusDetail = "停止信号已发送，正在等待脚本安全退出。";
    }

    public void ReportProtocolWarning(string message)
    {
        StatusTitle = "检测到无效协议事件";
        StatusDetail = message;
    }

    public void Complete(AxTaskResult result)
    {
        IsRunning = false;
        CanStop = false;
        StatusTitle = result.Status switch
        {
            AxTaskStatus.Succeeded => "任务运行器自检成功",
            AxTaskStatus.Stopped => "任务运行器自检已停止",
            _ => "任务运行器自检失败"
        };
        StatusDetail = result.Summary;
        LastResultText = result.Status switch
        {
            AxTaskStatus.Succeeded => $"成功 · {result.Duration.TotalSeconds:0.0}s",
            AxTaskStatus.Stopped => $"已停止 · {result.Duration.TotalSeconds:0.0}s",
            _ => $"失败 · 退出码 {result.ExitCode} · {result.Duration.TotalSeconds:0.0}s"
        };
    }
}
