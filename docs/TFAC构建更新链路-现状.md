# TFAC 平台构建更新链路 · 现状研读（只读，未改代码）

> 日期：2026-10-19（晚间）。范围：AxTools「TFAC启动平台」页 + `D:\UnrealMap\CrossingVoidinitiator-PC`（PC 启动器）+ 对照 `CrossingVoidinitiator-Android`（零境安卓启动器）+ Aliyun `crossing-server` 上的 `ToolboxUpdateServer`。
> 结论先行：**TFAC 原来只有一条自更新路径——Tauri updater → `www.crossingvoid.top` 上的 .NET 接口 → 阿里云 OSS 签名链接；OSS bucket `download-server-xj` 已被整个删除（`NoSuchBucket`），Gitee 旧发布仓库也早已转私有且被 Gitee 判定「RAW 外链滥用」不允许再公开。线上任何「更新启动器」当时都下不动。**
>
> **2026-10-19 晚已落地新链路**：新建公开仓库 `xiaojie578/CrossingVoid-Launcher-PC` → Gitee Release 资产 + 站点静态清单（`pc-latest.json` Tauri + `pc-installer-latest.json` 扁平）→ 客户端扁平清单优先、Tauri 兜底。落地细节见 §8；本文其余章节保留当时的研读结论（含已被证伪的一条：旧仓库不是被删）。

---

## 0. 术语对齐（先说清谁是谁）

| 说法 | 实际对象 |
|---|---|
| TFAC | 零境交错的产物代号（游戏容器目录 `D:\TFAC-hz64`、包名 `com.TFAC.CorssingVoid`） |
| TFAC启动平台 | AxTools 的一个工具页（`CrossingVoidPcAdapter.cs:59`），管的是 **PC 启动器** `D:\UnrealMap\CrossingVoidinitiator-PC` |
| 零境安卓启动器 | AxTools「平台-零境-安卓」页（`CrossingVoidAndroidAdapter.cs:69`），管 `D:\UnrealMap\CrossingVoidinitiator-Android` |
| 「OSS 更新路径」 | PC 启动器自更新的安装包托管 + 服务器 `update.json` 的 `objectKey` 签名（不是游戏包分发；游戏包已改走下载站） |

AxTools 侧配置（`D:\NewData\Ax工具箱项目\AxTools.settings.json`）：

```text
managedTools["CrossingVoidinitiator-PC"]
  sourceRoot      D:\UnrealMap\CrossingVoidinitiator-PC
  outputRoot      D:\NewData\Ax工具箱项目\Artifacts\CrossingVoidinitiator-PC  (脚本再拼一层 Launcher)
  publishVersion  1.4.0
managedTools["CrossingVoidinitiator-Android"]
  sourceRoot      D:\UnrealMap\CrossingVoidinitiator-Android
  publishVersion  1.4.10
```

---

## 1. TFAC 构建链（现状）

### 1.1 三个入口，同一套脚本

| 入口 | 位置 | 调用 |
|---|---|---|
| AxTools 工具页 | `AxTools.Core/Adapters/CrossingVoidPcAdapter.cs:26-31` | `Scripts/Adapters/CrossingVoidPc/Invoke-CrossingVoidPcAction.ps1` |
| 启动器内「开发者」页（仅 dev 构建） | `src/dev/useDeveloperConsole.ts` → Rust `dev_run_launcher_script`（`lib.rs:3256-3261`） | 同上两个 PS 脚本 |
| 手工 | 命令行 | `Scripts/Publish-LauncherGiteePackage.ps1` |

AxTools 动作表：`构建启动器包 / 校验启动器包 / 上传演练 / 单独上传 / 发布演练 / 完整发布`。
注意 `SetVersion` 在脚本里有实现（`Invoke-CrossingVoidPcAction.ps1:223`），但**动作表没挂**，所以版本只能在启动器开发页里改。

### 1.2 构建：`Scripts/Build-LauncherUpdaterPackage.ps1`

