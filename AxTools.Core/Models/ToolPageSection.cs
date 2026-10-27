namespace AxTools.Core.Models;

/// <summary>
/// 工具页当前选中的页签。顶部「配置状态」通知栏按这个决定检查什么：
/// 开发版查项目，打包与发布查游戏包目录，其余沿用适配器校验。
/// </summary>
public enum ToolPageSection
{
    Development,
    Release,
    Publish,
    Fragments,
    Storage,
    /// <summary>游戏页的「设置」页：正规设置项 + 临时的「功能散页」，不参与环境校验。</summary>
    Settings
}
