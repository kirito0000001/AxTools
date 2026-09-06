using CommunityToolkit.Mvvm.ComponentModel;
using AxTools.Core.Models;

namespace AxTools.Core.ViewModels;

public sealed partial class GlobalProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _operationTitle = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _percentText = "0%";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private double _lastPercent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _cancelLabel = "取消当前操作";

    public void Start(string operationTitle, string detail)
    {
        OperationTitle = operationTitle;
        Title = "正在准备...";
        Detail = NormalizeDetail(detail);
        Percent = 0;
        LastPercent = 0;
        PercentText = "0%";
        ElapsedText = "00:00";
        IsIndeterminate = false;
        CanCancel = true;
        CancelLabel = "取消当前操作";
        IsVisible = true;
    }

    public void Report(ProgressUpdate update)
    {
        var clampedPercent = Math.Clamp(update.Percent, 0, 100);
        LastPercent = Percent;
        Percent = clampedPercent;
        PercentText = $"{clampedPercent:0}%";
        Title = update.Message;
        Detail = NormalizeDetail(update.Detail);
        IsIndeterminate = update.IsIndeterminate;
        IsVisible = true;
    }

    public void SetCancellationMode(
        AxTaskCancellationMode mode,
        string? message = null)
    {
        CanCancel = mode != AxTaskCancellationMode.Locked;
        CancelLabel = mode switch
        {
            AxTaskCancellationMode.Stop => "停止当前操作",
            AxTaskCancellationMode.Locked => "当前阶段不可停止",
            _ => "取消当前操作"
        };

        if (!string.IsNullOrWhiteSpace(message))
        {
            Detail = NormalizeDetail(message);
        }
    }

    public void MarkStopRequested()
    {
        CanCancel = false;
        CancelLabel = "正在停止";
        Detail = "停止信号已发送，正在等待脚本安全退出。";
    }

    public void Complete(string title, string detail)
    {
        LastPercent = Percent;
        Percent = 100;
        PercentText = "100%";
        Title = title;
        Detail = NormalizeDetail(detail);
        IsIndeterminate = false;
        CanCancel = false;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }

    private static string NormalizeDetail(string? detail) =>
        (detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
}
