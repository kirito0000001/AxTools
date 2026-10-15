# AxTools 发布链设计（与 ZD 工具箱同构）

日期：2026-10-15
范围：`Scripts\Release\` 这一整套出包 / 发布脚本，以及驱动它的 GUI 动作。
一句话：**把 ZD 工具箱（CrossingVoidZDTool）那套"出包 → 校验 → 双通道发布"的底层搬到 AxTools 自己的仓库里**，
两份仓库各自独立，谁也不引用谁的脚本。

---

## 1. 为什么换

旧流程只有三个动作：`Package-AxTools.ps1` 打 zip → `Test-AxToolsPackage.ps1` 校验 → `Publish-AxToolsRelease.ps1`
删光同版本 Release 的全部附件再重传。它能跑，但有四个绕不过去的问题：

| 问题 | 旧流程 | 现流程 |
| --- | --- | --- |
| 断点续传 | 每发一次都把旧附件全删光，网络一抖就得从 0 重来（89 MB 重传） | 按内容核对，已传好的附件跳过；清单永远是最后一步 |
| 体积墙 | 单附件硬撞 Gitee 100 MiB，超了就发不出去 | 超限自动分卷（每卷 ≤90 MB，卷是字节级切片，拼回来逐字节相同） |
| 更新体积 | 每次更新都要下 89 MB | 有增量包：逐文件比对，只传变化的文件（实测 3 MB 级） |
| 老包复用 | 想只重发清单也得重新出包 | 台账（`artifacts.json`）校验通过就复用，只重生成清单 |

另外旧脚本没有出包台账与文件索引，导致"这一版到底发了什么"只能靠 zip 反推；现在每一版都有落盘的台账 + 文件索引。

## 2. 产物与目录

产物根：GUI 传的 `WorkspaceArtifactPathPolicy` 输出根下的**版本目录**（命令行默认 `<repo>\Artifacts\ReleaseAssets`）：

```
<产物根>\v<版本>\
  AxTools-v<版本>-win-x64.zip                     整包（Bandizip -l:9 最高压缩）
  AxTools-v<版本>-win-x64.sha256.txt              第 1 行整包，其后每卷 1 行
  AxTools-v<版本>-win-x64.part01.zip …            仅在整包 >100 MiB 时出现
  AxTools-v<版本>-win-x64-delta-from-v<基准>.zip  增量包（仅在差距够小时出现）
  toolbox-update.json                             更新清单（客户端唯一入口）
  file-index.json                                 本版文件索引（下一版算增量的依据）
  artifacts.json                                  出包台账（复用与排查的依据）
  .work\<版本>\win-x64\                           程序目录（dotnet publish 输出 + 包内体检单）
```

包内还有 `update-package.json`（体检单）：`toolboxStableKey / version / runtime / entryExe / windowsAppSdkRuntime / createdAt`。
`windowsAppSdkRuntime` 现在是空串（AxTools 是 `--self-contained` + `WindowsAppSDKSelfContained=true`），
留着这个字段是为了客户端将来能判断"升级前要不要先确认运行时在不在"。

## 3. 脚本清单（全部在 `Scripts\Release\`）

| 脚本 | 职责 |
| --- | --- |
| `Build-AxToolsUpdatePackage.ps1` | 出包：写版本号 → dotnet test/publish → 补 WinUI 资源与更新器 → 压包 → 分卷 → sha256 → 清单 → 索引 + 增量 → 台账 |
| `Test-AxToolsPackage.ps1` | 只读校验：清单 ↔ zip ↔ sha256.txt ↔ 分卷 ↔ 增量包 ↔ 文件索引 ↔ 台账，逐项对账 |
| `Publish-AxToolsRelease.ps1` | 完整发布编排：令牌预检 → Gitee（0-50%）→ GitHub（50-100%） |
| `Publish-AxToolsGiteeRelease.ps1` / `Publish-AxToolsGithubRelease.ps1` | 单通道入口 |
| `AxToolsPublish.ps1` | 两条通道的共享实现（Gitee API 封装 / 上传 / 复查 / 清理 / 自检 / GitHub 全套） |
| `AxToolsReleaseCommon.psm1` | 令牌解析：Gitee（`AXTOOLS_GITEE_TOKEN` → 复用 `FANTASYTOOLS_GITEE_TOKEN` → `GITEE_TOKEN`）与 GitHub（环境变量 → `gh auth token`） |
| `Set-AxToolsVersion.ps1` | 把版本号写进 `AxTools.csproj` 四个属性（只许前进，`-AllowDowngrade` 才回退） |
| `Update-AxTools.ps1` | **客户端**外部更新器（随包发出去；本轮未改，仍是整包覆盖） |

`Package-AxTools.ps1` **已删除**：出包只有一处真相源，且发布链只认 `Build-AxToolsUpdatePackage.ps1`。

## 4. 发布流水线的顺序（顺序本身就是契约）

```
出包（幂等：台账有效就复用）
  └ 校验：清单 ↔ 本地产物逐项对账（对不上就地中止，绝不带着可疑产物上传）
      └ Gitee：Release 幂等 PATCH/POST
          ├ 传包体（整包或全部分卷）→ 已有同内容则跳过
          ├ 传 sha256.txt（每次重传，它是"包体是否齐"的凭据）
          ├ 复查附件名 + 大小（缺一即中止）
          ├ ★ 传 toolbox-update.json（此后 locked 不可中断）—— 客户端读到的最后一步
          ├ 匿名自检：只读一次线上清单，核对 version / channel / 整包 sha256（不打 API、不走配额）
          └ 清理旧版本：只保留最近 3 个**带附件**的 Release（附件 + Release 记录 + 远端 tag）
      └ GitHub：同一批附件、同一份清单；**从不删除任何旧版本**
