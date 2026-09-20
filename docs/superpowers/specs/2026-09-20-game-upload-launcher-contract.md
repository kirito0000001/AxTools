# 游戏包分发 · 启动器对接契约

> **给谁看**：PC 启动器（`D:\UnrealMap\CrossingVoidinitiator-PC`）、安卓启动器
> （`D:\UnrealMap\CrossingVoidinitiator-Android`）、以后可能接的游戏内下载器。
> **状态**：分发侧（AxTools / 野草云）**已按本文实现并实测通过**（PC + 安卓，2026-09-20）；
> 启动器按本文改造（下一期主线）。
> **日期**：2026-09-20。上游设计见 `2026-09-20-game-upload-design.md`。

## 零、现在就能实测的地址（2026-09-20 已上线）

野草云上已经放了真实的 0.5.13 全量产物，随时可以拿它联调（不需要等启动器改造）：

```bash
# 文件级清单（v1，含 baseUrl）—— PC 86 条 / 安卓 3 条
curl -s https://dl.crossingvoid.top/games/crossingvoid/manifests/0.5.13.json | head -c 300
curl -s https://dl.crossingvoid.top/games/crossingvoid-android/manifests/0.5.13.json

# 大文件抽查（916 MB 的基础包容器）
curl -I https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas
#   → HTTP/1.1 200 OK，Content-Length: 961189680

# 安卓两个文件
curl -I https://dl.crossingvoid.top/games/crossingvoid-android/0.5.13/main.1.com.TFAC.CorssingVoid.obb
#   → 200 OK，Content-Length: 2141746524

# 断点续传抽查
curl -s -o /dev/null -D - -r 0-1023 https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas
#   → HTTP/1.1 206 Partial Content，Content-Range: bytes 0-1023/961189680
```

清单里的 `baseUrl` 就是 `https://dl.crossingvoid.top/games/crossingvoid/0.5.13/`，
`files[].path` 是**相对这个目录**的路径（例如 `CrossingVoid/Content/Paks/pakchunk0-Windows.ucas`）。

> 2026-09-20 用发布内核重新推过一遍并做过公网校验：PC 清单 86 条 + 抽样 5 个文件字节数一致；
> 安卓清单 3 条 + 抽样 2 个文件一致。

## 一句话

**清单还是从 `www.crossingvoid.top` 读（URL 不变），只是清单内容从「一个 zip + 切片」改成
「逐文件 + 哈希」，文件从 `dl.crossingvoid.top` 下。**

## 一、不变的东西（先看这条，少改一半）

| 不变 | 值 |
| --- | --- |
| 清单 URL | `https://www.crossingvoid.top/manifests/game/windows-latest.json`（PC）<br>`https://www.crossingvoid.top/manifests/game/android-latest.json`（安卓） |
| `productKey` 校验 | PC：`crossingvoid-game`（测试通道 `crossingvoid-game-test`）<br>安卓：`crossingvoid-android-game` |
| 启动器自己的更新 API | `https://www.crossingvoid.top/api/toolbox-updates`（不动） |
| 公告 / 官网 | `launcher-notice.json`、`https://www.crossingvoid.top/`（不动） |

## 二、要改的东西（一句话版）

1. **认新清单**：`schemaVersion` 从 `2` 改成 `1`（文件级）。原来 v2 的
   `latest.assets[].chunks[]`（zip 切片 + `objectKey` 指向 OSS）**不再产生**。
2. **换下载源**：文件从 `baseUrl`（=`https://dl.crossingvoid.top/games/<game>/<版本>/`）拼 `path` 下载，
   不再从 OSS / GitHub Release 取。
3. **按文件增量**：本地已经有、且 `sha256` 相同的文件**不重复下载**（这是这次改造的主要收益）。
4. **并发上限 ≤ 6**：下载站 `limit_conn perip 6`，第 7 个连接会被 **503**。建议用 4 并发。
5. **要删掉本地多余文件**：清单里没有、但本地已经装了的文件（旧 pak、上一版删掉的文件、旧的
   `_P` 等）**必须删掉**，否则旧容器会一直留着、越积越多，甚至被引擎当有效 pak 加载。