- 版本：读 `Saved/Launcher/developer-version.json`（当前 `1.4.0`）→ 环境变量 `CV_LAUNCHER_VERSION` + 临时 Tauri 配置覆盖（`tauri-version.override.json`）。`package.json` / `tauri.conf.json` 里的 `1.0.3` 只是源码回退值（指南 §6）。
- 签名：`src-tauri/private/updater.key`（本机存在，348 B）→ `TAURI_SIGNING_PRIVATE_KEY`；`tauri.conf.json:62` 的 `pubkey` 是配对公钥，两者同一套，换链路不用换密钥。
- 分两段构建：`tauri build --no-bundle` + `tauri bundle`（NSIS，`installMode: passive`），改安装包图标后**重新签名**（`:369-375`）。
- 产物：
  - `dist-launcher-update`：`零境启动器_1.4.0_x64-setup.exe` 44,477,773 B / sha256 `59874e90…5de2f`、同名 `.sig` 430 B、`latest.json`、`update.json`
  - `输出根\Launcher`：只导出 **安装包 + .sig + `update.json`**（`Export-ReleasePackageFiles` 不拷 `latest.json`；OSS 脚本之后还会补 `update.pc.json` / `update.legacy.json`）
  - `latest.json` = Tauri updater 格式，`platforms.windows-x86_64.url` **构建时就写成 Gitee Release 地址**（`:388`）
  - `update.json` = 后端 ToolboxUpdateServer 格式（`latest/versions/assets`），`downloadUrl` 同样指向 Gitee

### 1.3 发布：`Scripts/Publish-LauncherGiteePackage.ps1`

```text
（可选构建）
 → Ensure-GiteeRelease  launcher-v<ver>  @ xiaojie578/CrossingVoid-Downloader-PC:master
 → 上传 exe + .sig 附件（脚本硬编码 100 MiB 上限，:19/:226）
 → Publish-GiteeRepositoryFile  写仓库 launcher/latest.json（:381）
 → 必调 Publish-LauncherUpdaterPackage.ps1（:384-398，脚本不存在直接 throw）
```

最后这一步就是 OSS 路径（指南 §20.3）：

- `ossutil` 上传到 `oss://download-server-xj/toolboxes/crossingvoid-launcher-pc/releases/<ver>/<sha256>/windows-x86_64/<exe>`
- 把 `objectKey` 塞进 `latest.assets` / `versions[].assets`
- `ssh crossing-server` 覆盖服务器上两份清单：
  `…\OSSAPI\ToolboxUpdateServer\app\Data\products\crossingvoid-launcher\update.json`（legacy）
  `…\products\crossingvoid-launcher-pc\update.json`
- 顺手删 OSS 上旧版本目录（只保留当前版）

**这条硬依赖现在是个坑**：Gitee 仓库没了、OSS 没了，但脚本依然必须调用它——也就是说今天点「完整发布」不会"跳过 OSS 继续走"，而会在这三步里失败（且 `Assert-CrossingPublicVersion` 之后还会再失败一次，见 §2.3）。

### 1.4 断点与校验

| 动作 | 脚本行为 |
|---|---|
| `ValidatePackage` | `Test-CrossingPackage`：输出目录或 `dist-launcher-update` 里找 `latest.json`，必须有 Windows `signature`，同目录有 `*setup.exe` + `.sig` |
| `Upload` | 复用断点包 → 只发布不构建 → 再跑公网版本核验 |
| `Publish` | `Test-CrossingPublishCheckpoint` 有效就跳过构建（按钮会显示"检测到有效发布断点"），否则完整构建 |

---

## 2. TFAC 更新链（现状）与它现在断在哪

### 2.1 客户端

`src-tauri/tauri.conf.json:57-66`：

```json
"updater": {
  "endpoints": ["https://www.crossingvoid.top/api/toolbox-updates/tauri/crossingvoid-launcher-pc/windows/x86_64/{{current_version}}"],
  "pubkey": "dW50cnVzdGVkIGNvbW1lbnQ6…",
  "windows": { "installMode": "passive" }
}
```

