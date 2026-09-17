# AxTools 服务器分区设计

目标：在 AxTools 左侧导航新增独立的「服务器」分区，管理服务器侧运维事务。

范围：**看门狗控制**与**游戏服务端更新**两项。

不包含：客户端游戏资源上传。该能力仍归各游戏自己的工具（如 `CrossingVoidGame` 适配器），服务器分区不重复实现。

架构：新增并列于工具适配器的「服务器档案（ServerProfile）+ 服务器适配器（IServerAdapter）」，复用现有任务执行器、`::axtools` 结构化事件协议、日志与任务历史。服务器动作落在 `Scripts/Servers/`，不改动现有 `Scripts/Adapters/<Tool>/`。

## 一、现状（2026-09-17 实测）

### 1. 主机

| 项目 | 实测值 |
| --- | --- |
| 地址 / 账号 | `47.109.153.36`，`Administrator`，SSH 别名 `crossing-server` |
| 主机名 | `iZjg5pd9l69hpuZ` |
| 系统 | Windows Server |
| IIS | Default Web Site → `C:\inetpub\wwwroot`，绑定 `www.crossingvoid.top`（80/443） |
| 关键端口 | 22 SSH、80/443 IIS、3306 MySQL、3389 RDP、8888 面板、51987 ToolboxUpdateServer、51988/51989 回环中继、60888 |
| 桌面目录 | `BP_Server`、`OSSAPI`、`QQ-Codex`、`SaveGames备份`、`UEWatchdog`、`WindowsServer`、`和谐家园`、`幻杀_Server` |

### 2. 被监控的四个服务端实例

| 名称 | 显示名 | 端口 | 可执行文件 |
| --- | --- | --- | --- |
| CrossingVoid-Login | 零境 - 登录大厅 | UDP 11451 | `Desktop\WindowsServer\CrossingVoid\Binaries\Win64\CrossingVoidServer.exe` |
| CrossingVoid-Main | 零境 - 主界面大厅 | UDP 11452 | 同上，参数不同 |
| NarutoBP | 火影 | UDP 1234 | `Desktop\BP_Server\WindowsServer\NarutoBPServer.exe` |
| FantasyProject | 幻杀 | UDP 1235 | `Desktop\幻杀_Server\WindowsServer\FantasyProjectServer.exe` |

四个实例对应**三个可执行程序**：零境的两个实例共用同一个 `CrossingVoidServer.exe`，只是地图与端口参数不同。因此「服务端更新」的更新单位是**程序**，不是实例。

### 3. 看门狗

配置 `C:\UEWatchdog\` 下的 `watchdog.config.psd1`：

```
CheckIntervalSeconds = 30      RestartCooldownSeconds = 60
StartGraceSeconds    = 180     CrashLookbackMinutes   = 30
LogTailLines         = 80
LogDirectory         = C:\UEWatchdog\logs
MaintenanceFile      = C:\UEWatchdog\maintenance.txt
Mail                 = smtp.qq.com:587，已启用，收件 1376609162@qq.com
```

计划任务实测存在且为 Ready：

| 任务 | 用途 |
| --- | --- |
| `UEWatchdog` | 服务监控与自动拉起 |
| `UEWatchdog-DailyReboot` | 每日 04:00 重启 |

### 4. 服务器上已有的管理入口

`Desktop\UEWatchdog\Manage-UEWatchdog.ps1` 提供非交互参数：

```powershell
-Action <Menu|Status|Start|Stop|Restart|Logs>
-ServerName <实例名>
-ConfigPath <默认 watchdog.config.psd1>
-MaintenancePath <默认 maintenance.txt>
-PassThru
```

内部可用函数：`Get-ManagedServerStatus`、`Start-ManagedServer`、`Stop-ManagedServer`、`Restart-ManagedServer`、`Get-LatestServerLog`、`Send-TestMail`、`Switch-WatchdogTask`、`Switch-DailyRebootTask`、`Set-ServerMaintenance`、`Test-ServerMaintenance`。

**维护模式是按实例记录的**，写入 `maintenance.txt`，由 `Set-ServerMaintenance -Name <实例> -Enabled $true/$false` 控制。目前维护模式与任务开关只挂在交互式菜单上，需要为 AxTools 补非交互入口。

`Watchdog.ps1` 在启动实例前会检查维护状态，处于维护模式的实例不会被自动拉起。

## 二、分区结构

左侧导航新增「服务器」分组，下设一个页面；页面内按页签分区，沿用现有 `ToolPage` 的页签样式。

```
服务器
└── 阿里云 · 零境服务器
    ├── 概览
    ├── 看门狗
    └── 服务端更新
