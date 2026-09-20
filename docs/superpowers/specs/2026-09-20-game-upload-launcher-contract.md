# 游戏包分发 · 启动器对接契约（详细版）

> **给谁看**：PC 启动器（`D:\UnrealMap\CrossingVoidinitiator-PC`）、安卓启动器
> （`D:\UnrealMap\CrossingVoidinitiator-Android`）、以及以后任何要下游戏包的客户端。
> **状态**：分发侧（AxTools / 野草云）**已实现并实测**；**启动器侧还没改**（本文档就是要改的东西）。
> **日期**：2026-09-20。上游设计：`2026-09-20-game-upload-design.md`；进度：`2026-09-20-game-upload-plan.md`。
>
> **怎么用这份文档**：第二章看清单长什么样 → 第四章照抄下载算法 → 第五章按平台落位 →
> 第七章对着你现在的代码逐文件改 → 第八章用现成地址测。

---

## 零、这次到底改什么

| 项 | 现在（旧） | 改成（新） |
| --- | --- | --- |
| 清单结构 | `schemaVersion: 2`，`latest.assets[].chunks[]`（整体 zip + 500 MB 字节切片） | **`schemaVersion: 1`**，`files[]`（逐文件 + sha256） |
| 下载源 | 切片 → 阿里云 OSS 的 `objectKey`（或 GitHub Release 附件） | **`baseUrl` + `files[].path`**（野草云 `dl.crossingvoid.top`） |
| 增量粒度 | 改一个字节 → 后面所有切片重下 | **只下 sha256 变了的文件**（其余本地已有就跳过） |
| 本地清理 | 没有"删旧文件"这一步 | **清单里没有的本地文件要删掉** |
| 校验 | zip 整包 sha256 + 每片 sha256 | 每个文件自己的 sha256 |
| 解压 | 下完要合并切片再解压 | **不用解压**（产物本身就是目录形态） |
| 清单地址 | `https://www.crossingvoid.top/manifests/game/{windows,android}-latest.json` | 名称先不变；按方案 A 后续改成"清单走 Gitee API 最新 Release"（见 2.4） |
| `productKey` | `crossingvoid-game` / `crossingvoid-android-game` | **不变**，但注意现在有三个游戏，各自一套（附录 C） |

**一句话**：清单还是从原来的地方读（URL 不变），但**内容从"一个 zip + 切片"变成"逐文件 + 哈希"，
文件从野草云下、本地已有的不重下、清单里没有的本地文件要删**。

---

## 一、现在就能实测的地址（六个产品）

清单域名前缀统一 `https://dl.crossingvoid.top/`。截至 2026-09-20 **真正上线了的**只有这些：

| 游戏 | 平台 | 清单地址 | 今天有哪一版 | 清单条数 | 体积 |
| --- | --- | --- | --- | --- | --- |
| 零境交错 | PC | `/games/crossingvoid/manifests/<版本>.json` | `0.5.13` | 86 | 2.71 GB |
| 零境交错 | 安卓 | `/games/crossingvoid-android/manifests/<版本>.json` | `0.5.13` | 3 | 2.21 GB |
| 幻杀 | 安卓 | `/games/fantasyproject-android/manifests/<版本>.json` | `0.1.0` | 3 | 106 MB |
| 幻杀 | PC | `/games/fantasyproject/manifests/<版本>.json` | 还没发过 | — | 2.5 GB（产物已就位） |
| 火影 | PC / 安卓 | `/games/narutobp{,-android}/manifests/<版本>.json` | 还没发过 | — | 等打包 |

> ⚠️ **没有"latest"指针**。每份清单是**按版本号**放的（`manifests/0.5.13.json`），
> 所以启动器今天还得靠 www 的 `-latest.json` 才知道"该下哪个版本"。
> 方案 A 会把这件事挪到 Gitee API（见 2.4）。

### 拿一段最小 curl 自测（不用等启动器）

```bash
# ① 清单（v1）
curl -s https://dl.crossingvoid.top/games/crossingvoid/manifests/0.5.13.json | head -c 400

# ② 大文件能下、能续传
curl -I  https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas
#   → 200 OK，Content-Length: 961189680
curl -s -o /dev/null -D - -r 0-1023 https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas
#   → 206 Partial Content，Content-Range: bytes 0-1023/961189680

# ③ 幻杀安卓（小，106 MB，适合拿来做第一次联调）
curl -s https://dl.crossingvoid.top/games/fantasyproject-android/manifests/0.1.0.json
```

---

## 二、清单（v1）逐字段

### 2.1 PC 完整样例（零境 0.5.13，截取）

```json
{
  "schemaVersion": 1,
  "productKey": "crossingvoid-game",
  "runtime": "Windows",
  "version": "0.5.13",
  "channel": "stable",
  "baseUrl": "https://dl.crossingvoid.top/games/crossingvoid/0.5.13/",
  "generatedAt": "2026-09-20T03:03:47.0000000+08:00",
  "files": [
    { "path": "CrossingVoid.exe", "sizeBytes": 444928,
      "sha256": "8fd9233aa2cd2b304ce5fe94c4d7e4ad16787b50d8a62b108320fadb3d825ac1" },
    { "path": "CrossingVoid/Binaries/Win64/CrossingVoid-Win64-Shipping.exe", "sizeBytes": 160886272,
      "sha256": "……" },
    { "path": "CrossingVoid.version.json", "sizeBytes": 77, "sha256": "……" }
  ],
  "patches": []
}
```

