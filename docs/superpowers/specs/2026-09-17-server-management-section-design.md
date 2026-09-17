# AxTools 服务器分区设计

目标：在 AxTools 左侧导航新增独立的「服务器」分区，集中管理现有两台服务器上的运维与发布事务。

范围：阿里云控制面（UE 看门狗 + 游戏服务端）、野草云数据面（客户端资源分发）。

架构：新增「服务器档案（ServerProfile）+ 服务器适配器（IServerAdapter）」两条并列于现有工具适配器的概念，复用现有任务执行器、`::axtools` 结构化事件协议、日志与任务历史。服务器动作落在 `Scripts/Servers/<Server>/`，不改动现有 `Scripts/Adapters/<Tool>/`。

## 一、现状（2026-09-17 实测）

### 1. 阿里云 · crossing-server（控制面）

| 项目 | 实测值 |
| --- | --- |
| 地址 / 账号 | `47.109.153.36`，`Administrator`，SSH 别名 `crossing-server` |
| 主机名 | `iZjg5pd9l69hpuZ` |
| IIS | Default Web Site → `C:\inetpub\wwwroot`，绑定 `www.crossingvoid.top`（80/443） |
| 关键端口 | 22 SSH、80/443 IIS、3306 MySQL、3389 RDP、8888 面板、51987 ToolboxUpdateServer、51988/51989 回环中继、60888 |
| 桌面目录 | `BP_Server`、`OSSAPI`、`QQ-Codex`、`SaveGames备份`、`UEWatchdog`、`WindowsServer`、`和谐家园`、`幻杀_Server` |

ToolboxUpdateServer 是 .NET 8 自包含 ASP.NET Core 应用，位于 `Desktop\OSSAPI\ToolboxUpdateServer\app`，AccessKey 只存在系统环境变量。`appsettings.json` 关键配置：

```
Oss.Bucket            = download-server-xj
Oss.Region            = cn-chengdu
Oss.SignExpireSeconds = 600
TrafficQuota          = ossbag / 下行流量 / 低阈值 3GB
```

已注册的更新产品：`crossingvoid-game`、`crossingvoid-android-game`、`crossingvoid-launcher`、`crossingvoid-launcher-pc`、`fantasy-tools`。

UE 看门狗位于 `C:\UEWatchdog`，脚本在 `Desktop\UEWatchdog`。`watchdog.config.psd1` 实测：

```
CheckIntervalSeconds = 30      RestartCooldownSeconds = 60
StartGraceSeconds    = 180     CrashLookbackMinutes   = 30
LogDirectory         = C:\UEWatchdog\logs
MaintenanceFile      = C:\UEWatchdog\maintenance.txt
Mail                 = smtp.qq.com:587，已启用
```

被监控的四个游戏服务端：

| 名称 | 显示名 | 端口 | 可执行文件 |
| --- | --- | --- | --- |
| CrossingVoid-Login | 零境 - 登录大厅 | UDP 11451 | `Desktop\WindowsServer\CrossingVoid\Binaries\Win64\CrossingVoidServer.exe` |
| CrossingVoid-Main | 零境 - 主界面大厅 | UDP 11452 | 同上，参数不同 |
| NarutoBP | 火影 | UDP 1234 | `Desktop\BP_Server\WindowsServer\NarutoBPServer.exe` |
| FantasyProject | 幻杀 | UDP 1235 | `Desktop\幻杀_Server\WindowsServer\FantasyProjectServer.exe` |

QQ-Codex 守护在配置中默认关闭。

### 2. 野草云 · yecaoyun-hk（数据面）

| 项目 | 实测值 |
| --- | --- |
| 地址 / 账号 | `207.57.125.218`，`root`，SSH 别名 `yecaoyun-hk` |
| 系统 | Debian 12 (bookworm)，主机名 `yc-138-Crossingvoid` |
| 磁盘 | 30G，已用 1.7G |
| nginx | 1.22.1，`sites-enabled/dl` → `sites-available/dl` |
| 站点根 | `/srv/downloads`，`autoindex on`，`charset utf-8` |
| 限速 | `limit_rate_after 64m`、`limit_rate 8m`、`limit_conn perip 6`（`limit_conn_zone` 10m） |
| 证书 | Let's Encrypt `dl.crossingvoid.top`，2026-12-16 到期，自动续期 |
| 运行服务 | 仅 nginx 与 sshd（外加系统默认单元） |
| 目录内容 | 仅 `speedtest.bin`（200MB）、`test-50m.bin`（50MB）两个测速文件 |

目前只有 `root` 可写，尚无受限上传账号。

### 3. 客户端当前如何判断版本

启动器不写死版本号，全部读服务端 JSON：

