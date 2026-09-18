# 服务端 Git 增量更新设计

目标：把「选文件夹整体上传」的服务端更新方式，换成基于 Git 的增量更新，并补上「回退到上一版」能力。

范围：零境交错 / 火影BP / 幻杀 三个 UE 服务端的程序更新与回退。

不包含：客户端资源分发（仍归各游戏工具）；崩溃符号解析（另行设计）。

## 一、共识结论

| # | 事项 | 结论 |
| --- | --- | --- |
| 1 | 更新机制 | Git 增量：本机仓库 → push → 服务器裸仓库 → checkout 到运行目录 |
| 2 | 本机仓库 | `D:\DabaoV\服务器Git合集\<程序>` |
| 3 | 服务器仓库 | `C:\Users\Administrator\Desktop\ServerRepos\<key>.git`（裸仓库），运行目录里不放 `.git` |
| 4 | 更新单位 | 零境交错 / 火影BP / 幻杀 各自独立 |
| 5 | 版本策略 | 最近两版之间互相切换：`回退上个版本` / `切换最新版本`；两个按钮互斥（能退就不能切，反之亦然） |
| 6 | 闸门 | 自动暂挂看门狗拉起 → 停服 → 切换 → 启动 → 健康检查 180 秒 → 自动恢复 |
| 7 | pdb | 不纳入版本管理、不传服务器；本机按版本归档，保留最近两版 |
| 8 | `Saved\` | 绝对不动（玩家数据，尚未迁 SQL），排除在版本管理外 |
| 9 | 推送 / 执行 | 分两步：推送可在服务运行时做，应用由用户在 AxTools 点击 |
| 10 | 页签 | 三个程序卡片 + `导入新构建` / `推送新版本` / `切换最新版本` / `回退上个版本` / `查看待更新内容` |
| 11 | 零境两实例 | 共用同一份目录，更新/回退必然一起动，界面标注 |
| 12 | 首次基线 | 以服务器线上产物为 v1，不需要全量上传 |
| 13 | 首次停机 | 不需要：首次初始化只读取运行目录，不写入任何被跟踪文件 |
| 14 | 仓库根映射 | 见下表 |
| 15 | 日常操作 | Rider 打开文件夹 → Commit → Push；AxTools 按钮为等价入口 |
| 16 | 二进制保护 | 仓库内 `.gitattributes` 把二进制标为 `-text -diff` |
| 17 | 服务器磁盘 | 净减少约 1 GB（删掉零境现存 pdb 后 C 盘可用约 12.4 GB） |
| 18 | 新构建入库 | `导入新构建` 做镜像同步 → 预填说明的提交框 → 可直接在 AxTools 推送 |

## 二、程序档案

| Key | 显示名 | 服务器仓库 | 运行目录（工作树） | 看门狗实例 |
| --- | --- | --- | --- | --- |
| `crossingvoid` | 零境交错 | `...\ServerRepos\crossingvoid.git` | `C:\Users\Administrator\Desktop\WindowsServer` | `CrossingVoid-Login`、`CrossingVoid-Main` |
| `narutobp` | 火影BP | `...\ServerRepos\narutobp.git` | `C:\Users\Administrator\Desktop\BP_Server\WindowsServer` | `NarutoBP` |
| `fantasyproject` | 幻杀 | `...\ServerRepos\fantasyproject.git` | `C:\Users\Administrator\Desktop\幻杀_Server\WindowsServer` | `FantasyProject` |

仓库目录名一律用 ASCII，避免中文路径经 Windows OpenSSH 传到远端 cmd 时被代码页破坏；本机工作树目录名用中文显示名，因为它是纯本地路径。

## 三、为什么是 Git

实测（服务器上逐文件流式压缩）：

| 文件 | 原始 | gzip 后 | 比值 |
| --- | --- | --- | --- |
| 零境 `pakchunk0` | 178.2 MB | 157.8 MB | 0.885 |
| 火影 `pakchunk0` | 117.0 MB | 113.8 MB | 0.973 |
| 幻杀 `pakchunk0` | 109.9 MB | 108.1 MB | 0.983 |
| 零境 `CrossingVoidServer.exe` | 288.4 MB | 103.8 MB | 0.360 |

pak 已经接近不可再压缩，所以增量不能靠 gzip。Git 用的是 delta：实测取现成的 178.2 MB pak，改掉 4 处共 1 MB 后提交第二版，`git repack -adf` 后整个 `.git` 只有 **158.5 MB**（比值 0.890）——两版只花了一版多的空间。

已知最坏情况：某次重打包把整个 pak 重排，delta 失效，该版本按全量传输（零境最大单文件 178 MB）。第一次真实更新后要实测一次增量体积。

## 四、服务器端仓库形态

裸仓库 + 外部工作树，已验证可行：

```powershell
git --git-dir=<repo>.git --work-tree=<运行目录> add -A
git --git-dir=<repo>.git --work-tree=<运行目录> commit -m <说明>
git --git-dir=<repo>.git --work-tree=<运行目录> checkout -f refs/heads/deployed
```

要点：

- 运行目录里**不会**出现 `.git`；UE 服务端不会扫到版本库。
- `Saved/` 由 `.gitignore` 排除，`checkout` 不会触碰它（实测通过）。
- `refs/heads/main` = 已推送的最新提交；`refs/heads/deployed` = 当前实际部署的提交。回退就是 checkout `refs/heads/deployed` 的上一提交。
- `checkout` 后 HEAD 处于 detached 状态，`advice.detachedHead=false` 抑制提示。

### 内存保护

服务器只有 1966 MB 内存、空闲约 722 MB。delta 重算只发生在本机推送时与服务器端 repack 时：

- 服务器仓库设 `gc.auto=0`、`receive.autogc=false`，不自动 repack。
- 必须 repack 时使用 `-c core.bigFileThreshold=64m`，让大于 64 MB 的对象整存、不参与 delta 搜索，内存峰值保持低位。
- 本机保留默认阈值，delta 在本机算完再推送。

## 五、版本策略

只保留最近两版：保留 `main` 与其父提交，更早的提交在应用新版本后丢弃并 repack 回收。

两个按钮是一对切换开关，各自指向一根固定指针：

| 按钮 | 目标 | 效果 |
| --- | --- | --- |
| `切换最新版本` | `refs/heads/main` | 切到最新推送的那一版（= 原来的"应用并重启"，两者本就是同一个动作） |
| `回退上个版本` | `refs/heads/previous` | 切回上一版 |

`refs/heads/previous` 只在**切换成功后**才被写成"刚才在跑的那一版"，所以它永远是一个真的跑起来过、可以退回去的版本；失败的切换不会污染它。

**回退不删除任何东西。** 最新版本仍然留在服务器仓库里，所以可以来回切换、可以在两版之间对比文件差异，也可以在修好后重新前进。代价只是那块磁盘没有释放——实测每版增量很小（178 MB 的 pak 改 1 MB 后，两版合计只占 158.5 MB），因此接受。

本机仓库 `D:\DabaoV\服务器Git合集\` 始终保留完整历史，即使服务器上某个版本被清掉也不会丢。

pdb 单独处理：`.gitignore` 排除 `*.pdb`，不推送、不上服务器。`导入新构建` 时把 pdb 归档到 `D:\DabaoV\服务器Git合集\_Symbols\<程序>\<短哈希>\`，每个程序保留最近两版。

## 六、闸门流程

`切换最新版本` 与 `回退上个版本` 走同一条流程：

```
1  预检      本地/远端版本、运行目录状态、受影响实例
2  暂挂      对受影响实例置维护模式（watchdog 不再自动拉起）
3  停服      停止实例，确认进程退出
4  切换      checkout 目标提交到运行目录
5  启动      按 watchdog.config.psd1 的参数拉起实例
6  健康检查  进程存活 + 端口监听，宽限 180 秒
7  收尾      解除维护模式
   失败      保持维护模式并在停服状态恢复原提交，然后重启