- `App.vue:4655` 调 `check()`，**不传任何参数** → 端点完全来自编译期配置（`@tauri-apps/plugin-updater` 的 `CheckOptions` 只有 headers/timeout/proxy/target/allowDowngrades，没有 endpoints）。
- `App.vue:4694-4732`：`update.download()` → Tauri 校验 minisign → NSIS passive 安装 → `relaunch()`。
- 闸门：`launcherUpdateGate = updateRequired` 时禁止游戏下载（`App.vue:4621-4630`）；`verificationFailed` 也拦。
- 客户端代码里**没有任何 gitee 端点**，而且 `tests/launcherPublishing.test.ts:59-64` 有一条明确断言：端点必须是上面那个 www 地址、且 `tauri.conf.json` 里**不允许出现 `gitee.com`**。这是当初有意为之的规则，改链路必须动这条测试。

### 2.2 服务端

- IIS 站点 `www.crossingvoid.top` → `C:\inetpub\wwwroot`；更新接口是另一个 .NET 程序：
  `C:\Users\Administrator\Desktop\OSSAPI\ToolboxUpdateServer\app\ToolboxUpdateServer.dll`（2026-09-21 构建；本机 `D:\` 深度 4 内没找到它的源码工程，只有服务器上的编译产物 + `backup-*` 目录）。
- `appsettings.json` 仍写着 `Oss.Bucket = download-server-xj` / `SignExpireSeconds = 600`；`TrafficQuota` 已经切到下载站的 `https://dl.crossingvoid.top/_status/traffic.json`（Edge 口径）——即 10/18 那次"流量统计改下载站"。
- 从 DLL 里能直接读出这条路线的语义：`缺少 objectKey`、`生成 OSS 临时签名链接失败`、`请检查服务端 OSS 配置`、`更新包缺少 Tauri signature`、`Failed to sign OSS URL. Product={ProductKey}…ObjectKey={ObjectKey}`。
  → **Tauri 通道必须用 `objectKey` 现场签 OSS 链接，没有 `downloadUrl` 直出的兜底分支。**

### 2.3 线上实测（2026-10-19 20:2x，只读）

| 请求 | 结果 |
|---|---|
| `GET /api/toolbox-updates/tauri/crossingvoid-launcher-pc/windows/x86_64/0.0.0` | `200`，`version=1.4.0`，`url=https://download-server-xj.oss-cn-chengdu.aliyuncs.com/…零境启动器_1.4.0_x64-setup.exe?Expires=1792412974&OSSAccessKeyId=…`，`pub_date=2026-10-12T03:25:02Z` |
| 对该 `url` 发 `HEAD` | `404` |
| 对该 `url` 发 `GET` 看报错体 | `<Code>NoSuchBucket</Code>` → **整个 bucket 不存在**，不是单个对象丢 |
| `GET /api/toolbox-updates/tauri/crossingvoid-launcher/…`（legacy 产品键） | `200`，同一份 1.4.0 / 同一条死 OSS 链接 |
| `Gitee api /repos/xiaojie578/CrossingVoid-Downloader-PC` | `404`（网页 `403`） |
| 该账号公开仓库列表 | `CrossingVoid`、`CrossingVoid-Downloader-Android`、`CrossingVoidZDTool`、`FantasyProject-Downloader-Android`、`FantasyTools`、`TFACStorybox`——**没有 PC 发布仓库** |
| GitHub `kirito0000001/CrossingVoid-Downloader-PC` | 存在（源码），**zero releases** |

服务器侧清单也印证：`products/crossingvoid-launcher-pc/update.json` 里 1.4.0 资产同时有 `downloadUrl`（Gitee，死链）和 `objectKey`（`toolboxes/crossingvoid-launcher-pc/…`），接口优先按 `objectKey` 签 OSS。

**用户可见后果**