```

野草云目前不进入本分区：它的能力是客户端资源分发，而资源上传已归属各游戏工具。若后续仍希望集中查看它的磁盘与证书状态，可另加一个只读页，不影响本设计。

## 三、概览页

只读，用于进入其他页签前的判断依据。

| 内容 | 说明 |
| --- | --- |
| 主机信息 | 主机名、系统版本、开机时长、SSH 连通性 |
| 资源占用 | 系统盘与数据盘容量、可用空间 |
| 端口监听 | 11451、11452、1234、1235 的监听状态 |
| 实例摘要 | 四个实例的运行/停止、PID、维护模式标记 |
| 计划任务 | `UEWatchdog` 与 `UEWatchdog-DailyReboot` 的当前状态 |

## 四、看门狗页

### 状态区

读取 `Manage-UEWatchdog.ps1 -Action Status -PassThru`，按实例展示：运行状态、PID、监听端口、进程启动时间、是否处于维护模式、最近日志文件与最后写入时间。

### 实例操作

每个实例一行，提供启动、停止、重启、查看日志、维护模式开关。前三项直接映射 `-Action Start|Stop|Restart -ServerName <实例>`；日志映射 `-Action Logs`。

维护模式开关需要新增非交互脚本，内部调用 `Set-ServerMaintenance`，并要求填写失效时间，避免遗忘后长期失去监控。

### 监控开关

| 操作 | 实现 |
| --- | --- |
| 暂停监控 | `Stop-ScheduledTask -TaskName UEWatchdog` |
| 恢复监控 | `Start-ScheduledTask -TaskName UEWatchdog` |
| 启用/停用每日重启 | `Enable/Disable-ScheduledTask -TaskName UEWatchdog-DailyReboot` |
| 发送测试邮件 | `Send-TestMail` |
| 查看最近告警 | 读取 `watchdog.messages.txt` 与 `C:\UEWatchdog\logs` |
| 修复看门狗 | 调用服务器上的 `Repair-WatchdogServer.ps1` |

暂停监控与停用每日重启属于降低可用性的操作，必须二次确认，并在界面上常驻显示当前状态。

## 五、服务端更新页

### 更新单位

| 程序 | 影响实例 | 目标目录 |
| --- | --- | --- |
| 零境交错服务端 | 登录大厅 + 主界面大厅 | `Desktop\WindowsServer\CrossingVoid\` |
| 火影服务端 | 火影 | `Desktop\BP_Server\WindowsServer\` |
| 幻杀服务端 | 幻杀 | `Desktop\幻杀_Server\WindowsServer\` |

更新零境服务端时两个实例会同时受影响，必须在同一个维护窗口内完成。

### 选择来源

上传入口是一个**文件夹选择器**，沿用项目已有的 `FolderPicker` + `PickSingleFolderAsync` 写法，选中 UE 打包输出的服务端目录后，AxTools 扫描其中的可执行文件与资源，确认与目标程序匹配才继续。

| 方式 | 流程 | 何时有用 |
| --- | --- | --- |
| 本机文件夹 | 选择本地目录 → 上传到服务器暂存目录 | 默认方式 |
| 从 URL 拉取 | 填一个已存在的下载地址，由服务器自己下载 | 构建产物已经在网上，例如合作方发了链接 |

关于「从 URL 拉取」：它**不能绕开家宽上行**。构建产物还在本机时，无论哪种方式都要先经本机上传（约 1.85 MB/s）；只有当产物本来就在互联网上时，服务器自己下载才有意义。因此默认只做文件夹选择，URL 拉取作为可选入口。

### 更新流程

按「不留备份、直接替换」设计：

```
1  预检      读取实例状态、系统盘可用空间、目标目录
2  上传      新构建传到同卷暂存目录 <目标目录>.incoming，此时服务仍在运行
3  校验      暂存目录的文件数量、总大小与哈希和源目录一致
4  置维护    对受影响实例置维护模式（必须先做，否则看门狗会在替换过程中拉起服务）
5  停服      停止受影响实例并确认进程退出、端口释放
6  切换      旧目录改名 → 暂存目录改名到正式路径（同卷改名，毫秒级）
7  启动      按看门狗配置的参数拉起实例
8  健康检查  进程存活 + 端口监听 + 最近日志无致命错误，宽限 180 秒
9  收尾      解除维护模式，删除旧目录残留
   失败      在停服状态下把旧目录改名回正式路径并重启；暂存目录保留供排查
