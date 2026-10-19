using AxTools.Core.Catalog;
using AxTools.Core.Models;

namespace AxTools.Core.ViewModels;

/// <summary>
/// TFAC 平台「碎片制作」里的游戏选择项。PC 端多个游戏共用一个页面，
/// 选中的档案决定产品段、标记名、包名前缀，路径决定从哪份游戏包发布取源。
/// </summary>
public sealed record FragmentGameOption(GameProfile Profile, ManagedToolPaths Paths)
{
    public string DisplayName => Profile.DisplayName;
    public string ShortName => Profile.ShortName;
}