### 2.2 安卓完整样例（幻杀 0.1.1，全量 6 条 —— **安卓已经是"chunk 化"形态，见附录 D**）

```json
{
  "schemaVersion": 1,
  "productKey": "fantasyproject-android-game",
  "runtime": "Android",
  "version": "0.1.1",
  "channel": "stable",
  "baseUrl": "https://dl.crossingvoid.top/games/fantasyproject-android/0.1.1/",
  "generatedAt": "2026-09-20T13:51:25.0000000+08:00",
  "files": [
    { "path": "FantasyProject-Android-Shipping-arm64.apk", "sizeBytes": 67367761, "sha256": "……" },
    { "path": "FantasyProject/Content/Paks/pakchunk0-Android_ASTC.pak", "sizeBytes": 43551485, "sha256": "……" },
    { "path": "Engine/Content/Renderer/TessellationTable.bin", "sizeBytes": 259568, "sha256": "……" },
    { "path": "Engine/Config/StagedBuild_FantasyProject.ini", "sizeBytes": 3, "sha256": "……" },
    { "path": "FantasyProject.version.json", "sizeBytes": 76, "sha256": "……" },
    { "path": "FantasyProject.obb.json",     "sizeBytes": 314, "sha256": "……" }
  ],
  "patches": []
}
```

> **注意**：安卓清单里**没有 `main.<n>.<包名>.obb`** —— 发的是 OBB 里的每个条目，
> 外加一个 `<工程名>.obb.json` 旁车。为什么要这样、启动器怎么用，见**附录 D**。
> （`0.1.0` 那种"apk + 一个 2 GB obb"的旧形态只有 2026-09-20 之前的版本才有。）

### 2.3 字段表（启动器逐个处理）

| 字段 | 类型 | 含义 | 启动器要做什么 |
| --- | --- | --- | --- |
| `schemaVersion` | int | 固定 `1` | 只接受 `1`；遇到 `2` 说明是旧清单，报"清单格式不受支持"（不要静默当空清单） |
| `productKey` | string | 产品标识（附录 C 六个值） | 与当前端/当前游戏比对，不符就报错并停止 |
| `runtime` | `Windows` / `Android` | 运行平台 | 与当前端一致才用 |
| `version` | string | 玩家看到的版本号（`0.5.13`） | 显示 + 判断要不要更新 |
| `channel` | `stable` / `test` | 通道 | 与启动器设置的通道比对 |
| `baseUrl` | string（**以 `/` 结尾**） | 文件下载基址 | 下载 URL = `baseUrl + path`，**不要**自己拼 `/` |
| `generatedAt` | ISO 时间 | 生成时间 | 只有排查用，可以只记日志 |
| `files[]` | 数组 | 要落地的每个文件 | 逐个下载/校验/落位 |
| `files[].path` | string | **相对路径**（正斜杠，和打包产物一致） | 拼 URL、落位时按平台规则拼接 |
| `files[].sizeBytes` | long | 字节数 | 断点续传的 `Range` 终点 + 进度条总量 |
| `files[].sha256` | 小写 hex（64 位） | 文件指纹 | **下完必须校验**；不符就重下（最多 2 次）再报错 |
| `patches[]` | 数组 | **预留**，本期永远为空 | 现在必须忽略（空数组不许报错） |

### 2.4 清单从哪拉（现在 / 以后）

- **现在（本期可以用）**：还是 www 的两份 latest： 
  `https://www.crossingvoid.top/manifests/game/windows-latest.json`、
  `.../android-latest.json`。但这两份**今天还是 v2 格式**（老流程写的），
  在没有把它们换成 v1 之前，启动器得**同时认两种**：
  `schemaVersion === 1` 走新流程，`schemaVersion === 2` 走老流程（过渡期）。
  过渡期结束（分发侧把 www 指针切到 v1）后就可以删掉 v2 分支。
- **以后（方案 A，启动器二期）**：清单改为**从 Gitee API 拿最新 Release**（不消耗下载站流量、
  不受 raw 缓存限制），大文件仍在野草云。届时启动器只需要换"清单地址"这一处，
  清单内部结构不变。
- **破缓存**：无论哪种来源，都带上 `?t=<时间戳>` + `Cache-Control: no-cache`（两端现在都这么干，
  保持）。野草云目前**没有** `Cache-Control` 响应头，所以时间戳参数是必须的。

### 2.5 启动器必须做的校验（照抄）

1. `schemaVersion` 必须是支持的版本，否则明确报错（不要当成"没有更新"）。
2. `productKey` 必须匹配；不匹配 → 报"这份清单不是给当前启动器的"。
3. `runtime` 必须与当前端一致。
4. `channel` 必须与当前通道一致（`stable` / `test`）。
5. `files[].path` 必须是**相对路径**、不含 `..`、不含盘符、不含开头斜杠；
   含有这些的清单直接判为非法（防路径穿越）。
6. `files[].sha256` 必须是 64 位小写 hex。
7. `files[].sizeBytes` 必须 > 0 且是安全整数。
8. `version` 比本地版本**更低**时：不要自动"降级安装"，提示玩家"线上版本更新，请更新启动器"或按你现在的策略处理。

---

## 三、下载站（dl.crossingvoid.top）

### 3.1 URL 规则

```
https://dl.crossingvoid.top/games/<产品段>/<版本>/<files[].path>
```

产品段与 `productKey` 的对应关系见附录 C。例：
`https://dl.crossingvoid.top/games/crossingvoid/0.5.13/CrossingVoid/Content/Paks/pakchunk0-Windows.ucas`