```

两个关键点：

- **第 4 步不能省。** `Watchdog.ps1` 会在实例意外退出后自动拉起，若不在替换前进入维护模式，会出现文件占用或半更新状态。「直接替换」省掉的是备份，不是停服。
- **上传与切换分离。** 上传发生在服务运行期间，走暂存目录；真正停服的只有第 5 到第 7 步，中间是两次同卷改名，停服时间以秒计。即使上传中途失败，正式目录也不会变成半成品。

### 不留备份的取舍

不保留上一版构建后，新版本一旦有问题，只能重新打包上传，无法一键回滚。若之后觉得代价偏高，最小改动是把第 6 步的旧目录改名保留而不是删除。

### 版本记录

每次更新在服务器上写入一份 `server-version.json`，记录程序名、版本、更新时间和本次源目录哈希，用于判断服务器上跑的是哪一版。不留备份时它只作记录，不承担回滚。

## 六、配置模型

新增 `AppSettings.Servers`（`Dictionary<string, ServerProfile>`），`SchemaVersion` 递增。密钥与口令仍只走 SSH 配置，不写入 JSON。

```csharp
public sealed class ServerProfile
{
    public string StableKey { get; set; }      // AliyunControlPlane
    public string DisplayName { get; set; }
    public string Platform { get; set; }       // Windows
    public string SshTarget { get; set; }      // crossing-server
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string UserName { get; set; }
    public string WatchdogRoot { get; set; }   // C:\UEWatchdog
    public string ManageScriptPath { get; set; }
    public string SourceFolderHint { get; set; }  // 上次选择的服务端构建目录
}
```

## 七、代码与脚本落点

| 层 | 文件 |
| --- | --- |
| 模型 | `AxTools.Core/Models/ServerModels.cs` |
| 目录 | `AxTools.Core/Catalog/ManagedServerCatalog.cs` |
| 适配器 | `AxTools.Core/Servers/IServerAdapter.cs`、`ServerAdapterBase.cs`、`AliyunServerAdapter.cs` |
| 视图模型 | `AxTools.Core/ViewModels/ServerPageViewModel.cs` |
| 视图 | `Views/ServerPage.xaml`、`Views/ServerPage.xaml.cs` |
| 导航 | `MainWindow.xaml`（分组项）、`MainWindow.Navigation.cs`（Tag 映射） |
| 脚本公共层 | `Scripts/Common/AxRemoteServer.psm1` |
| 脚本入口 | `Scripts/Servers/Aliyun/Invoke-AliyunServerAction.ps1` |
| 服务器侧补充 | 维护模式与状态输出的非交互封装 |

`AxRemoteServer.psm1` 统一封装 SSH/SCP 子进程调用，沿用 PC 启动器里已验证的 `Invoke-OpenSshProcess` 修复（显式创建 stdin/stdout/stderr 管道），避免再次出现 `DuplicatedHandle() : dup() in/out/err failed` 导致 ssh 退出码 255。

远端 PowerShell 一律使用 `EncodedCommand` 传递，避免中文、引号与路径被远端 `cmd.exe` 解析。

## 八、实施顺序

| 阶段 | 内容 | 说明 |
| --- | --- | --- |
| 1 | 概览页 + 看门狗状态读取 | 纯只读，先验证 SSH 封装与页面骨架 |
| 2 | 实例控制：启动 / 停止 / 重启 / 日志 | 复用现成参数，风险低 |
| 3 | 维护模式与监控开关 | 需补服务器侧非交互封装 |
| 4 | 服务端更新：选文件夹、上传暂存、停服切换、健康检查 | 风险最高，需维护窗口 |

## 九、安全边界

- AxTools 只保存 SSH 别名与路径，不保存私钥或口令。
- 停服、替换、暂停监控、停用每日重启一律先扫描并显示影响范围，再二次确认。
- 更新前必须置维护模式，且维护模式需带失效时间。
- 不留备份，因此切换前必须完成暂存目录校验；校验不通过一律不进入切换步骤。
- 零境服务端更新会同时影响两个实例，界面上必须明确提示。

## 十、待确认

1. 三个服务端程序的构建产物目录结构（UE 打包输出路径），决定文件夹选择器的校验规则。
2. 是否需要保留「从 URL 拉取」入口；若不需要，本页只保留文件夹选择。
3. 暂存目录放在哪个盘、需要预留多少临时空间。
4. 是否需要把野草云的磁盘与证书状态也纳入本分区的只读页。