```

* Gitee 的 1 GB 附件预算与 3 个版本的保留策略是**有意**的；GitHub 没有这条约束，所以两边行为不同不是没对齐。
* Gitee 分支 `master`（仓库是 `auto_init` 建的），GitHub 分支 `main`。
* 令牌只从环境变量/`gh` 读，脚本里不留、日志里会做掩码（`<token>`）。

## 5. 增量包

* 依据：`file-index.json`（相对路径 → 长度 + SHA-256）与上一版程序目录/索引逐文件比对。
* 只把"新增/变化"的文件打进增量包，另外带一份显式 `delete[]` 清单（整包里被删掉的文件）。
* 每个变化项都带 `baseSha256`（旧内容指纹）。覆盖器按它逐个核对：**对不上就整体拒绝**，绝不混装。
* 比例门：估算增量 > 整包的 70% 就干脆只发整包（`MaxRatioPercent = 70`）。
* 基准版默认取产物根下"比本版低、且留有索引或程序目录"的最高版本（`vX.Y.Z` 目录，预发布号不参与）；也可 `-DeltaBaseVersion` 指定。
* 台账（`artifacts.json`）会记下打包时用的开关（`keepDebugArtifacts` / `excludeAiRuntime`）。
  复用旧包重发时，索引与增量**按台账里的开关**重算，而不是按本次命令行 ——
  否则索引会与包的内容脱节，下一版的增量就会把包里明明有的文件列进删除表（实测：删除项从 1 变 2）。
* 实测（假产物 + 真产物各验一遍）：整包 3.0 MB / 增量 657 B（改 1 个文件 + 删 1 个文件）；
  真包 581 个文件时索引 581 条，改一个 DLL 就只传那个 DLL。

## 6. 100 MiB 体积墙与"AI 推理运行时"

* Gitee 单附件上限 100 MiB；出包脚本以 100 MB / 90 MB 为默认阈值，超限自动按字节切卷，卷号顺序即拼接顺序。
* `Microsoft.WindowsAppSDK` 会**传递**带进 AI 推理运行时：`onnxruntime.dll`（20.66 MB）+ `DirectML.dll`（17.83 MB）
  + `Microsoft.ML.OnnxRuntime.dll`（0.23 MB），来自 `Microsoft.Windows.AI.MachineLearning` 包。
  实测它们让 zip 多约 16 MB（89.4 MB → 68.44 MB）。
* AxTools 全工程 grep 不到任何 `OnnxRuntime` / `DirectML` / `Windows.AI` 用法，所以默认剔除（GUI 与出包脚本的默认路径都会加 `-ExcludeAiRuntime`）。
* 将来真做本地推理时必须关掉：把环境变量 `AXTOOLS_EXCLUDE_AI_RUNTIME` 设成 `0`（或出包时不加 `-ExcludeAiRuntime`）。

## 7. 版本号与通道

* 通道由**版本号**决定：带后缀（如 `1.1.0-Beta`）= 测试版，纯三段 = 正式版；清单里的 `channel`、Release 的 `prerelease` 都由它推。
* GUI 上的通道选择框现在只是**校对**：选 `beta` 却填 `1.1.0` 会直接报错并提示怎么写，不会再发出一个"通道标测试版、版本像正式版"的包。
* 出包默认会把版本号写进 `AxTools.csproj`（四个属性），不想要就加 `-SkipVersionWrite`。

## 8. 怎么用

GUI（发布中心）：`打包` → `校验产物` → `单独上传` → `完整发布`；工具页（Ax管理器）另有 `打包正式版 / 打包测试版 / 校验产物 / 上传演练 / 单独上传 / 发布演练 / 完整发布`。
建议顺序仍是先 `发布演练`（只跑本地，不碰网络）再 `完整发布`。

命令行：

```powershell
# 出包（默认会写版本号、跑单测、dotnet publish；有台账就复用）
.\Scripts\Release\Build-AxToolsUpdatePackage.ps1 -Version 1.1.0 -ExcludeAiRuntime
# 出包演练：一个字节都不写
.\Scripts\Release\Build-AxToolsUpdatePackage.ps1 -Version 1.1.0 -DryRun
# 本地校验已有产物
.\Scripts\Release\Test-AxToolsPackage.ps1 -ReleaseAssetRoot .\Artifacts\ReleaseAssets\v1.1.0 -ExpectedVersion 1.1.0
# 发布演练 / 完整发布 / 只发一条通道
.\Scripts\Release\Publish-AxToolsRelease.ps1 -Version 1.1.0 -ExcludeAiRuntime -DryRun
.\Scripts\Release\Publish-AxToolsRelease.ps1 -Version 1.1.0 -ExcludeAiRuntime
.\Scripts\Release\Publish-AxToolsRelease.ps1 -Version 1.1.0 -ExcludeAiRuntime -SkipGithub
# 只上传已有产物（不出包）
.\Scripts\Release\Publish-AxToolsRelease.ps1 -Version 1.1.0 -SkipBuild
```

★ **命令行记得加 `-ExcludeAiRuntime`**：GUI 的动作会自动加（可用 `AXTOOLS_EXCLUDE_AI_RUNTIME=0` 关掉），
脚本层是显式开关 —— 与 ZD 那边同一套约定，不加就是 89 MB 级的整包。

`AXTOOLS_EXCLUDE_AI_RUNTIME=0` / `AXTOOLS_GITEE_TOKEN` / `AXTOOLS_GITHUB_TOKEN` 是这套链上仅有的三个环境开关。

## 9. 测试与验证

* `Scripts\Tests\Test-AxToolsReleaseScripts.ps1`（已加入 `Test-AllManagedToolAdapters.ps1`）：
  脚本面与参数契约 → 令牌解析 → 出包（含 `.pdb`/AI 运行时剔除断言）→ 校验 → 发布演练 →
  增量（基准 / 变化数 / 删除数 / `baseSha256` / 体积占比）→ 分卷（卷数 / sha256 行数 / 拼接逐字节相同）→
  反面用例（坏 sha256.txt、缺入口 exe 必须被拦）→ 复用路径（zip 不再重压）。
* `Scripts\Tests\Test-AxToolsAdapter.ps1`：环境检查、打包演练不落目录、**通道与版本号冲突必须被拒**。
* C# 侧：`dotnet test` 373 项全绿（含修好的两处过期测试，见下）。
* 实测真包：581 个文件 / 未压缩 177.8 MB / zip 68.44 MB / 出包 65 s。

## 10. 与旧流程的差异清单（升级须知）

1. `Package-AxTools.ps1` 退役，改用 `Build-AxToolsUpdatePackage.ps1`（GUI 与适配器已同步）。
2. 产物落点从"平的 `Artifacts\ReleaseAssets`"变成"`Artifacts\ReleaseAssets\v<版本>`"。
3. 同版本重发**不再清空旧附件**：包体按内容跳过，只有 sha256.txt 与清单每次重传。
4. Gitee 侧开始清理旧版本附件（保留最近 3 个带附件的 Release，含 Release 记录与远端 tag）。
5. 清单新增 `releaseNotesUrl` 与 `delta` 段；`minSupportedVersion` 不再产出（客户端本来就没读它）。
6. 出包会改 `AxTools.csproj` 的版本号（有意为之，`-SkipVersionWrite` 可关）。

## 11. 还没做（下一步）

* **客户端还没吃增量**：`Scripts\Release\Update-AxTools.ps1` 仍是"整目录换掉 + 备份回滚"，
  C# 侧 `UpdateService` / `UpdatePackageValidator` 也只认 `assets[0]` 的整包（不认 `parts` 与 `delta`）。
  在客户端跟上之前，增量包只是"传上去了、没人用"；老客户端遇到分卷包会失败（整包 ≤100 MiB 时不会触发）。
* `PublishedVersionService` 查 AxTools 线上版本仍在读 GitHub Releases API；
  ZD 那边的做法是"先匿名读 `releases/download/latest/toolbox-update.json`，不合通道再回落 API"，AxTools 可以照抄（省配额、少失败）。
* `Scripts\Tests\` 里有 7 处**既有**失败（`Test-SmartTauriDevelopmentLaunch.ps1` 缺 `node_modules\.bin\tauri.cmd`、
  `Test-DevelopmentLaunchPolicy.ps1` 的 Tauri `--debug` 断言、`Test-FantasyProjectPcAdapter.ps1` 的 `package.json` 特征、
  `Test-CrossingVoidPcAdapter.ps1` 的服务器快照容错文本、`Test-CrossingVoidGamePackage.ps1` 的 `CheckGamePackage` 文本、
  `Test-GalExcleToolsAdapter.ps1` 的依赖缓存进度、`Test-SeparatedEnvironmentCheck.ps1` 末尾缺 `exit 0` 导致退出码是上一次命令的残留）。
  这些都已经在**干净的 HEAD 工作树**上原样复现过，不属本次改动；本次新增/修改的 `Test-AxToolsReleaseScripts.ps1` 与
  `Test-AxToolsAdapter.ps1` 全部通过。
