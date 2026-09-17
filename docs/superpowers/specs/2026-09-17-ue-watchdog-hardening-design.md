# UE 看门狗诊断与加固设计

目标：修复阿里云服务器上 UE 看门狗的既有故障，并把它改造成 AX 服务器分区可稳定读取状态、可查询崩溃记录、可自动恢复的组件。

范围：`C:\UEWatchdog`（阿里云 `crossing-server`）。不涉及野草云。

## 一、故障现象

| 现象 | 证据 |
| --- | --- |
| 每天 04:00 重启后服务端起不来 | `watchdog-task-error.log` 从 09-10 到 09-17 连续 8 天记录同一错误 |
| 看门狗任务显示失败 | `UEWatchdog` 任务 `LastTaskResult=1` |
| 服务端整日无人看管 | 09-17 04:01 失败后，四个实例直到 21:34 仍未运行 |

每日重启任务本身是成功的（`UEWatchdog-DailyReboot` 的 `LastTaskResult=0`），失败的是重启后由开机触发器拉起的看门狗进程。

## 二、根因

### 缺陷 1：健康检查在报告异常时抛异常，导致看门狗整体退出

`Watchdog.ps1` 第 235 行原为：

```powershell
$issues = New-Object System.Collections.Generic.List[object]
```

随后第 278 行 `return @($issues)` 会抛 `System.ArgumentException: Argument types do not match`，也就是日志里的「参数类型不匹配」。

最小复现（Windows PowerShell 5.1 与 PowerShell 7.6 均成立）：

```powershell
$l = New-Object System.Collections.Generic.List[object]
@($l).Count                                                       # 抛 ArgumentException
$l2 = [System.Collections.Generic.List[object]]::new()
@($l2).Count                                                      # 正常
$l3 = New-Object System.Collections.Generic.List[string]
@($l3).Count                                                      # 正常
```

问题只出在 `New-Object` 创建的 `List[object]` 被数组子表达式枚举时。全文件另外两处使用 `List[string]`，不受影响。

影响面之所以致命，是因为 `Get-ServerHealthReason` 只在「服务端异常、需要重启」时被调用。也就是说，看门狗每次真正需要干活时都会崩溃；重启后所有服务端都是停止状态，第一次检查必然走到这里，于是每天都死在同一处。

### 缺陷 2：重复进程清理依赖时序，会误杀真正在跑的进程

火影与幻杀的服务端各自会派生一个同名子进程（实测 `5380 → 5128`、`3180 → 1588`），因此看门狗经常看到两个同名进程。

原逻辑在端口尚未绑定时回退为「保留 `processes[0]`」：

```powershell
if ($Health.MatchingPortOwnerIds.Count -gt 0) {
    $keepProcessId = [int]$Health.MatchingPortOwnerIds[0]
}
else {
    $keepProcessId = [int]$processes[0].ProcessId
}
```

于是清理时可能杀掉持有端口的那一个，剩下的进程随后也退出。09-17 21:35 实测：清理后火影 2740 与幻杀 5024 双双消失，端口 1234/1235 无监听。

## 三、已实施的修复

两处修复均已部署到 `C:\UEWatchdog\Watchdog.ps1`，改动前留有备份，并通过 PowerShell 语法解析校验。

| 编号 | 位置 | 改动 | 备份 |
| --- | --- | --- | --- |
| 1 | 第 235 行 | `New-Object System.Collections.Generic.List[object]` 改为 `[System.Collections.Generic.List[object]]::new()` | `backups\Watchdog.ps1.bak-20260917-213427` |
| 2 | `Stop-DuplicateServerProcesses` | 无端口所有者时不做清理；保留端口所有者的整条父子链，只有无关进程才判定为重复 | `backups\Watchdog.ps1.bak-20260917-213924-dupfix2` |

修复 1 用三种故障场景验证（进程缺失、端口被占用、进程存活但端口未监听）均能正确产出原因文本；修复 2 用六种进程组合验证，其中「端口属于父进程」这一原本会误杀的场景已不再清理。

部署后实测：21:34 启动看门狗，四个实例全部被拉起，11451 / 11452 / 1234 / 1235 四个端口均在监听。

## 四、仍然存在的结构性缺陷

这些是本次未改动、但会导致看门狗自身失联的部分：

1. **主循环没有异常保护**。`while ($true)` 内部任何未捕获异常都会让整个看门狗进程退出，之后只能等下次开机由启动触发器拉起。任务虽配置 `RestartCount=3 / RestartInterval=PT1M`，但连续失败三次后同样不再拉起。
2. **没有状态输出**。AX 目前只能通过 SSH 执行脚本或抓日志判断状态，代价高，且易受远端输出编码影响。
3. **闪退信息只进邮件**。`Get-RecentCrashEvents` 已经会读 Windows 应用程序日志与 UE 日志尾部，但这些内容只拼进重启通知邮件，没有落盘成可查询记录。
4. **每日重启缺少兜底**。04:00 依赖开机触发器把看门狗带起来；一旦启动失败就会整天无人监控（本次即如此），且没有启动失败的二次告警。

## 五、加固方案

### A. 单实例隔离

把 `while` 循环里每个实例的检查包进 `try/catch`：单个实例出错只写日志并继续下一个，不再中断整轮。这是投入最小、收益最大的一步。

### B. 看门狗看门狗

新增一个每 5 分钟运行的计划任务，检查 `UEWatchdog` 是否在运行；不在则启动，并记录一次事件。这样即使看门狗进程因未知原因退出，最多 5 分钟即可恢复，不必等下次开机。