```

维护模式不再做成独立按钮；界面上只在流程中显示「切换中 · 看门狗已暂挂」。

零境的两个实例共用同一份目录，切换时必然一起重启，界面上必须明确提示。

## 七、轴侧实现落点

| 层 | 文件 |
| --- | --- |
| 模型 | `AxTools.Core/Models/ServerGitModels.cs` |
| 档案 | `AxTools.Core/Catalog/ServerProgramCatalog.cs` |
| 服务 | `AxTools.Core/Services/ServerGitUpdateService.cs` |
| 视图模型 | `AxTools.Core/ViewModels/ServerPageViewModel.cs`（扩展） |
| 视图 | `Views/ServerPage.xaml.cs`（新增 `服务端更新` 卡片） |
| 本机脚本 | `Scripts/Servers/Aliyun/Invoke-AliyunServerGit.ps1` |
| 远端脚本 | `Scripts/Servers/Aliyun/Remote-AliyunServerGit.ps1`（部署到 `C:\UEWatchdog\Remote-AliyunServerGit.ps1`） |
| 部署脚本 | `Scripts/Servers/Aliyun/Deploy-AliyunServerGit.ps1` |
| 引导脚本 | `Scripts/Servers/Aliyun/Initialize-AliyunServerRepos.ps1` |

所有远端命令走 `EncodedCommand`；远端路径一律正斜杠，避免 Windows OpenSSH 吞反斜杠。

## 八、安全边界

- AxTools 只保存 SSH 别名与路径，不保存私钥或口令。
- 停服、切换、回退一律先扫描并显示影响范围，再二次确认。
- 更新前必须置维护模式，流程结束（成功或失败）都要解除；失败时保持维护模式并提示。
- `Saved\` 永远不进入版本管理，任何切换都不得改动它。
- 首次初始化只读取运行目录，不写入被跟踪文件；不产生停机窗口。