6. **判定"要不要下"只看 sha256，不要只看大小或时间**：实测出现过"**容器大小完全一样、内容却全变了**"
   的情况（见上游设计的「待查疑点」），只比大小会漏更新。

## 三、清单字段（逐条）

分发侧产出的文件（沿用已经在代码里存在、但从未被使用的 v1 形态）：

```json
{
  "schemaVersion": 1,
  "productKey": "crossingvoid-game",
  "runtime": "Windows",
  "version": "0.5.13",
  "channel": "stable",
  "baseUrl": "https://dl.crossingvoid.top/games/crossingvoid/0.5.13/",
  "generatedAt": "2026-09-20T13:20:00+08:00",
  "files": [
    { "path": "CrossingVoid/Content/Paks/pakchunk0-Windows.ucas",
      "sizeBytes": 961218560,
      "sha256": "……" },
    { "path": "CrossingVoid/Binaries/Win64/CrossingVoid-Win64-Shipping.exe",
      "sizeBytes": 150450176,
      "sha256": "……" }
  ],
  "patches": []
}
```

| 字段 | 含义 | 启动器要做什么 |
| --- | --- | --- |
| `schemaVersion` | 固定 `1` | 只接受 `1`（`2` 是旧形态，遇到就当"不支持"报错） |
| `productKey` | 产品标识 | 和现在一样校验，不符就报错 |
| `runtime` | `Windows` / `Android` | 与当前端一致才用 |
| `version` | 玩家看到的版本号 | 用于显示与"要不要更新"的判断 |
| `channel` | `stable` / `test` | 与启动器设置的通道比对 |
| `baseUrl` | **新增**：文件下载基址（以 `/` 结尾） | 下载 URL = `baseUrl + path` |
| `generatedAt` | 生成时间（可选，用于排查） | 显示/记录即可 |
| `files[]` | 每个要落地的文件 | 逐个下载、校验、落位 |
| `files[].path` | **相对路径**（用 `/` 分隔，与打包产物一致） | 拼接时不要重复斜杠；Android 侧注意落位根目录不同 |
| `files[].sizeBytes` | 字节数 | 断点续传与进度用 |
| `files[].sha256` | 小写十六进制 | **下完必须校验**；不符就重下（最多 2 次）再报错 |
| `patches[]` | **预留**（本期永远为空数组） | 现在**必须忽略**（存在但为空时不要报错）；将来非空时表示可选的增量补丁包 |

清单里还会带一个 `CrossingVoid.version.json`（内容 `{"version":"0.5.13","platform":"Windows","channel":"stable"}`），
按普通文件落位即可（这是旧发布流程留下的约定文件，用来给外部工具读版本，别把它当垃圾删了）。
分发侧生成它时**不写行尾换行**（77 字节）。启动器只要按清单里的 `sha256` 校验即可，
不要自己拼这个文件的内容。

真实样例（从线上清单里截的前两条）：

```json
"files": [
  { "path": "CrossingVoid.exe",
    "sizeBytes": 444928,
    "sha256": "……" },
  { "path": "CrossingVoid/Binaries/Win64/CrossingVoid-Win64-Shipping.exe",
    "sizeBytes": 160886272,
    "sha256": "……" }
]
```

### 和现役 v2 的差异（供改造时对照）

| | v2（现役） | v1（本次切换后） |
| --- | --- | --- |
| 粒度 | 整体 zip，按 500 MB 切成 `chunks[]` | 逐文件 `files[]` |
| 下载源 | `assets[].objectKey`（阿里云 OSS 对象键） | `baseUrl` + `files[].path`（野草云） |
| 增量 | 改一处 → 后面所有片都重下 | 只下 sha256 变化的文件 |
| 校验 | zip 整包 sha256 + 每片 sha256 | 每个文件自己的 sha256 |
| 解压 | 下完要解压 | **不需要解压**（产物本身就是目录形态） |

## 四、下载与落位规则

1. **URL 拼接**：`baseUrl + path`。示例：
   `https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas`
2. **断点续传**：服务端支持 `Range`（实测 `Accept-Ranges: bytes`）。用 `Range: bytes=<已下>-` 续传，
   但对**最终校验失败**的文件必须整文件重下（不要拿残留拼接）。
