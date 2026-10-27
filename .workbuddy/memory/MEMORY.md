# AxTools 项目长期约定

## 协作方式（2026-10-27 起生效）

- **过程用中文说明思路**，让用户可以随时跟进；不要闷头改完再汇报。
- **只改代码，验证最多做到静态检查**（XML/JSON 解析、资源键交叉核对、grep 断言）。
  **不跑 `dotnet build` / `dotnet test`**，编译与运行由用户负责，报错由用户贴回。
- **每完成一步提交一次**：只做本地提交，不 push；提交信息用中文，正文附简略描述（改了什么、为什么、验证到哪一步）。
- 用户明确说过「不用考虑上下文剩余」，可以连续推进多个步骤，不必为省 token 而缩水。

## 项目结构速查

- `AxTools`（WinUI 3 / WindowsAppSDK 2.3.1 / net8.0-windows / 自包含无打包）：应用壳与全部 UI。
- `AxTools.Core`（net8.0，仅依赖 CommunityToolkit.Mvvm，零 UI 引用）：Models / Services / Adapters / Catalog / ViewModels。
- `AxTools.Tests`：xUnit，含 `ToolPageXamlLayoutTests`（按字符串断言 XAML 结构的布局契约测试）。
- `Scripts/`：65 个 PowerShell 脚本，是真正的执行内核；C# 侧通过 JSON 事件回传（`AxTaskEventParser`）。

## 当前重构主题

1. **排版**：`Views/ServerPage.xaml.cs` 1880 行里大半是代码建 UI（`CreatePanel`/`CreateInstanceRow`/`CreateKeyValueLine`/`CreateActionButton`），XAML 只剩 110 行骨架；`ToolboxStyles.xaml` 样式令牌稀少，间距字号散落硬编码；设置页 10 个 Expander 因全局样式 `IsExpanded=True` 全部展开。
2. **内核抽出**：ServerPage 六段流程（ImportBuild / PushNewVersion / Apply / Rollback / Recover / CleanupLocalRepo）、`MainWindow.ToolActions.cs` 的动作管道、`ToolPageViewModel` 里的版本校验与发布检查，都适合往 Core 的 Service 搬。

## 已知问题

- `Views/ToolPage.xaml` 的「扫描空间占用」按钮没有 Click，存储清理页签是空壳（真功能在设置页）。
- 排版改动会先撞上 `AxTools.Tests/ToolPageXamlLayoutTests.cs`（509 行字符串断言），需同步更新。