| 用途 | 地址 | 缓存头 |
| --- | --- | --- |
| 启动器自身更新 | `https://www.crossingvoid.top/api/toolbox-updates/<framework>/<product>/<os>/<arch>/<current>` | 由 ToolboxUpdateServer 控制 |
| 游戏资源清单 | `https://www.crossingvoid.top/manifests/game/windows-latest.json`（另有 `android-latest.json`） | `no-store, must-revalidate, no-cache` |

客户端还会在清单 URL 后追加 `?t=<时间戳>`，因此清单层不存在缓存滞留风险。

清单结构（schemaVersion = 2）实测：

| 平台 | 版本 | 归档 | 大小 | 分片 |
| --- | --- | --- | --- | --- |
| Windows | V0.5.12 | `CrossingVoid.zip` | 2,368,418,317 B | 5 |
| Android | V0.5.12 | `CrossingVoid-Android-Package.zip` | 1,970,478,795 B | 4 |

每个分片带 `index`、`count`、`fileName`、`githubFileName`、`objectKey`、`sha256`、`sizeBytes`。

下载链路：

```
官方源：POST /api/toolbox-updates/sign-download {productKey, version, runtime, objectKey, launcherVersion}
        → 返回签名 URL → 从 OSS 下载
GitHub：直接按 downloadReleaseTag + githubFileName 拼接 Release 资源地址
```

结论：换下载源只需改服务端 `sign-download` 返回的 URL，客户端零改动、零重新发版。

## 二、职责划分：控制面 / 数据面

保持下列边界，可以让任意一侧单独更换而不影响客户端：

| 层 | 归属 | 内容 |
| --- | --- | --- |
| 控制面 | 阿里云 | 版本清单、`sign-download` 签名、白名单、看门狗、游戏服务端 |
| 数据面 | 野草云 | 大文件字节流，按版本目录存放 |

游戏包迁移到野草云时，推荐让 `sign-download` 直接返回 `https://dl.crossingvoid.top/<version>/<file>`，OSS 保留为回退源。这样清单与签名仍在阿里云，玩家端无需更新启动器。

## 三、导航与页面结构

左侧导航新增一个可展开的「服务器」分组，下挂两个页面。分组按机器划分而非按事项划分，因为看门狗与被监控的游戏服务端在同一台机器上，拆成两个入口会割裂状态与控制。

```
服务器
├── 阿里云 · 零境服务器      crossing-server
└── 野草云 · 资源分发        yecaoyun-hk
```

实现上使用带子项的 `NavigationViewItem`（子项 `Tag` 为 `ServerAliyun` / `ServerYecaoyun`），沿用现有 `ShowPageByTag` 的显隐切换方式。

### 阿里云页

| 分区卡 | 内容 |
| --- | --- |
| 概览 | 主机名、系统版本、磁盘、内存、开机时长、SSH 连通性、关键端口监听 |
| 游戏服务端 | 四行服务表：名称、协议端口、进程状态、PID、启动时间、最近日志时间；行内启动/停止/重启/查看日志 |
| 看门狗 | 看门狗状态、检查间隔、最近检查结果、维护模式开关、邮件告警状态、发送测试邮件、最近告警摘要 |
| 服务端更新 | 选择服务端构建 → 上传到临时目录 → 校验 → 停服 → 备份 → 替换 → 启动 → 健康检查，失败自动回滚 |

### 野草云页

| 分区卡 | 内容 |
| --- | --- |
| 概览 | 系统、磁盘与可用空间、nginx 状态、证书到期日、限速参数 |
| 资源分发 | `/srv/downloads` 目录树：版本目录、文件、大小、修改时间、总占用 |
| 上传 | 选择本地包 → 上传到 `/srv/downloads/<version>/<file>` → 校验 SHA-256 与大小 → 输出下载 URL |
| 清单 | 生成/更新 `latest.json`，校验 URL 实际可达且响应头正确 |
| 清理 | 删除指定旧版本目录，需二次确认并显示占用 |

## 四、动作清单

新增 `ServerAction` 枚举：

```
CheckEnvironment        服务器体检（只读）
InspectServices         游戏服务端状态（只读）
StartService            启动服务端
StopService             停止服务端
RestartService          重启服务端
ViewServiceLog          读取最近日志
WatchdogStatus          看门狗状态（只读）
SetMaintenance          切换维护模式
TestWatchdogMail        发送测试邮件
RepairWatchdog          安装/修复看门狗
UploadServerBuild       上传服务端构建
ReplaceServerBuild      停服替换并启动
RollbackServerBuild     回滚到上一版
ScanDownloads           扫描分发目录（只读）
UploadResource          上传资源文件
VerifyResource          校验远端文件哈希
RemoveResource          删除资源
PublishManifest         生成并发布清单
VerifyManifestUrl       校验清单与下载 URL
```