### 3.2 实测参数（2026-09-20 实测，写死到代码里也别踩坑）

| 项 | 实测值 | 对启动器的要求 |
| --- | --- | --- |
| 范围请求 | `Accept-Ranges: bytes`；`-r 0-1023` → `206` | **断点续传可用**，大文件务必用 |
| 限速 | **每连接**前 64 MB 不限速，之后 **8 MB/s** | 大文件（零境 916 MB）单连接要 2 分钟以上 → 必须并发 |
| 并发 | `limit_conn perip 6`，第 7 条连接 **503** | **并发上限 6，建议 4**（留余量给清单/心跳） |
| 清单 Content-Type | `application/json` | 直接 `JSON.parse` |
| 清单缓存头 | **没有** `Cache-Control` | 必须带 `?t=<时间戳>` |
| 证书 | Let's Encrypt，有效至 2026-12-16 | 正常 HTTPS 校验即可 |
| 单连接速度 | 前 64 MB 区间实测 **11 MB/s** | 别用"8 MB/s × 连接数"去估时间，前 64 MB 更快 |
| 服务器并发承受 | 2 核 2 GB 的 VPS | 别指望能扛很多玩家同时满速；玩家侧并发也别调高 |

### 3.3 一次更新大概要下多少（决定你怎么做进度条）

| 场景 | 要下的量（实测） |
| --- | --- |
| 首次安装 · 零境 PC | 2.71 GB（86 个文件） |
| 首次安装 · 零境 安卓 | 2.21 GB（apk 69.8 MB + obb 2042.5 MB） |
| 首次安装 · 幻杀 安卓 | 106 MB（适合做联调） |
| 只改了贴图 / 重打包 | 可能还是 900 MB 起（**改一个贴图 = 变的是它所在的那个 pak 容器**，零境 `pakchunk0` 就是 916 MB） |
| 什么都没改、重发同一版 | **0 字节**（清单逐文件 sha256 比对后完全相同） |

> 这里就是"为什么以后要做 patch pak"的原因；本期先用文件级，够用且简单。

---

## 四、下载算法（照着写就行）

### 4.1 主流程

```text
1. 读清单（带 ?t=），校验（2.5）
2. 读本地状态：{ 路径 → { sizeBytes, sha256 } }   ← 上次落位时保存的（4.7）
3. 生成计划：
     for each files[]:
        local = 本地状态[path] 且 磁盘上这个文件存在且 sizeBytes 对得上
        要下 = !(local.sha256 === files[].sha256)
   删除集合 = 本地状态里有、但新清单里没有的路径（限定在可删白名单内，见 5.3）
4. 下载（并发 ≤6，建议 4）：
     for each 要下的文件:
        若已有 <目标>.part 且大小 < sizeBytes → 用 Range: bytes=<已下>- 续传
        否则从 0 开始下到 <目标>.part
        下完 → 校验 sha256
          不符 → 删 .part，整文件重下（最多 2 次）→ 仍失败则整体失败（不要"差不多就行"）
        相符 → 原子改名 <目标>.part → <目标>（同目录 rename）
5. 处理删除集合：逐个删（删不掉就记日志，不要因此让整次更新失败）
6. 刷新本地状态文件：把新清单的 files[] 全部写进去（含没下过的）
7. 安卓额外一步：APK 走系统安装、OBB 落到指定目录（见 5.2）
8. 完成后把"已安装版本"记成清单的 version
```

### 4.2 "要不要下"只看 sha256

**不要**只看大小或时间戳。实测过"容器大小完全一样、内容却全变了"的情况
（零境重新打包时三个 pak 容器就是这种），只比大小会漏更新。
正确判据：`本地记录的 sha256 === 清单里的 sha256`（本地记录来自上次落位，4.7）。

### 4.3 半包防护（必须有）

- 一律先写 `<文件名>.part`，**绝不直接写目标名**；
- 校验通过后 `rename`（同目录、同名替换）；
- 进程被杀/断电留下的 `.part` 下次要能被识别并续传，或者干脆删掉重下（二选一，别一半一半）。

### 4.4 断点续传

- 用 `Range: bytes=<已下字节数>-`；
- 服务端返回 `206` + `Content-Range` → 继续追加；
- 返回 `200`（服务端不支持 Range）→ 从头下；
- **校验失败时不要续传拼接**：删掉 `.part` 整文件重下。

### 4.5 并发与限速怎么配合

- 并发 4 时：前 64 MB 内大概能跑 30~40 MB/s（受服务器出口限制），之后每连接压到 8 MB/s；
- 大文件（>64 MB）优先排队，别让它排在最后才开始；
- 小文件（<1 MB）一批下完立刻写盘，避免内存堆着；
- 并发别超 6：第 7 条连接会被 nginx 直接 `503`。

### 4.6 本地状态文件（本地哈希表的落点）

| 端 | 建议位置 | 说明 |
| --- | --- | --- |
| PC | 安装目录里的 `CrossingVoid.manifest.json`（**现在就有这个文件**，见 7.4） | 结构直接沿用 `{ "files": [ { path, sizeBytes, sha256 } ] }`，和清单的 `files[]` 同形 → 可以直接"清单换状态" |
| 安卓 | 应用私有目录（`filesDir` / `getExternalFilesDir`）里的 `game-manifest.json` | OBB 在公共目录、状态不能放那儿；用包名目录下的私有文件 |

---

## 五、落位规则

### 5.1 PC