3. **半包防护**：一律先写 `<目标名>.part`，`sha256` 校验通过后再**改名到位**；绝不直接写目标名。
4. **并发**：≤ 6（建议 4）。每个连接限速 8 MB/s（前 64 MB 不限速），所以并发是必要的。
5. **落位根目录**：
   - PC：游戏安装目录（现有逻辑所在的那一层，保持不动）
   - 安卓：**待核对**（APK 内 / 外部存储）。核对结论要回填进本文档
6. **本地清单**：落位后把"文件 → sha256"记在本地（现有 `downloadPlan` 的思路可复用），
   下次比对用它决定"下不下"。

### 下载站实测参数（2026-09-20）

| 项 | 实测值 | 对启动器的要求 |
| --- | --- | --- |
| 范围请求 | `Accept-Ranges: bytes`，`-r 0-1023` → `206` | 断点续传可用 |
| 限速 | 每连接前 64 MB 不限速，之后 8 MB/s | 大文件要开多连接（≤6）才快 |
| 并发 | `limit_conn perip 6`，第 7 条连接 **503** | 并发 ≤ 6，建议 4 |
| 清单 Content-Type | `application/json`（按扩展名识别） | 直接 `JSON.parse` 即可 |
| 清单缓存头 | dl 上**目前没有** `Cache-Control`（www 那条待办） | 若从 dl 直接拉清单，请加 `?t=<时间戳>` 之类的破缓存参数 |

## 五、版本与保留

（安卓专属约定见下一节。）

- 服务器上每个版本一个目录：`/srv/downloads/games/<game>/<版本>/…`。
  **只保留「最新的一版 + 上一版」各一个**（更早的由分发侧删；上一版放在 `games/<game>-archive/<版本>/`，
  靠硬链接实现，所以启动器不用关心它和 `games/` 的关系）。
- 已被删掉的旧版本，如果玩家还在用，会得到 `404` —— 启动器遇到 `404` 的处理建议：
  提示"该版本已下线，请更新到最新版"，而不是无限重试。

> 更新时**不要把文件条数或文件名写死**：0.5.13 的 PC 清单是 86 条，下一版会随内容变化而不同
> （安卓那边除了 `main.<版本号>.<包名>.obb` 里的版本号，命名是稳定的）。

## 六、安卓（APK + OBB）的额外约定（2026-09-20 实测）

安卓产物形态和 PC 不同：**两个文件平铺在版本目录里**（没有子目录），
清单里的 `files[].path` 就是文件名本身：

```json
{
  "productKey": "crossingvoid-android-game",
  "runtime": "Android",
  "baseUrl": "https://dl.crossingvoid.top/games/crossingvoid-android/0.5.13/",
  "files": [
    { "path": "CrossingVoid-Android-Shipping-arm64.apk", "sizeBytes": 73185276,  "sha256": "……" },
    { "path": "main.1.com.TFAC.CorssingVoid.obb",        "sizeBytes": 2141746524, "sha256": "……" },
    { "path": "CrossingVoid.version.json",               "sizeBytes": 79,        "sha256": "……" }
  ]
}
```

| 文件 | 大小（0.5.13） | 说明 |
| --- | --- | --- |
| `CrossingVoid-Android-Shipping-arm64.apk` | 69.8 MB | 主 APK；包名 `com.TFAC.CorssingVoid`（**拼写就是这样**，别"纠正"它） |
| `main.1.com.TFAC.CorssingVoid.obb` | 2042.5 MB | OBB 数据包；文件名里的 `1` 是版本号 |
| `CrossingVoid.version.json` | 79 B | 版本标记（同 PC） |

**打包目录里存在、但分发时必须排除的东西**（这些是开发工具与符号，旧发布脚本也是这么过滤的）：

- `CrossingVoid_Symbols_v1/`（符号文件，2.2 GB）
- `AFS_CrossingVoid-Android-Shipping-arm64.apk`（AndroidFileServer 调试工具，4.4 MB）
- `Install_*.bat` / `Uninstall_*.bat` / `SymbolizeCrashDump_*.bat`
- `win-x64/UnrealAndroidFileTool.exe`（AFS 推送工具，34 MB）

**启动器落地要求**：