只读动作不进入确认流程；所有写操作、删除与替换均需二次确认。

## 五、配置模型

新增 `AppSettings.Servers`（`Dictionary<string, ServerProfile>`），`SchemaVersion` 递增。密钥与口令仍只走 SSH 配置与环境变量，不写入 JSON。

```csharp
public sealed class ServerProfile
{
    public string StableKey { get; set; }      // AliyunControlPlane / YecaoyunDownload
    public string DisplayName { get; set; }
    public string Platform { get; set; }       // Windows / Linux
    public string SshTarget { get; set; }      // crossing-server / yecaoyun-hk
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string UserName { get; set; }
    public string RootPath { get; set; }       // 服务器上的管理根目录
    public string PublicBaseUrl { get; set; }  // https://dl.crossingvoid.top
    public string PublishVersion { get; set; }
}
```

## 六、代码与脚本落点

| 层 | 文件 |
| --- | --- |
| 模型 | `AxTools.Core/Models/ServerModels.cs` |
| 目录 | `AxTools.Core/Catalog/ManagedServerCatalog.cs` |
| 适配器 | `AxTools.Core/Servers/IServerAdapter.cs`、`ServerAdapterBase.cs`、`AliyunServerAdapter.cs`、`YecaoyunDownloadAdapter.cs` |
| 视图模型 | `AxTools.Core/ViewModels/ServerPageViewModel.cs` |
| 视图 | `Views/ServerPage.xaml`、`Views/ServerPage.xaml.cs` |
| 导航 | `MainWindow.xaml`（分组项）、`MainWindow.Navigation.cs`（Tag 映射） |
| 脚本公共层 | `Scripts/Common/AxRemoteServer.psm1` |
| 脚本入口 | `Scripts/Servers/Aliyun/Invoke-AliyunServerAction.ps1`、`Scripts/Servers/Yecaoyun/Invoke-YecaoyunDownloadAction.ps1` |

`AxRemoteServer.psm1` 统一封装 SSH/SCP 子进程调用，直接沿用 PC 启动器里已验证的 `Invoke-OpenSshProcess` 修复（显式创建 stdin/stdout/stderr 管道），避免再次出现 `DuplicatedHandle() : dup() in/out/err failed` 导致 ssh 退出码 255。

## 七、实施顺序

| 阶段 | 内容 | 说明 |
| --- | --- | --- |
| 1 | 只读概览：服务器体检、游戏服务端状态、野草云目录与磁盘/证书 | 无写操作，先验证 SSH 封装与页面骨架 |
| 2 | 野草云资源上传：上传 + 哈希校验 + URL 输出 | 当前最急，服务器侧已就绪 |
| 3 | 看门狗：状态、维护模式、测试邮件、日志、修复 | 复用服务器上现有 `Manage-UEWatchdog.ps1` |
| 4 | 游戏服务端更新：上传、停服替换、健康检查、回滚 | 风险最高，需要维护窗口与备份 |
| 5 | 清单与下载源切换：`sign-download` 指向野草云，OSS 留作回退 | 服务端改动，客户端零改动 |

## 八、安全边界

- 野草云新建 `uploader` 账号：仅 SFTP、chroot 到 `/srv/downloads`、独立密钥、禁用 shell 与密码登录；`root` 不进入任何客户端配置。
- AxTools 只保存 SSH 别名与路径，不保存私钥、口令或 AccessKey。
- 删除、替换、停服一律先扫描并显示影响范围，再要求二次确认。
- 游戏服务端更新必须先备份当前构建，健康检查失败自动回滚。
- 看门狗维护模式必须设置自动失效时间，避免遗忘后长期失去监控。

## 九、待确认

1. ToolboxUpdateServer 的源码位置：服务器上只有发布产物，本机未找到对应 `csproj`，后续要改 `sign-download` 需要先定位源码。
2. 游戏资源的长期归属：迁到野草云为主、OSS 为备，还是 OSS 为主、野草云做镜像。
3. 游戏服务端构建产物从哪里产出（UE 打包输出路径），决定「服务端更新」的取件来源。
4. 上传账号命名，例如 `uploader` 或 `ax-uploader`。
5. 是否需要在野草云再放一份清单（建议不复制，控制面统一留在阿里云）。

## 十、对服务器配置方的两个接口回答

| 问题 | 回答 |
| --- | --- |
| 上传协议 | SFTP（SSH 子系统）。AxTools 已用 ssh/scp，服务器已开 SSH，只需新建受限账号，无需安装 FTP/WebDAV。 |
| 版本判断 | 读服务端 JSON 清单，客户端不写死。启动器走 `www.crossingvoid.top/api/toolbox-updates`，游戏资源走 `www.crossingvoid.top/manifests/game/*-latest.json`，两者均需 no-cache（现状已满足）。 |