- 落位根目录 = 启动器现在用的**游戏安装目录**（`src/gameInstallPath.ts`：
  `GAME_CONTAINER_DIRECTORY_NAME = "TFAC-hz64"` + `GAME_DIRECTORY_NAME = "CrossingVoid"`，
  默认 `D:\TFAC-hz64\CrossingVoid`）——**保持不变**；
- `files[].path` 直接作为相对路径拼到安装目录下（`CrossingVoid/Content/Paks/xxx.pak` → 
  `<安装目录>\CrossingVoid\Content\Paks\xxx.pak`）；
- 清单里带的 `CrossingVoid.version.json` 也要按普通文件落位（**别当垃圾删了**，
  启动器自己的"读版本"就是读它，见 7.4）；
- 旧版本遗留的 `CrossingVoid.zip` / `*.碎片NNN` / `_download` 临时目录可以清掉（如果还有）。

### 5.2 安卓（APK + OBB）

**2026-09-20 起安卓是"chunk 化"形态**：清单里没有 OBB 本体，发的是 OBB 里的条目 + 一个旁车。
完整规则在**附录 D**，这里只列要点：

| 清单里的东西 | 落位 |
| --- | --- |
| `*-Android-Shipping-*.apk` | 下到应用私有目录 → **调系统安装**（`installDownloadedApk()` 那套）。**签名必须与已装版本一致**，否则覆盖安装失败 |
| `CrossingVoid/Content/Paks/*.pak`、`*.ucas`、`*.utoc` | **首次安装**：参与组装 OBB（见下）；**更新时**：放到 `Android/data/<包名>/files/UnrealGame/<工程名>/Saved/Paks/`，引擎启动自动挂载 |
| `Engine/…` 等非 pak 条目 | 只能进 OBB → 它们的变动要触发一次 OBB 重组 |
| `<工程名>.version.json` | 应用私有目录（或 OBB 同级），按普通文件落位 |
| `<工程名>.obb.json`（旁车） | 读它拿到"要组装的 OBB 叫什么名字、由哪些条目组成"；组装完放 `Android/obb/<包名>/<obbFileName>` |

顺序要求：

1. **先把 APK 和 OBB 都下完并校验**，再安装/替换 —— 安卓上替换 APK 会杀掉进程，
   边下边装会把自己弄死；
2. 装完 APK 之后再确认 OBB 在位（有的机型安装时会清 OBB 目录）；
3. `Android/obb/<包名>/` 里**其他 `main.*.obb` 要清掉**，否则可能被引擎复用成旧数据；
4. 包名是 `com.TFAC.CorssingVoid` / `com.TFAC.Fantasy`（**拼写就是这样，别"纠正"它**）。

### 5.3 删旧文件的白名单（清单里没有就删）

**可以删**：安装目录内由清单管理过的文件（即上次状态文件里记着的、这次清单里没有的）。

**绝不能删**：

