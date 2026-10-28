namespace AxTools.Core.Models;

/// <summary>确认弹窗里的一行键值。</summary>
public sealed record ToolActionConfirmationLine(string Label, string Value);

/// <summary>
/// 动作确认内容：标题、主按钮文案、若干键值行、底部说明。
/// 这些原来散在 MainWindow 的弹窗代码里，现在由 Core 生成，界面只负责渲染。
/// </summary>
public sealed record ToolActionConfirmation(
    string Title,
    string PrimaryButtonText,
    IReadOnlyList<ToolActionConfirmationLine> Lines,
    string Note,
    bool AllowChannelChoice = false,
    bool DefaultDownloadSite = true,
    bool DefaultGitHub = true);

/// <summary>动作预检结果：能不能执行、被挡的原因、需要确认时给什么内容。</summary>
public sealed record ToolActionPreflightResult(
    bool Allowed,
    string BlockedTitle = "",
    string BlockedMessage = "",
    ToolActionConfirmation? Confirmation = null,
    ManagedToolActionRequest? Request = null)
{
    public static ToolActionPreflightResult Block(string title, string message) =>
        new(false, title, message);

    public static ToolActionPreflightResult Ready(
        ToolActionConfirmation confirmation,
        ManagedToolActionRequest request) =>
        new(true, Confirmation: confirmation, Request: request);
}