- `1.4.0` 玩家：接口返回同版本 → `check()` 返回 null → "已是最新"，闸门 ready，能正常玩。
- `< 1.4.0` 玩家：接口返回 1.4.0 → 提示"发现新版本"→ 点更新 → OSS 404 → "更新失败"，而且闸门停在 `updateRequired`，游戏下载被拦住。**这批人现在无路可走**（客户端里的端点改不了）。
- 发布侧：`Invoke-CrossingVoidPcAction.ps1:187-208 Assert-CrossingPublicVersion` 读的正是这个接口 → 端点永远回 1.4.0 → 今天发任何新版本都会在 99% 判"未通过公网版本核验"。

本地还有救的东西：`dist-launcher-update\零境启动器_1.4.0_x64-setup.exe`（44.5 MB）+ `.sig` + `latest.json` + `update.pc.json` 都在，1.4.0 可以原样重传，不用重编。

---

## 3. 对照：零境安卓启动器的 Gitee 链路（目标形态）

### 3.1 发布：`Scripts/Publish-AndroidLauncher.ps1`（AxTools「平台-零境-安卓」→ `Invoke-CrossingVoidAndroidAction.ps1:161-181`）

```text
断点检查（APK 大小+sha256+清单版本一致 → 跳过构建）
→ npm test → npm run build → npx cap sync android
→ gradlew assembleRelease -PlauncherVersionName=<x.y.z> -PlauncherVersionCode=<n>
→ aapt 校验包名 com.TFAC.CorssingVoid / versionCode / versionName
→ apksigner 校验签名指纹 = 56f1b0b3…ba2d92（和游戏同一张证书）
→ 产出 CrossingVoidInstaller-<ver>-Android.apk + launcher/android-installer-latest.json
→ Gitee Release android-installer-v<ver>  @ xiaojie578/CrossingVoid-Downloader-Android:master（附件 ≤100 MiB）
→ 清单提交到仓库 launcher/android-installer-latest.json（master）
→ 同一份清单 scp 到 crossing-server:C:\inetpub\wwwroot\manifests\launcher\android-latest.json（带 ACL 修正）
```

清单是扁平 schema（`Publish-AndroidLauncher.ps1:275-288`）：

```json
{"schemaVersion":1,"productKey":"crossingvoid-launcher-android-installer",
 "versionName":"1.4.10","versionCode":1,"notes":"…","publishedAt":"…",
 "asset":{"fileName":"CrossingVoidInstaller-1.4.10-Android.apk",
          "url":"https://gitee.com/xiaojie578/CrossingVoid-Downloader-Android/releases/download/android-installer-v1.4.10/…apk",
          "sizeBytes":8512191,"sha256":"6c15b138…"}}
```

### 3.2 客户端

- `src/services/launcherUpdate.ts`：读 `https://www.crossingvoid.top/manifests/launcher/android-latest.json?t=<ts>`（带 `Cache-Control: no-cache`），严格校验 `schemaVersion=1` / productKey / 三段版本号 / sizeBytes / 64 位 sha256；`shouldInstallLauncherUpdate` = 版本名更大且 `manifest.versionCode >= 当前`。
- 下载/校验/安装全在原生：`LauncherUpdateService` → `LauncherUpdateDownloader`（断点）→ `LauncherUpdateVerifier` / `DownloadFileUtils.sha256` → `LauncherInstallSupport`（拉起系统安装器）+ 通知栏 + `UpdateStateStore`。
- **URL 是运行时读的**，清单换地址/换仓库不用发新版；这是和 PC 最大的结构差别。

### 3.3 线上实测

| 请求 | 结果 |
|---|---|
| `GET https://www.crossingvoid.top/manifests/launcher/android-latest.json` | `200`，1.4.10 / 8,512,191 B / `6c15b138…` |
| `GET gitee …/raw/master/launcher/android-installer-latest.json` | `200`，与站点镜像一致 |
| `HEAD gitee …/releases/download/android-installer-v1.4.10/….apk` | `200` → 302 到 `foruda.gitee.com/attach_file/…apk?token=…&ts=…`，长度 8,512,191 |