- PC：玩家自己的存档/配置 —— `Saved\` 整个目录（尤其 `Saved\SaveGames`、`Saved\Config`）；
  日志 `Saved\Logs` 也别删（排查要用）；
- 安卓：应用私有目录里的玩家数据、`Android/data/<包名>/`；
- 启动器自己的文件（日志、设置、`*.part` 之外的临时文件按你自己的规则）。

> 服务端也是同一条原则：**只有清单里的东西才归发布系统管**。
> 所以分发侧生成清单时，把 `Saved\Logs` / `Saved\Crashes` / `.pdb` / 发布工具残留都排除了
> （见附录 B），启动器不会因为"清单里没有"去删它们。

---

## 六、错误与边界（现象 → 原因 → 启动器怎么表现）

| 现象 | 原因 | 启动器应该怎么做 |
| --- | --- | --- |
| 文件 `404` | 该版本已被下线（服务器只保留最新一版 + 上一版，更早的会删） | 提示"该版本已下线，请更新到最新版"，**不要无限重试** |
| 清单 `schemaVersion=2` 而启动器只认 1 | 过渡期没兼容旧格式 | 要么按老流程走，要么明确报"清单格式不受支持" |
| `productKey` 不符 | 拿错清单（多游戏平台尤其容易） | 报"清单不属于当前游戏"，停止 |
| 下载到一半断网 | 正常 | 保留 `.part`，恢复后 `Range` 续传；UI 显示"已暂停/可继续" |
| sha256 不符 | 传输损坏 / 服务器正在更新 | 删 `.part` 整文件重下，最多 2 次；仍失败 → 报错并给出文件名 |
| 第 7 条连接 `503` | 并发超了 `limit_conn perip 6` | 把并发降到 4 并重试该文件 |
| 下载很慢（8 MB/s 封顶） | 触发了每连接限速 | 提高**并发**而不是提高单连接速度 |
| 磁盘不够 | 首次安装 2.7 GB 起 | 下载前用 `get_available_space`（PC 已有）检查并提示 |
| 清单里出现 `..` 或绝对路径 | 恶意/损坏清单 | 判非法并拒绝整份清单 |

---

## 七、对着代码的改造清单

### 7.1 PC 启动器（Tauri + Vue）

| 文件 | 现在 | 要改成 |
| --- | --- | --- |
| `src/App.vue:367` `gameMetadataManifestUrl` | 写死 `.../windows-latest.json` | 从"游戏目录"（`src/platform/gameCatalog.ts`）里按当前游戏取；多游戏平台就是把每个游戏的清单地址做成一行配置 |
| `src/App.vue:1919-1939` 拉清单 + 校验 | `manifest.schemaVersion !== 2` → 抛"版本不受支持" | 认 `schemaVersion === 1`（v1 分支），`2` 走老分支（过渡期同时保留） |
| `src/App.vue:1740` 选 asset | 按 `runtime` 从 `latest.assets[]` 里挑 | v1 没有 `assets`，直接用 `files[]` + `baseUrl` |
| `src/launcherTypes.ts` `UpdateManifestPayload` / `DownloadArchiveChunk` | v2 类型 | 新增 `GameFileManifestV1` / `GameFileEntry`（字段照 2.3） |
| `src-tauri/src/lib.rs`：`download_archive_to_staging` / `import_game_chunks` / `repair_game_from_staged_archive` | 切片下载 → 拼接 → 解压 → 落位 | 新增/改造为 `download_files(manifest)`：并发下载 + sha256 + `.part` → rename；**切片那套保留但不再调用** |
| `src-tauri/src/lib.rs:810-870` | 已经在处理安装目录里的 `CrossingVoid.version.json` + `CrossingVoid.manifest.json`，还有 `productKey == "crossingvoid-game"` 的纠正逻辑 | **这是现成的"文件清单 + 版本文件"能力**，直接复用：本地状态就用 `CrossingVoid.manifest.json` |
| `src/gameInstallPath.ts` | 安装路径推导 | 不动（落位根目录不变） |
| `src/platform/gameCatalog.ts` | 五个游戏条目，`implemented` 标记 | 每个游戏补上 `{ productKey, manifestUrl, installDirName }`，为多游戏铺路 |
| 下载源开关（`official` / `github`） | 官方源 = OSS 切片 | 官方源改成 dl 的文件级下载；GitHub 源可以保留做备用（同一份清单的 `files[].path` 也适用于 GitHub，如果那边也放了产物） |

### 7.2 安卓启动器（Capacitor + Vue）

| 文件 | 现在 | 要改成 |
| --- | --- | --- |
| `src/services/gameUpdate.ts` `ANDROID_GAME_METADATA_MANIFEST_URL` / `PRODUCT_KEY` | 写死零境的 www 地址 + `crossingvoid-android-game` | 按游戏取（附录 C 的六行表）；`productKey` 跟着当前游戏走 |
| 同文件 `parseAndroidUpdateCheckResponse` | 严格 v2：`schemaVersion===2`、`latest.assets[0].chunks[]`、**还硬编码校验分片名**（`CrossingVoid手机端.碎片001`、`CrossingVoid.001`） | 新增 v1 解析：`schemaVersion===1` + `files[]`；**那些分片名校验要删掉**（v1 没有分片概念） |
| `src/services/downloadPlan.ts` `buildAndroidDownloadPlan` | 由 chunks 生成下载计划（校验 index/count、累加大小） | 改成由 `files[]` 生成计划：`{ path, url, sizeBytes, sha256 }`；`productKey` 不再是字面量 |
| `src/services/downloadSource.ts` | `official` / `github`，默认 **github** | 默认改成 `official`（走 dl）；github 仅作备用 |
| `src/services/androidLauncher.ts` 的桥方法 | `startGameDownload(plan)` / `importGameChunks(plan)` / `exportGameChunks()` 都是**切片**语义 | 新增 `downloadGameFiles(plan)` + `installGameApk()` + `ensureGameObb()`（或把现有的改名/改造）；`obbPath` / `obbFileName` 那两个字段继续用 |
| 原生侧（`android/app/src/main/java/...`，Kotlin 插件） | 按 chunk 下载 zip 分片 → 合并 → 解压 → 装 APK + 放 OBB | 改成按文件下载（Range + sha256 + `.part`）→ 装 APK → 放 OBB；**解压那一段可以整段删掉** |
| `src/App.vue`（进度/阶段） | `totalChunks` 等切片语义 | 换成"文件数 + 总字节数"，显示"3/86 个文件 · 1.2/2.7 GB" |

### 7.3 两端建议共享的部分

清单校验、计划生成、sha256 判定、`.part`→rename、错误分类这几块逻辑**两端完全一样**，
建议抽成一个 `gamePackageManifest.ts`（纯 TS，不碰平台 API），两端各自只写：

- 取文件字节流（PC：Rust 侧 reqwest；安卓：Kotlin 插件）
- 落位（PC：直接写安装目录；安卓：APK 安装 + OBB 目录）
- 读本地状态文件的位置

### 7.4 PC 侧现成能力（别重复造）

`src-tauri/src/lib.rs` 里已经有的（来自旧的 v1 流程）：

- `validate_install_state` / `validate_game_install_state`：判断安装目录是否"ready"；
- `read_game_version_file` / `migrate_mislabeled_game_version`：读/纠正 `CrossingVoid.version.json`；
- `repair_game_from_archive` + `RepairSummary { checkedFiles, repairedFiles }`、
  `ManifestVerifySummary { checkedFiles, invalidFiles, missingFiles }`：
  **按文件清单逐文件核对并修复**（这正是新流程要的"本地哈希表 + 只补缺"）；
- `calculate_file_sha256_with_progress`：带进度的 sha256。

也就是说 PC 端**大半逻辑已经在了**，新流程主要是把"下载源"从切片换成 `baseUrl + path`。

---

## 八、怎么测

### 8.1 本地假下载站（不碰线上）

```bash
mkdir -p /tmp/fake && cd /tmp/fake
printf 'hello' > a.txt
python3 -m http.server 8000     # 或本地任意静态服务器
```

然后手写一份 v1 清单，把 `baseUrl` 改成 `http://127.0.0.1:8000/`，
`files[]` 里放 `a.txt`（sha256 用 `sha256sum a.txt` 算），拿它跑通
"逐文件下载 → 校验 → `.part` → 改名 → 写状态文件"。

