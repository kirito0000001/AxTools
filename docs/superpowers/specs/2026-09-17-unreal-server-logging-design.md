# 虚幻服务端日志与崩溃诊断设计

目标：让阿里云上三个虚幻专用服务端的日志更详细、崩溃原因可定位，并把最有价值的证据纳入 AxTools 的监控范围。

## 一、现状（2026-09-17 实测）

| 服务端 | 日志位置 | 崩溃目录 |
| --- | --- | --- |
| 零境交错 | `Desktop\WindowsServer\CrossingVoid\Saved\Logs\CrossingVoid.log` | `Saved\Crashes\`，已有 **107** 个崩溃目录 |
| 火影BP | `Desktop\BP_Server\WindowsServer\NarutoBP\Saved\Logs\NarutoBP.log` | `NarutoBP\Saved\Crashes` |
| 幻杀 | `Desktop\幻杀_Server\WindowsServer\FantasyProject\Saved\Logs\FantasyProject.log` | `FantasyProject\Saved\Crashes` |

注意三个项目的日志目录结构并不一致：零境的 `Saved` 在工程根下，火影和幻杀的在 `WindowsServer\<工程名>\Saved`。看门狗里的崩溃目录探测因此同时支持「向上回溯」和「子目录形式」两种布局。

UE 自身会轮转日志，保留 `<Project>-backup-<时间>.log`。

### 崩溃目录里有什么

每个崩溃目录包含：

| 文件 | 价值 |
| --- | --- |
| `CrashContext.runtime-xml` | **最重要**：崩溃类型、错误条件、完整调用栈、命令行、模块列表 |
| `<Project>_2.log` | 崩溃时刻的服务端日志副本 |
| `UEMinidump.dmp` | 崩溃转储，需要调试器打开 |

`CrashContext.runtime-xml` 里的 `ErrorMessage` 同时包含错误条件和调用栈，例如实测的一份：

```
CrashType: Ensure
Ensure condition failed: !IsBunchTooLarge(Connection, Bunch)
  [File: ...\Runtime\Engine\Private\DataChannel.cpp] [Line: 1260]
Attempted to send bunch exceeding max allowed size.
BunchSize=312130, MaximumSize=65536
Channel: [UActorChannel] Actor: PC_MainBP_C ... Role: 3, RemoteRole: 2
Stack:
0x... CrossingVoidServer.exe!UChannel::SendBunch() [...DataChannel.cpp:1260]
0x... CrossingVoidServer.exe!UNetDriver::ProcessRemoteFunctionForChannelPrivate() [...NetDriver.cpp:3102]
...
```

也就是说：**崩溃根因一直都有记录，只是此前没有被采集**。

## 二、需要改进的四点

### 1. 崩溃上下文纳入监控（已实现）

看门狗在重启服务端时会读取最近 `CrashLookbackMinutes`（默认 30 分钟）内最新的 `CrashContext.runtime-xml`，提取 `CrashType` 与 `ErrorMessage`（含调用栈），写入 `state\crashes.json` 的 `crashContext` 字段。超出回溯窗口的旧崩溃不会混入本次记录。

### 2. 硬崩溃前的日志不能丢

虚幻默认对日志有缓冲，进程被强杀或硬崩溃时，**最后几十行往往写不进磁盘**，而这几行正是关键。

建议在服务端启动参数中加入：

```
-ForceLogFlush
```

每行日志立即刷盘，代价是少量 I/O。

### 3. 提高关键分类的详细度

当前启动参数只有 `-log`，日志级别为默认。建议按分类提高，二者选一：

**命令行方式**（改 `watchdog.config.psd1` 里各服务端的 `Arguments`）：

```
-LogCmds="LogNet Verbose,LogInit Verbose,LogLoad Verbose,LogWorld Verbose,LogGameMode Verbose,LogPlayerController Verbose,LogOnline Verbose,LogExit Verbose"
```

**配置文件方式**（更持久，推荐；放在 `<项目>\Saved\Config\WindowsServer\Engine.ini`）：

```ini
[Core.Log]
LogNet=Verbose
LogNetTraffic=Log
LogInit=Verbose
LogLoad=Verbose
LogWorld=Verbose
LogGameMode=Verbose
LogPlayerController=Verbose
LogOnline=Verbose
LogExit=Verbose
```

注意 `LogNet` 不要直接上 `VeryVerbose`，日志量会成倍增长；先 `Verbose` 观察。

### 4. 日志与崩溃目录的保留策略

零境已经积累 107 个崩溃目录，每个含一份日志副本和几百 KB 的转储。建议增加按数量或时间的清理（例如保留最近 20 个），并纳入 AxTools 的清理动作。

## 三、当前的采集面

| 来源 | 采集方式 |
| --- | --- |
| 看门狗自身日志 | `state\status.json` + scp 取 `logs\watchdog.log` |
| 各服务端 UE 日志 | 同上，按实例取 `<Project>.log` |
| Windows 应用程序日志 | 看门狗的 `Get-RecentCrashEvents`（Application Error / WER / .NET Runtime） |
| 崩溃上下文 | 新增，写入 `crashes.json` |
| 服务端运行状态 | `status.json`，AxTools 每 15 秒经 HTTPS 读取 |

## 四、实测发现的一个真实问题

零境交错（主界面大厅，UDP 11452）出现大量 `Ensure` 级崩溃：

```
Ensure condition failed: !IsBunchTooLarge(Connection, Bunch)
BunchSize=312130, MaximumSize=65536
Actor: PC_MainBP_C  Channel: UActorChannel
```

含义：某个复制调用（RPC 或属性复制）单次发送的数据包超过引擎 64KB 上限，引擎判定为不可恢复并生成崩溃报告。

常见成因：

- 一次性复制超大数组或字符串，例如整张玩家列表、长公告文本；
- 未对大数据做分片，走 Reliable RPC 直接下发。

处理方向（按推荐顺序）：

1. **拆包**：把该 RPC 的负载改成多次、分批下发；或对数组做分页同步。
2. **降低单次载荷**：确认 `PC_MainBP_C` 上新增/修改过哪些复制变量或 Multicast。
3. 临时缓解可调 `net.MaxBunchSize`，但这只是把阈值抬高，问题仍会以别的方式出现，不建议作为最终方案。

这条信息完全来自崩溃上下文，若不采集就只会表现为「服务端又掉了」。

## 五、实施顺序

| 阶段 | 内容 | 影响 |
| --- | --- | --- |
| 1 | 看门狗采集崩溃上下文 | 已实现，无需重启服务端 |
| 2 | 启动参数加 `-ForceLogFlush` | 需重启服务端生效 |
| 3 | `Engine.ini` 提高关键分类的详细度 | 需重启服务端生效，日志量上升 |
| 4 | 崩溃目录保留策略 | 可与 AxTools 清理动作合并 |

阶段 2、3 都会改变服务端的启动或配置，建议在维护窗口内进行，并在改动前备份 `watchdog.config.psd1` 与 `Engine.ini`。