AxTools 侧核验也读同一份站点清单：`Invoke-CrossingVoidAndroidAction.ps1:85-106 Assert-AndroidPublicVersion`（5 次重试，比 versionName）。

---

## 4. 两端逐项对照

| 维度 | TFAC（PC） | 零境安卓 |
|---|---|---|
| 清单来源 | www 上 .NET 接口（现场签名、动态） | www 上静态 JSON（= Gitee 仓库里的同一份） |
| 清单格式 | Tauri `latest.json`（version/notes/pub_date/platforms.signature+url）+ 后端 `update.json` | 扁平 schemaVersion 1（versionName/versionCode/asset{sizeBytes,sha256}） |
| 安装包托管 | 阿里云 OSS 签名链接（**bucket 已删除**） | Gitee Release 附件（≤100 MiB，302 到 foruda CDN） |
| 完整性校验 | minisign 签名（公钥烤进 `tauri.conf.json`） | sha256 + APK 包名/签名指纹 |
| 客户端实现 | Tauri 官方 updater 插件；端点编译期写死 | 自研 TS + 原生；URL 运行时读 |
| 发布仓库 | 旧 Gitee PC 仓库（**已转私有 + 被 Gitee 禁止公开**，当时匿名 403/404）；GitHub 只有源码、无 Release；新仓库 `CrossingVoid-Launcher-PC` 已建 | Gitee Android 仓库（public，在） |
| 版本口径 | SemVer，无 versionCode | 三段 SemVer + versionCode（普通包恒为 1） |
| AxTools 动作 | 构建/校验/上传演练/单独上传/发布演练/完整发布 | 发布演练/完整发布 |
| 断点复用 | `latest.json` + 安装包 + `developer-version.json` 一致 | APK 大小+sha256+清单版本一致 |
| 发布后核验 | 读 www 接口的 `version` | 读 www 静态清单的 `versionName` |
| Gitee 令牌来源 | `FANTASYTOOLS_GITEE_TOKEN / GITEE_TOKEN / GITEE_ACCESS_TOKEN` | 同上 |
| 站点镜像 | 无（只有 notices / channels / traffic-status） | `manifests/launcher/android-latest.json` |

---

## 5. 要让 TFAC 走同一条 Gitee 链路，必须动产的点

### 5.1 仓库与凭据

1. **重建 Gitee 发布仓库**（现 `xiaojie578/CrossingVoid-Downloader-PC` 已 404）。沿用同名可以少改文档/脚本默认值；换名则两个脚本的 `-GiteeRepository` 默认值、指南、测试一起改。
2. 令牌：本机只有 User 级 `FANTASYTOOLS_GITEE_TOKEN`（可用）。AxTools 设置页那个令牌框写的是 `AXTOOLS_GITEE_TOKEN`，两个启动器脚本都**不读**它（只有 GalExcle 读）——要么脚本补一个来源，要么继续靠 `FANTASYTOOLS_` 环境变量。

### 5.2 发布脚本

3. 把 OSS 兼容阶段从 `Publish-LauncherGiteePackage.ps1` 摘掉（现在 `:384-398` 是硬依赖、找不到脚本直接 throw）。过渡期可以先传 `-SkipOss -SkipServer`，但默认值必须换。
4. 新增「静态清单发布」：Gitee 仓库 `launcher/latest.json`（已支持）+ www 镜像 `C:\inetpub\wwwroot\manifests\launcher\pc-latest.json`。scp + ACL 修正的写法现成：`Publish-LauncherDownloadChannels.ps1` / `Publish-LauncherRemoteNotice.ps1` / Android 的 `Publish-WebsiteManifest`。
5. 附件上限：PC 安装包 44.5 MB、`.sig` 430 B，远低于 100 MiB；但**发布前必须断言**，否则以后 Tauri 包涨过 100 MiB 会在上传中途才炸。

### 5.3 清单形态（关键决策，三选一）