### 8.2 用真实数据测（今天就能测）

| 目的 | 用哪个 |
| --- | --- |
| 小体积联调（106 MB，几分钟） | 幻杀安卓：`/games/fantasyproject-android/manifests/0.1.0.json` |
| 大文件 / 续传 / 限速 / 并发 | 零境 PC：`/games/crossingvoid/manifests/0.5.13.json`（`pakchunk0-Windows.ucas` 961 MB） |
| 安卓 OBB 落位 + 安装流程 | 零境安卓：`/games/crossingvoid-android/manifests/0.5.13.json`（obb 2.04 GB） |

### 8.3 三个验收点（做完请拿这三条自检）

1. **首次安装**：空目录 → 下完 → 逐文件 sha256 与清单一致 → 游戏能起；
2. **改一个文件**：把假清单里某个文件的 sha256 改掉（或等真有一版新产物），
   再跑一次 → **只下那一个文件**（日志里能看到其余全是跳过）；
3. **断网续传**：下到一半拔网线 → 恢复后继续，最终 sha256 一致；
   再补一条：**中途杀进程** → 重开后 `.part` 能被续传或安全重下（不能留下半包目标文件）。

### 8.4 真机注意

- 并发别超 6（建议 4）；服务器是 2 核 2 GB 的 VPS，玩家侧也别开高；
- 下载站每连接 8 MB/s 封顶（前 64 MB 不限速），要快就靠并发；
- 安卓安装 APK 会杀掉自己的进程，**先下完再装**。

---

## 九、明确不做 / 后续

| 项 | 说明 |
| --- | --- |
| UE patch pak（`_P.pak`） | **暂缓**。理由：chunk 已经承担了"只换变动内容"的职责；契约里的 `patches[]` 字段保留不删。什么时候上：改一张图经常要重下 200 MB 以上时 |
| 边下边装 / 省空间 | 下一期（等文件级清单跑通、有了真实更新数据再动） |
| 游戏内分片下载器（`BuildManifest`） | 那是另一条线（服务器日志里那批 404 的来源），本期不碰 |
| chunk 怎么分 | 用户自己分，插件侧不动 |

---

## 十、分发侧现状（排查问题时看，启动器不需要读）

### 10.1 目录布局（docroot = `/srv/downloads`）

```
games/<产品段>/<版本>/<文件…>                     ← 最新版（对外下载）
games/<产品段>/manifests/<版本>.json              ← 最新版清单
games/<产品段>-archive/<版本>/<文件…>             ← 上一版归档（硬链接，不额外占空间）
games/<产品段>-archive/<版本>.manifest.json       ← 上一版清单副本
```

- **树里永远只有最新那一版**：发 0.5.14 时 `games/crossingvoid/0.5.13/` 会消失
  （内容变成 `-archive/0.5.13/`）。所以玩家还停在被删版本会拿到 `404`。
- `*-archive/` 是分发侧自己的备份，**不是给启动器读的**，随时可能被下一次发布覆盖。

### 10.2 内部机制（知道结论就够）

- 裸仓库 `/srv/repos/game-<产品段>.git`，工作树就是 `/srv/downloads`；
- 每次推送：hook 先把上一版硬链接归档 → `checkout -f main` 落位 →
  `update-index --refresh` → 过期 reflog → `repack -a -d` + `prune`；
- 仓库里**永远只有一条提交**（分发侧用 `commit-tree` 写没有父提交的提交）；
- 两个仓库都设了 `core.trustctime=false`（硬链接会改 ctime，不改这个 git 会误判"文件全被改过"）；
- 清单 `generatedAt` 在内容没变时沿用上次的值 → 同内容重发时**清单字节都不变**，推送 0 字节。

### 10.3 实测数字（2026-09-20）

| 项 | 值 |
| --- | --- |
| 磁盘 | 30 GB 盘：已用 **12 GB** / 可用 **18 GB** |
| `/srv/downloads` | 零境 PC 2.6 G、零境安卓 2.1 G、幻杀安卓 107 M（两个 archive 都是硬链接，几乎不占） |
| `/srv/repos` | 零境 PC 2.4 G、零境安卓 2.1 G、幻杀安卓 99 M |
| 首次上传耗时 | 零境 PC 2.33 GiB / 534 秒（约 4.6 MiB/s）；安卓 2.02 GiB / 426 秒 |
| 幂等 | 同内容重发 = **0 字节**；改一个容器 = 只传那个容器（实测 1.12 GiB 全是变了的 pak） |

---

## 附录 A：v2 → v1 字段对照（改代码时对着看）

| 概念 | v2（旧） | v1（新） |
| --- | --- | --- |
| 版本 | `latest.version` | `version` |
| 通道 | `latest.channel` | `channel` |
| 下载源 | `latest.assets[].objectKey`（OSS）或 `downloadUrl` | `baseUrl` + `files[].path` |
| 文件粒度 | `latest.assets[].chunks[]`（500 MB 字节切片） | `files[]`（每个真实文件一条） |
| 文件名 | `chunks[].fileName` / `githubFileName`（中文碎片名） | `files[].path`（相对路径，和打包产物一致） |
| 校验 | `assets[].sha256`（整包）+ `chunks[].sha256` | `files[].sha256`（逐文件） |
| 解压 | 需要（切片 → zip → 解压） | 不需要 |
| Release 标签 | `downloadReleaseTag`（GitHub Release 名） | 没有这个概念（不再依赖 GitHub Release 分发） |

