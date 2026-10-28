# AxTools 项目长期约定

## 协作方式（2026-10-27 起生效）

- **过程用中文说明思路**，让用户可以随时跟进；不要闷头改完再汇报。
- **只改代码，验证最多做到静态检查**（XML/JSON 解析、资源键交叉核对、grep 断言）。
  **不跑 `dotnet build` / `dotnet test`**，编译与运行由用户负责，报错由用户贴回。
- **每完成一步提交一次**：只做本地提交，不 push；提交信息用中文，正文附简略描述（改了什么、为什么、验证到哪一步）。
- **阶段完成后发邮件通知**：调用 `C:\Users\liuyu\Documents\CodexTools\notify-step.ps1 -Title <完成事项> -Summary <改了什么/验证了什么/阻塞> -Project AxTools`。
  凭据优先 `CODEX_NOTIFY_*`，回退 `TFAC_NOTIFY_*`（本机已配 TFAC_NOTIFY_TO / SMTP_USER / SMTP_PASSWORD）。
  脚本被 ExecutionPolicy 拦，需先 `Set-ExecutionPolicy -Scope Process Bypass -Force`。
- 用户明确说过「不用考虑上下文剩余」，可以连续推进多个步骤，不必为省 token 而缩水。

## 项目结构速查

- `AxTools`（WinUI 3 / WindowsAppSDK 2.3.1 / net8.0-windows / 自包含无打包）：应用壳与全部 UI。
- `AxTools.Core`（net8.0，仅依赖 CommunityToolkit.Mvvm，零 UI 引用）：Models / Services / Adapters / Catalog / ViewModels。
- `AxTools.Tests`：xUnit，含 `ToolPageXamlLayoutTests`（按字符串断言 XAML 结构的布局契约测试）。
- `Scripts/`：65 个 PowerShell 脚本，是真正的执行内核；C# 侧通过 JSON 事件回传（`AxTaskEventParser`）。

## 当前重构主题

1. **左栏排版（当前）**：左侧导航固定 8 项——AX管理器 / 工具-零境素材 / 工具-剧情填表 / 工具-幻杀卡牌 / TFAC启动平台 / 游戏-零境交错 / 游戏-幻杀 / 游戏-火影BP；整体设置在页脚。导航常驻展开（`PaneDisplayMode=Left`）。
   - 「服务器」页后续整体移除，**功能要保留**（迁到哪未定）；两个安卓页当前无入口，页面实例仍在。
   - 三个游戏页已回到 PageHost 作独立页，首次进入自动切「打包与发布」。
   - **不要再改 ServerPage 的排版**，那页是待拆的。
2. **内核抽出（后续）**：ServerPage 的六段流程、`MainWindow.ToolActions.cs` 的动作管道、`ToolPageViewModel` 里的版本校验，都适合往 Core 的 Service 搬。等左栏定稿后再谈。

## 已知问题

- `Views/ToolPage.xaml` 的「扫描空间占用」按钮没有 Click，存储清理页签是空壳（真功能在设置页）。
- 排版改动会先撞上 `AxTools.Tests/ToolPageXamlLayoutTests.cs`（509 行字符串断言），需同步更新。