| 方案 | 做法 | 代价 |
|---|---|---|
| **A. 保留 Tauri 清单** | 只把端点换成静态 `latest.json`（Gitee raw 或 www 镜像），`url` 指 Gitee Release | 客户端 0 代码；但端点编译期写死，必须发版才生效；仓库不存在/私有/限流时 Gitee raw 会回 403（本次 PC 仓库的 403 就是这么暴露的），走 www 镜像更稳 |
| **B. 完全照 Android** | 新增扁平 `pc-installer-latest.json`（productKey/versionName/asset{size,sha256}），客户端自研下载+sha256+跑 NSIS | 客户端要写下载/校验/安装/断点/进度，工作量大；换来"换仓库不发版" |
| **C. 双清单并存** | 同时产 Tauri `latest.json` 与扁平清单；新客户端读扁平、旧客户端读 Tauri | 发布脚本多产一份；过渡最平滑，长期要清一边 |

注：Tauri 的 `check()` **没有 endpoints 参数**，想运行时换源只能自己包一层 Rust 命令（`tauri_plugin_updater::UpdaterBuilder::endpoints()`），否则 A/C 都得靠发版切端点。

### 5.4 旧客户端（≤1.4.0）怎么上来

6. 客户端端点改不了 + OSS 已删 → **这批人无法自动升级**。可选：官网/网盘/QQ 群一次性手动包；或先恢复一条能返回可用 URL 的 www 接口（需要改 `ToolboxUpdateServer.dll` 源码或反编译，不划算）。
7. 新版本不要再指望旧接口：它现在只会返回 1.4.0 + 死链，OSS 不恢复之前没有价值。方案 C 的"兼容旧客户端"只在**旧客户端还要能读**的意义上成立，实际内容仍然得换成 Gitee 地址。

### 5.5 AxTools 侧

8. `Invoke-CrossingVoidPcAction.ps1:187-208 Assert-CrossingPublicVersion`：改成读新清单（条目/字段按 5.3 选型）。
9. 动作表与脚本参数不用改（`Upload`/`Publish` 语义仍然成立），但 `CrossingVoidPcAdapter` 里"必须存在 `Publish-LauncherGiteePackage.ps1`"这条要保持——文件名不变最省事。
10. `AxTools.Tests` 里与 PC 启动器发布相关的断言（若引用 OSS/接口地址）要一起核。

### 5.6 测试与文档

11. **`tests/launcherPublishing.test.ts:59-64` 会直接挡路**：它断言 `tauri.conf.json` 必须包含 www 接口、且不得出现 `gitee.com`。改链路必须改这条（它现在保护的是旧设计）。
12. `tests/launcherPublishing.test.ts:30-41` 断言"发布 Gitee 之后必须调 OSS 兼容脚本"、"必须写 objectKey"——同在必改清单。
13. 指南要重写：`Docs/PCLauncherDevelopmentGuide.md` §10（"Gitee 客户端不得再读取其 Raw 文件"）、§20.2/§20.3（OSS 兼容）。
14. `Docs/launcher-logic-notes.md`、`Docs/GameDownloadRetrofitPlan.md` 里 OSS 相关段落同步。

### 5.7 顺手要清理的"第二条旧链"

15. 启动器仓库里还有一套**旧的游戏包发布链**：`Scripts/Publish-GamePackage.ps1` → `Publish-GamePackageCore.ps1`（GitHub + OSS 上传、OSS 清单签名）+ `Publish-GameMetadataGitee.ps1`。启动器开发页仍能触发（`lib.rs:3259` `game-windows`/`game-android`）。AxTools 这边的 `Publish-GameDownloadRelease.ps1` 才是现行 v1/下载站链路。OSS 没了以后这条旧链也是死的，建议一起标退役。
16. 客户端里还留着 v2/OSS 直读代码：`App.vue:585 officialUpdateApiUrl`、`:2681 resolveBackendDownloadUrl`（`/sign-download` + objectKey + chunks）。今日线上零境/幻杀都走下载站 v1（`dl.crossingvoid.top/games/…/latest.json` 200），这条只对"还没接下载站的档位"（例如火影）有意义，需要确认后决定删/留。

