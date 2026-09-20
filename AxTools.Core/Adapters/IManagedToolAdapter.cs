using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public interface IManagedToolAdapter
{
    ManagedToolKey Key { get; }

    IReadOnlyList<ManagedToolActionDefinition> Actions { get; }

    /// <summary>
    /// 游戏包发布工具的产品段（下载站目录名，例如 crossingvoid）；不是游戏发布工具时为 null。
    /// 用于查"线上最新版本"：PC 段是 <c>games/&lt;产品段&gt;</c>，安卓段自动加 `-android`。
    /// </summary>
    string? GameProductSegment { get; }

    ManagedToolValidationResult Validate(ManagedToolPaths paths);

    AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths);
}