## 附录 B：清单里"有"和"没有"的东西

**清单里有**：PC 的 `CrossingVoid.exe`、`CrossingVoid/Binaries/...`、`CrossingVoid/Content/Paks/*`、
`Engine/...`、`Manifest_*.txt`、`NOTICES.txt`、`CrossingVoid.version.json`；
安卓的 apk、**OBB 里的每个条目**（`<工程名>/Content/Paks/*`、`Engine/...`）、
`<工程名>.version.json`、`<工程名>.obb.json`（旁车）。**OBB 本体不在清单里**（见附录 D）。

**清单里没有（分发侧主动排除，启动器不要去找）**：

- PC：`*.pdb`、`Manifest_DebugFiles_Win64.txt`、`Saved/Logs`、`Saved/Crashes`、`.git`、`_download`；
- 安卓：`CrossingVoid_Symbols_v1/`（符号，2.2 GB）、`AFS_*.apk`、`Install_* / Uninstall_* / SymbolizeCrashDump_*.bat`、
  `win-x64/UnrealAndroidFileTool.exe`。

> 顺带一个变化：**PC 清单从 87 条变成 86 条** —— 以前那个 `Manifest_DebugFiles_Win64.txt`
> 现在按旧发布脚本的规则被排除了。客户端运行不需要它。

## 附录 C：三个游戏 × 两个平台（启动器按这张表配置）

| 游戏 | 平台 | `productKey` | dl 产品段 / `baseUrl` 尾段 | 版本标记文件 |
| --- | --- | --- | --- | --- |
| 零境交错 | PC | `crossingvoid-game` | `crossingvoid` | `CrossingVoid.version.json` |
| 零境交错 | 安卓 | `crossingvoid-android-game` | `crossingvoid-android` | `CrossingVoid.version.json` |
| 幻杀 | PC | `fantasyproject-game` | `fantasyproject` | `FantasyProject.version.json` |
| 幻杀 | 安卓 | `fantasyproject-android-game` | `fantasyproject-android` | `FantasyProject.version.json` |
| 火影 | PC | `narutobp-game` | `narutobp` | `NarutoBP.version.json` |
| 火影 | 安卓 | `narutobp-android-game` | `narutobp-android` | `NarutoBP.version.json` |

> 三个游戏的 `productKey` 各不相同（零境启动器读前两个，幻杀/火影各自的启动器读自己那两个）。
> 多游戏平台的做法：把这张表做成 `gameCatalog` 里的字段，别再写死单游戏常量。

---

## 附录 D：安卓的"chunk 化"形态（2026-09-20 定，分发侧随后就按这个改）

### D.1 为什么要拆掉 OBB 这一层

实测（幻杀那个 43.8 MB 的 OBB，`System.IO.Compression` 打开）：

```
条目数 = 3
41.53 MB → 41.53 MB（压缩后一样 = OBB 是"不压缩 ZIP"）  FantasyProject/Content/Paks/pakchunk0-Android_ASTC.pak
 0.25 MB                                              Engine/Content/Renderer/TessellationTable.bin
      0                                               Engine/Config/StagedBuild_FantasyProject.json
```

**OBB 就是一个"把 chunk 容器装起来的 ZIP"**，里面本来就按 chunk 分好了（零境的 OBB 装的是它那 9 个
`pakchunkN` 的 `.pak`/`.utoc`/`.ucas` 三件套）。

所以：

- 把 OBB 当**一个文件**发 → 改一个 chunk = 重下 2 GB（文件级清单帮不了你，因为变的是那个 2 GB 文件）；
- 把 OBB **按条目拆开发** → 文件级清单直接命中"只下变化的那几个 chunk"✓，**这才是安卓和 PC 对齐的那一步**。

### D.2 dl 上的形态（改了之后）

| 发布物 | 说明 |
| --- | --- |
| `*-Android-Shipping-*.apk` | 主 APK，不变 |
| **OBB 里的每个条目** | 原样按 OBB 内部路径发（`CrossingVoid/Content/Paks/pakchunk0-Android_ASTC.pak`、`Engine/Content/…`）——**不再是 `main.<n>.<包名>.obb` 这一个大文件** |
| `<工程名>.version.json` | 版本标记，不变 |
| `<工程名>.obb.json` | **新增的旁车文件**：告诉启动器"要组装的 OBB 叫什么名字、由哪些条目组成" |

旁车文件样例（`CrossingVoid.obb.json`）：

```json
{
  "obbFileName": "main.1.com.TFAC.CorssingVoid.obb",
  "entries": [
    "CrossingVoid/Content/Paks/pakchunk0-Android.ucas",
    "CrossingVoid/Content/Paks/pakchunk0-Android.utoc",
    "CrossingVoid/Content/Paks/pakchunk0-Android.pak",
    "Engine/Content/Renderer/TessellationTable.bin",
    "Engine/Config/StagedBuild_CrossingVoid.ini"
  ]
}
```

> `obbFileName` 里的 `1` 是 **APK 的 versionCode**（安卓的标准命名 `<main|patch>.<versionCode>.<包名>.obb`）。
> 启动器组装时用**已安装 APK 的 versionCode** 拼这个名字，别写死。
> `crossingvoid-android` 那份清单里那个 `<工程名>.obb.json` 是普通文件，按清单逻辑落位即可。