---

## 6. 顺带发现（与本次链路无关，但会干扰验证）

- `https://www.crossingvoid.top/traffic-status.json` 现在返回 `totalBytes=644245094400`（600 GiB，香港下载站口径）、**`remainingBytes=0`、`isLow=true`、`throttled=true`**。如果这是真值，启动器会一直挂"流量不足"提示、并可能拦下载；在发 PC 新版之前值得先确认这是统计口径问题还是真的跑满了。
- `https://dl.crossingvoid.top/games/crossingvoid/latest.json`、`…/crossingvoid-android/latest.json` 均 `200`（0.6.0，含 `fragmentsUrl`）；`…/fantasyproject/latest.json` 仍 `404`（幻杀还没发到下载站，与文档一致）。
- `crossing-server` 上 `C:\UEWatchdog` 一切正常（`watchdog.config.psd1`、`Remote-AliyunServerGit.ps1` 10-19 16:44、`backups/` 10-19 19:48、`maintenance.txt` 3 B 空），服务端更新链路不受本次影响。

---

## 7. 已拍板的四件事（2026-10-19 晚）

1. **Gitee 仓库**：原计划重建同名 → 发现旧仓库只是转私有、且被 Gitee 判「RAW 外链滥用」禁止公开；**改为新建公开仓库 `xiaojie578/CrossingVoid-Launcher-PC`**，脚本默认值已切过去。
2. **清单放哪**：Gitee + www 镜像（`manifests/launcher/pc-latest.json`、`pc-installer-latest.json`），客户端只读 www。
3. **清单形态**：C（双清单）——Tauri `latest.json` 供兜底通道，扁平 `pc-installer-latest.json` 供首选通道，客户端优先读扁平。
4. **≤1.4.0 老客户端**：暂不处理（自动升级做不到：端点烤死 + OSS 已删），只保证新链路可用；1.4.0 的安装包已重新发到新仓库，可作为手动分发源。

---

## 8. 落地记录（2026-10-19 晚，已改代码并实测）

### 8.1 改了什么

| 层 | 文件 | 改动 |
|---|---|---|
| 仓库 | Gitee `xiaojie578/CrossingVoid-Launcher-PC` | 新建公开仓库 + README；旧仓库保持私有存档 |
| 构建 | `Scripts/Build-LauncherUpdaterPackage.ps1` | 默认仓库切到新仓库；额外产出扁平清单 `pc-installer-latest.json`，并拷到发布目录 |
| 构建 | `Scripts/LauncherManifestUtilities.ps1` | 新增 `New-LauncherInstallerManifest`（扁平清单构造 + RFC 3339 归一） |
| 发布 | `Scripts/Publish-LauncherGiteePackage.ps1` | 双清单写 Gitee；新增站点镜像 `Publish-WebsiteMirrorFile`；OSS 兼容改 `-WithOssCompat` 登录式；复用旧断点时自动改写清单里的下载地址；令牌来源加 `AXTOOLS_GITEE_TOKEN` |
| 客户端 | `src-tauri/tauri.conf.json` | updater 端点 → `https://www.crossingvoid.top/manifests/launcher/pc-latest.json` |
| 客户端 | `src/launcherUpdateManifest.ts`（新增） | 扁平清单解析/校验/版本比较/拉取 |
| 客户端 | `src/App.vue` | 更新检查改「扁平清单优先、Tauri 兜底」；安装分支化（Rust 下载+校验+拉起安装） |
| 客户端 | `src-tauri/src/lib.rs` | 新增 `download_launcher_installer`（temp 下载 + sha256 + 进度事件）、`install_launcher_installer`（`/P /R /UPDATE /ARGS` 后退出） |
| AxTools | `Scripts/Adapters/CrossingVoidPc/Invoke-CrossingVoidPcAction.ps1` | 发布后核验改读站点扁平清单（回退读 pc-latest.json），不再读 OSS 签名接口 |
| AxTools | `Scripts/Tests/Test-CrossingVoidPcAdapter.ps1` | 换成新链路合同（禁止回旧接口、要求双清单+镜像） |
| 测试 | `tests/launcherPublishing.test.ts`、`tests/launcherUpdateManifest.test.ts`（新增） | 端点/双清单/镜像/版本比较/坏清单断言 |
| 文档 | `Docs/PCLauncherDevelopmentGuide.md` §10/§20 | 重写为双清单 + Gitee 资产 + 站点镜像 |