### C. 状态文件

每轮检查结束写一份 `C:\UEWatchdog\state\status.json`：

```
generatedAt            生成时间
watchdog               PID、启动时间
instances[]            name / displayName / port / protocol / running / pid /
                       portListening / maintenance / lastRestartAt / lastError
dailyReboot            下次运行时间、上次结果
```

AX 只读取这个文件即可展示状态，不必在服务器上执行判断逻辑，速度快且不受编码问题影响。

### D. 崩溃记录

把 `Get-RecentCrashEvents` 的结果落盘为 `C:\UEWatchdog\state\crashes.json`，滚动保留最近若干条，每条包含时间、实例名、退出码（若可得）、Windows 事件日志摘要与 UE 日志尾部若干行。

同时追加 `C:\UEWatchdog\state\events.ndjson`（每行一条 JSON），记录启动、异常、重启、维护模式切换等事件，便于 AX 按时间倒序展示。

### E. 每日重启兜底

两条路线二选一：

- 由看门狗自身执行受控重启：先停四个实例，再重启系统，避免重启时进程被强杀。
- 保留现有 `UEWatchdog-DailyReboot`，但增加启动校验：开机后 3 分钟内看门狗未运行或实例未全部就绪，则发送告警邮件并再触发一次启动。

### F. 健康判定

当前以「进程存在 + 端口监听」为准。可增加「最近日志无致命错误」作为软判据，并对火影、幻杀这类会派生子进程的服务端，统一按进程家族而不是同名进程数量判定健康。

## 六、AX 侧接口契约

| 用途 | 方式 |
| --- | --- |
| 读取状态 | SSH 读取 `C:\UEWatchdog\state\status.json`，只读单文件 |
| 读取崩溃 | SSH 读取 `C:\UEWatchdog\state\crashes.json` |
| 单实例操作 | `Manage-UEWatchdog.ps1 -Action Status\|Start\|Stop\|Restart\|Logs -ServerName <实例> -PassThru` |
| 维护模式 | 调用服务器侧 `Set-ServerMaintenance -Name <实例> -Enabled $true/$false` |
| 监控开关 | `Start/Stop-ScheduledTask -TaskName UEWatchdog` |
| 每日重启开关 | `Enable/Disable-ScheduledTask -TaskName UEWatchdog-DailyReboot` |

SSH 调用统一走 AX 侧公共封装（显式创建 stdin/stdout/stderr 管道），远端 PowerShell 一律用 `EncodedCommand` 传递。

## 七、实施顺序

| 阶段 | 内容 | 理由 |
| --- | --- | --- |
| 1 | A + B | 最小改动即可消除整天失联的风险 |
| 2 | C + D | 产出 AX 可读的状态与崩溃数据 |
| 3 | AX 看门狗页 | 先只读展示，再补操作 |
| 4 | E | 让每日重启具备兜底与告警 |

## 八、风险与回滚

- 每次改动前在 `C:\UEWatchdog\backups` 留带时间戳的备份，回滚即复制覆盖。
- 看门狗改动后先用语法解析校验，再在本地用合成数据验证判定逻辑，最后才部署。
- 重启系统类改动必须先确认四个实例可正常被拉起，否则会重演重启后无人拉起。
- 新文件要到看门狗下次启动才生效；正在运行的实例仍执行内存中的旧代码。

## 九、实施记录（2026-09-17）

方案 A、B、C、D 已落地并在生产验证。

| 项 | 落地内容 |
| --- | --- |
| A | `Watchdog.ps1` 新增 `Invoke-WatchdogServerCycle`，主循环按实例 `try/catch`，单个实例出错只记录并继续 |
| B | 新增计划任务 `UEWatchdog-Guard`（每 5 分钟），运行 `Ensure-WatchdogRunning.ps1`，发现看门狗不在则拉起并记录事件 |
| C | 每轮写 `C:\UEWatchdog\state\status.json`（schemaVersion 1：watchdog + instances + 每实例 running/pid/portListening/maintenance/lastRestartAt/action/lastError） |
| D | 重启时写 `state\crashes.json`（滚动保留 50 条，含原因、Windows 崩溃事件、UE 日志尾部）与 `state\events.ndjson` 事件流 |

部署后实测：21:51 重启看门狗，四个实例全部正常，`status.json` 每 30 秒刷新，四个端口均在监听。

文件备份：

```
C:\UEWatchdog\backups\Watchdog.ps1.bak-20260917-213427            (缺陷 1 修复前)
C:\UEWatchdog\backups\Watchdog.ps1.bak-20260917-213924-dupfix2    (缺陷 2 修复前)
C:\UEWatchdog\backups\Watchdog.ps1.bak-20260917-215042-watchdog-hardening
```

### AX 侧消费方

`ServerStatusService` 通过 `Scripts/Servers/Aliyun/Read-AliyunServerStatus.ps1` 经 SSH 读取 `status.json`，`Views/ServerPage.xaml` 在服务器分区内每 5 秒刷新一次，按实例显示状态灯。轮询只在进入服务器分区时运行，离开即停止。

尚未实现：方案 E（每日重启兜底告警）与方案 F（日志软判据）。当前每日重启仍依赖 `UEWatchdog-DailyReboot` + 开机触发器；由于缺陷 1 已修复，该链路已恢复可用，`UEWatchdog-Guard` 也提供了额外兜底。