清单（安卓，chunk 化之后）长这样：

```json
{
  "schemaVersion": 1,
  "productKey": "crossingvoid-android-game",
  "runtime": "Android",
  "version": "0.5.14",
  "channel": "stable",
  "baseUrl": "https://dl.crossingvoid.top/games/crossingvoid-android/0.5.14/",
  "files": [
    { "path": "CrossingVoid-Android-Shipping-arm64.apk", "sizeBytes": …, "sha256": "…" },
    { "path": "CrossingVoid/Content/Paks/pakchunk0-Android.ucas", "sizeBytes": …, "sha256": "…" },
    { "path": "CrossingVoid/Content/Paks/pakchunk0-Android.utoc", "sizeBytes": …, "sha256": "…" },
    { "path": "CrossingVoid/Content/Paks/pakchunk3-Android.ucas", "sizeBytes": …, "sha256": "…" }
  ],
  "patches": []
}
```

**清单结构没有任何新字段** —— 变的只是"安卓这一侧发的是 chunk 文件而不是 OBB"。

### D.3 启动器怎么用（两条落位路径）

**首次安装**：清单里的条目全下 → 用 **STORED（不压缩）ZIP** 组装出 `obbFileName` 那个文件 →
放 `Android/obb/<包名>/<obbFileName>` → 再装 APK（顺序仍按 5.2：先下完校验、再装）。
组装后的 OBB 不需要和 UE 原本产出的字节一致，引擎只按 ZIP 条目读。

**之后更新**（只下 sha256 变了的条目）：

| 变化的条目类型 | 放哪 | 为什么 |
| --- | --- | --- |
| `content/Content/Paks/*.pak`、`*.ucas`、`*.utoc` | `Android/data/<包名>/files/UnrealGame/<工程名>/Saved/Paks/` | 引擎启动时会扫这个目录（源码见 D.5），**不需要重建 OBB**，秒级落位 |
| `Engine/…`、其它非 pak 条目 | 重建 OBB（这些文件正常只在做基线时才变） | 引擎的 `Engine/…` 只能从 OBB/APK 里读 |

> 只有 `Saved/Paks` 里那一份变了时：**APK 不用重装、OBB 不用重建**，
> 玩家体感就是"更新了几十 MB 就能进游戏"。

### D.4 引擎侧的挂载事实（源码证据，UE 5.8.2）

| 结论 | 出处 |
| --- | --- |
| 启动时扫三个目录找 `*.pak`（正式包也扫，`Saved/Paks` 那行不在 `#if !UE_BUILD_SHIPPING` 里） | `IPlatformFilePak.cpp:5624-5642`：`<ProjectContentDir>/Paks/`、**`<ProjectSavedDir>/Paks/`**、`<EngineContentDir>/Paks/` |
| 安卓的 `Saved` 是可写的外部目录 | `AndroidPlatformFile.cpp:261`：`#define FILEBASE_DIRECTORY "/UnrealGame/"` → `Android/data/<包名>/files/UnrealGame/<工程名>/Saved` |
| IoStore 的 `.utoc/.ucas` 会跟着同名 `.pak` 一起被找到 | `IPlatformFilePak.cpp:6123`：`FPaths::ChangeExtension(InPakFilename, ".utoc")` |
| `_P.pak` 会自动**提优先级**（patch 覆盖 base 是引擎内建行为） | `IPlatformFilePak.cpp:6039-6060`，注释原话：*Increment by one so that the first patch file still gets more priority than the base pak file* |

⚠️ **需要真机验一次的一条**：`Saved/Paks` 里那份 pak 与 OBB 里同名的 pak **谁的优先级高**。
`_P.pak` 有源码保证，所以稳妥做法是**变化的那份用 `_P.pak` 命名**（官方 patch pak 用法）；
如果直接用同名普通 pak，务必先在一台真机上确认覆盖生效。

### D.5 首次安装要下多少（零境实测）

拆成条目之后，**首次安装的总下载量不变**（还是那些 pak 的字节数，合计约 2.0 GB），
区别只在"以后的更新"：从"2 GB 起"变成"变的那几个 chunk 之和"。
代价是首次安装时启动器要多花一次本地组装 OBB 的时间（2 GB 的 STORED ZIP，手机上是几十秒级）。

### D.6 前提与还没做的

| 项 | 状态 |
| --- | --- |
| 分发侧改成"解 OBB、发条目 + 旁车" | ✅ **已做完并实测**（2026-09-20）：幻杀安卓 `0.1.1` 就是第一个 chunk 化版本——清单 6 条（apk + 3 个 OBB 条目 + 版本标记 + 旁车），OBB 本体不再上传；旧版 `0.1.0`（apk+obb 形态）已归档 |
| 启动器改成"下载 chunk → 组装 OBB / 落 Saved/Paks" | 本文档这一节就是给它的说明，还没改 |
| **chunk 分配粒度** | ⚠️ 上游前提：`.pak` 模式下官方 chunk 分配链路是死的（见你工程里 `DreamChunkDownloader\Docs\2026-09-18-分包链路排查记录.md`）。不解决它，"变化的 chunk"可能仍然是整个 `pakchunk0`（零境 916 MB） |
| OBB 只在做基线时重建 | 一旦走这条，OBB 就是"底座"；每次都重建 = 玩家每次重下 2 GB |
| 真机验证 `Saved/Paks` 覆盖 OBB | 见 D.4 的 ⚠️ |