### 8.2 实测结果

| 项 | 结果 |
|---|---|
| `cargo check` | 通过（仅既有 warning） |
| `npm test` | 269 passed（38 files） |
| `npm run build` | 通过（vue-tsc + vite） |
| 真实发布 1.4.0 | `launcher-v1.4.0` Release 建成，exe 44,477,733 B + `.sig` 上传成功，双清单提交 Gitee，镜像写入 `C:\inetpub\wwwroot\manifests\launcher\` |
| 匿名核验 | www 两份清单 200；Gitee raw 200；安装包匿名 HEAD 200（302 到 foruda CDN，长度一致）；www 镜像与本地清单 sha256 完全一致 |

### 8.3 还没做 / 要注意

- 新客户端要**真的发一个 ≥1.5.0 版本**才会用上新链路；1.4.0 之后必须先在启动器开发页把版本提到 1.5.0 再点「完整发布」。
- `AxTools` 侧 `Scripts/Tests/Test-CrossingVoidPcAdapter.ps1` 的后半段（RunDevelopment 参数合同）是**既有的过期断言**（现在要求 `node_modules\.bin\tauri.cmd`、跑 `npm run tauri -- dev`），与本次改动无关，本次未动。
- 客户端里还留着旧的 v2/OSS 游戏下载代码（`App.vue:598 officialUpdateApiUrl`、`resolveBackendDownloadUrl`），只对没接下载站的档位有意义，待单独清理。
- `Publish-LauncherUpdaterPackage.ps1`（OSS 兼容）仍保留在仓库里，登录式调用；服务器上那两份 `update.json` 里的 `objectKey` 已无人消费。

---

## 附：证据索引（可复核）

| 项 | 位置 |
|---|---|
| AxTools 动作表 | `AxTools.Core/Adapters/CrossingVoidPcAdapter.cs:26-31` |
| AxTools 发布/上传/核验 | `Scripts/Adapters/CrossingVoidPc/Invoke-CrossingVoidPcAction.ps1:169-247` |
| 构建脚本 | `CrossingVoidinitiator-PC/Scripts/Build-LauncherUpdaterPackage.ps1:283-459` |
| Gitee 发布 + OSS 硬依赖 | `…/Scripts/Publish-LauncherGiteePackage.ps1:365-402` |
| OSS/服务器覆盖 | `…/Scripts/Publish-LauncherUpdaterPackage.ps1:1-23, 491-638` |
| 客户端更新流程 | `…/src/App.vue:4614-4763`；端点 `src-tauri/tauri.conf.json:57-66` |
| 安卓发布脚本 | `CrossingVoidinitiator-Android/Scripts/Publish-AndroidLauncher.ps1:185-315` |
| 安卓客户端清单 | `…/src/services/launcherUpdate.ts:1-105` |
| 安卓原生下载/校验/安装 | `…/android/.../LauncherUpdateService.java`、`LauncherUpdateVerifier.java`、`LauncherInstallSupport.java` |
| 服务器清单实例 | `crossing-server:C:\Users\Administrator\Desktop\OSSAPI\ToolboxUpdateServer\app\Data\products\crossingvoid-launcher-pc\update.json` |
| 服务器配置 | 同目录 `appsettings.json`（`Oss.Bucket=download-server-xj`、`TrafficQuota.Provider=Edge`） |
| 阻挡性测试 | `CrossingVoidinitiator-PC/tests/launcherPublishing.test.ts:30-41, 59-64` |