1. APK 走系统安装流程（覆盖安装；签名必须一致，否则装不上）
2. OBB 必须放到 `Android/obb/<包名>/main.<版本号>.<包名>.obb`（文件名原样），否则游戏起不来
3. 更新顺序：先下完 APK/OBB → 校验 sha256 → **再安装/替换**。不要边下边装（安卓上替换 APK 会杀掉进程）
4. 旧版本遗留：`Android/obb/<包名>/` 里其他 `main.*.obb` 要清掉，避免旧数据被复用

## 七、两端各自的改造清单

**PC 启动器**（`src/App.vue` 等）

- [ ] `gameMetadataManifestUrl` 保持不变（www），但解析改成 v1 结构
- [ ] 删掉/停用 v2 的 `chunks[]` 下载路径（不再依赖 OSS `objectKey`）
- [ ] 新增：按 `baseUrl + path` 逐文件下载 + sha256 校验 + `.part` → 改名
- [ ] 新增：本地文件哈希表，用于跳过未变文件
- [ ] 新增：删掉"清单里没有"的本地旧文件
- [ ] 并发上限 ≤ 6（建议 4）
- [ ] `patches[]` 为空时忽略，不报错

**安卓启动器**（`src/services/gameUpdate.ts`、`src/services/downloadPlan.ts`）

- [ ] 同上（`schemaVersion` 校验从 `2` 放宽/改为 `1`）
- [ ] 核对并写清：pak 落位到哪个可写目录、UE 侧怎么挂载
- [ ] Capacitor 侧注意后台/断网的续传与恢复

## 八、怎么测（不碰线上也能测）

1. **本地假下载站**：把一个小目录用 `python3 -m http.server` 起在本地，
   手改一份 v1 清单把 `baseUrl` 指向 `http://127.0.0.1:8000/`，用它跑通"逐文件下载 + 校验 + 落位"。
2. **真实数据（现在就能用）**：野草云上已经有 0.5.13 的真产物（PC 2.71 GB / 安卓 2.21 GB，
   含 v1 清单），把清单文件手动放到启动器能读的位置就能端到端验证，不用等新版本。
3. **验收点**：正常更新一次；改一个文件后再次更新（应当**只下那一个文件**）；拔网线中途恢复（应当续传成功）。

> 第 3 条里的"只下那一个文件"要在**分发侧真的发了一版新版本**之后才有样本。
> 现在最接近的实测是：PC 重新打包过的那次，分发侧是按文件 sha256 比出了"变了的容器才传"。

## 九、明确不做（本期）

- 不做 patch pak（`patches[]` 只留位，永远为空）
- 不改清单 URL、不改 `productKey`、不改启动器自身的更新 API
- 不改游戏内分片下载器（`BuildManifest` 那条线是另一件事）

## 十、分发侧现状（2026-09-20 P3 收工后，启动器不用管但需要知道）

### 三个游戏 × 两个平台（2026-09-20 起）

清单结构、字段含义、下载/校验规则**三个游戏完全一样**，只有下面这些标识不同：

| 游戏 | 平台 | dl 目录 / `baseUrl` | 清单 | `productKey` | 裸仓库 | 版本标记文件 |
| --- | --- | --- | --- | --- | --- | --- |
| 零境交错 | PC | `games/crossingvoid/<版本>/` | `games/crossingvoid/manifests/<版本>.json` | `crossingvoid-game` | `game-crossingvoid.git` | `CrossingVoid.version.json` |
| 零境交错 | 安卓 | `games/crossingvoid-android/<版本>/` | 同结构 | `crossingvoid-android-game` | `game-crossingvoid-android.git` | `CrossingVoid.version.json` |
| 幻杀 | PC | `games/fantasyproject/<版本>/` | 同结构 | `fantasyproject-game` | `game-fantasyproject.git` | `FantasyProject.version.json` |
| 幻杀 | 安卓 | `games/fantasyproject-android/<版本>/` | 同结构 | `fantasyproject-android-game` | `game-fantasyproject-android.git` | `FantasyProject.version.json` |
| 火影 | PC | `games/narutobp/<版本>/` | 同结构 | `narutobp-game` | `game-narutobp.git` | `NarutoBP.version.json` |
| 火影 | 安卓 | `games/narutobp-android/<版本>/` | 同结构 | `narutobp-android-game` | `game-narutobp-android.git` | `NarutoBP.version.json` |

