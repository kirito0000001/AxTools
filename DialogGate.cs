using AxTools.Core.Models;
using AxTools.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace AxTools;

/// <summary>
/// 全进程弹窗闸门：同一时刻只允许一个 ContentDialog。
///
/// 为什么必须有：WinUI 同一个 XamlRoot/线程同时只允许一个 ContentDialog，开第二个会抛
/// COMException(0x80000019 = E_ASYNC_OPERATION_NOT_STARTED，"Only a single ContentDialog
/// can be open at any time")；这个异常一旦没人接，进程会被直接带走。
/// 2026-10-11 实测就是这么崩的：「推送新版本已完成」刚弹出来，紧接着点「切换到新版本」，
/// 两道 ShowAsync 撞在一起 —— 事件日志 AxTools.exe 崩在 Microsoft.UI.Xaml.dll / 0xc000027b
/// （stowed exception），WER 里是 combase.dll / 0x80000019。
///
/// 所以所有弹窗都从 <see cref="TryShowAsync"/> 出去：抢不到闸门就放弃这一次弹窗
/// （调用方按"用户取消"处理），绝不去开第二个。
/// </summary>
public static class DialogGate
{
    /// <summary>等前一个弹窗关闭的上限；等不到说明前台确实有弹窗，这次就不弹了。</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(2);

    /// <summary>关闭到 XAML 内部状态复位之间留的余量：紧接着开第二个仍会踩到 0x80000019。</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 抢闸门 → ShowAsync → 释放。抢不到或抛异常都返回 <c>null</c>，并把原因写进
    /// <paramref name="log"/>（允许为 null）。调用方一律把 null 当"用户取消"处理。
    /// </summary>
    public static async Task<ContentDialogResult?> TryShowAsync(
        ContentDialog dialog,
        LogService? log = null)
    {
        if (!await Gate.WaitAsync(WaitLimit).ConfigureAwait(true))
        {
            log?.Write(
                LogKind.Warning,
                "已有弹窗正在前台显示，已忽略这次弹窗请求（连续点击，或上一个弹窗还没关）。");
            return null;
        }

        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            log?.Write(LogKind.Error, "显示弹窗失败。", exception);
            return null;
        }
        finally
        {
            await Task.Delay(SettleDelay).ConfigureAwait(true);
            Gate.Release();
        }
    }
}