清单域名前缀都是 `https://dl.crossingvoid.top/`。**目前真正上线了的**：

- 零境 PC / 安卓：`0.5.13`
- 幻杀 安卓：`0.1.0`（2026-09-20 第一次跑通，106 MB）
- 幻杀 PC、火影两平台：还没发过（清单不存在 → 启动器拿不到就按"暂无版本"处理）

> 启动器侧只需要按 `productKey` 判断"这份清单是不是我的"。三个游戏的 `productKey` **不同**，
> 别用同一个 Key 去套（零境启动器读 `crossingvoid-game` / `crossingvoid-android-game`，
> 幻杀/火影各自的启动器要读自己那两个）。

这一节是"野草云上现在到底长什么样"，排查问题时用得上；启动器**不需要**读这些。

### 目录布局（docroot = `/srv/downloads`）

```
games/crossingvoid/0.5.13/<文件…>                    ← PC 最新版（对外下载）
games/crossingvoid/manifests/0.5.13.json             ← PC 最新版清单
games/crossingvoid-archive/0.5.13/<文件…>            ← 上一版归档（硬链接，不额外占空间）
games/crossingvoid-archive/0.5.13.manifest.json      ← 上一版的清单副本
games/crossingvoid-android/…                         ← 安卓同结构
```

- **树里永远只有"最新那一版"**：发 0.5.14 时，`games/crossingvoid/0.5.13/` 会消失
  （内容变成 `-archive/0.5.13/`）。所以玩家如果还停在被删掉的版本，会拿到 `404`。
- `*-archive/` 是**分发侧自己的备份**（用 `cp -al` 硬链接做的），不是给启动器读的；
  它随时可能被下一次发布覆盖。
- 清单是**每次发布时生成**的，`generatedAt` 在内容没变时会沿用上一次的值
  （这样"同内容重发"连提交都不用做，推送 0 字节）。

### PC 与安卓的分发口径（实测）

| 项 | PC | 安卓 |
| --- | --- | --- |
| dl 目录名 | `crossingvoid` | `crossingvoid-android` |
| 清单条数 | 86（85 个产物 + `CrossingVoid.version.json`） | 3（APK + OBB + `CrossingVoid.version.json`） |
| 清单总体积 | 2.71 GB | 2.21 GB |
| 传上去时排除掉的东西 | `*.pdb`、`Manifest_DebugFiles_Win64.txt`、`Saved/Logs`、`Saved/Crashes`、`.git`、`_download` | `CrossingVoid_Symbols_v1/`、`AFS_*.apk`、`Install_*/Uninstall_*/SymbolizeCrashDump_*.bat`、`win-x64/UnrealEditorFileTool…`（AFS 工具） |

> 注意：`Manifest_DebugFiles_Win64.txt` 以前是跟着上传的（P2 那次 87 条），P3 按旧打包脚本的规则
> 把它排除了，现在是 86 条。这个文件只是 pdb 清单，客户端运行不需要它。

### 服务端仓库与回收（排查"体积怎么又涨了"时看这里）

- 裸仓库：`/srv/repos/game-<产品段>.git`，工作树是 `/srv/downloads`，push 地址
  `yecaoyun-upload:/srv/repos/game-<产品段>.git`（账号 `uploader`，密钥 `~/.ssh/yecaoyun_uploader`）。
- 每次推送的 `post-receive` 会：把上一版硬链接归档 → `checkout -f main` 落位 →
  `update-index --refresh`（刷新 stat 缓存，否则下次 checkout 会把硬链接全部写断）→
  过期 reflog → `repack -a -d` + `prune`。
- 仓库里**永远只有一条提交**（分发侧用 `commit-tree` 写没有父提交的提交）。
  任何人如果看到历史变长，那就是有人绕过内核、手工 `git commit`/`--amend` 了。
- 两个仓库都设了 `core.trustctime=false`：硬链接会改 ctime，不改这个设置 git 会误判"文件全被改过"。
- 实测（2026-09-20）：`/srv/repos` 4.5 GB、`/srv/downloads/games` 4.7 GB、磁盘 11 GB 已用 / 18 GB 可用。
