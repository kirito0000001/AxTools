# 游戏包上传 · 进度表（单一入口）

> 这次大工程**只看这一页**就知道：做到哪了、下一步做什么、怎么算做完。
> 每次收工前更新本页；卡住时先看本页的「下一步」。
> 起始日期：2026-09-20。设计详见同目录 `2026-09-20-game-upload-design.md`。

## 现在在哪 / 下一步

- ✅ 设计定稿（含"chunk 已经替代 patch"的认定、文件级清单、git 增量、野草云落点）
- ✅ 野草云现状侦察完毕（结论见文末「侦察基线」）
- ✅ **P1 链路验证通过**（2026-09-20，只读校验；服务器上本来就有 `test-50m.bin`，不需要再放文件）
- ✅ **P2 完成（PC + 安卓都做了）**：git/裸仓库/通用 hook/uploader 账号就位，
  0.5.13 的 PC 与安卓产物都已上线（详见下方 P2 节与「磁盘与仓库体积」）
- ✅ **P3 完成（2026-09-20）**：AxTools 侧动作集改造 + 发布内核重写 + 服务端 hook 两处修复，
  PC 与安卓都在真机上跑通了；磁盘从 14G 已用回收 到 **11G 已用 / 18G 可用**
- 🔄 **进行中 = P4**：P4.1 内核去硬编码 ✅、P4.2 服务端四个仓库 ✅、P4.3 AxTools 铺开（三个游戏各一页）✅、
  **P4.4 真机发布：幻杀安卓 ✅，还剩幻杀 PC + 火影（等产物）**；之后 P4.5 启动器对接（下一期主线）
- ⬜ 待查疑点仍在：重打包后 chang 容器"大小不变、内容全变"，是**内容真变了**还是
  **IoStore 非确定性**？→ 见文末「待查疑点」（现在 P3 的清单差异报告会直接把它显示出来）
- ⏸ 待用户拍板：nginx 限速是否调整（默认不动，见设计文档「限速说明」）

## P1 · 链路验证（手工，零代码改动）

| 项 | 内容 |
| --- | --- |
| 做什么 | 在服务器 `/srv/downloads` 放一个 10 MB 级测试文件（现成的 `test-50m.bin` 也可以直接用） |
| 产出 | 一条能从公网下通的 URL |
| 怎么验证 | `curl -I https://dl.crossingvoid.top/<file>` → `200` + `Content-Length` + `Accept-Ranges: bytes`；`curl -r 0-1023` → `206` |
| 状态 | ☑ **2026-09-20 通过** |

实测结果（对 `https://dl.crossingvoid.top/test-50m.bin`）：

- HEAD → `200 OK`，`Content-Length: 52428800`，`Accept-Ranges: bytes`
- Range 请求 → `206 Partial Content`，`Content-Range: bytes 0-1023/52428800`（**断点续传可用**）
- 单连接实测速度：下 20 MB 用 1.81 s ≈ **11 MB/s**（在"前 64 MB 不限速"区间内）
- 同时确认：响应里**没有 `Cache-Control`**（清单不缓存这条待 P2 加）

## P2 · 服务器就位 + 首传（手工）

| 项 | 内容 |
| --- | --- |
| 做什么 | ① 装 `git`；② 建裸仓库 `/srv/repos/game-<key>.git`，worktree = `/srv/downloads/games/<key>`；③ 建 `uploader` 账号（独立密钥 + 只能推这几个仓库）；④ nginx 给清单路径加 `Cache-Control: no-store`；⑤ 手工 push 一次真实版本 |
| 产出 | `/srv/downloads/games/crossingvoid/<版本>/…` 全量就位；**v1 文件级清单**（含 `baseUrl`）生成但**不上线** |
| 怎么验证 | 公网 HEAD/GET 每个 pak → 200，且 sha256 与清单一致；`du -sh` 体积合理；旧版本按"保留 2 版"处理 |
| 状态 | ☑ **2026-09-20 完成**（清单也顺带放上了 dl，方便排查；www/Gitee 那条按方案 A 未上线） |

实测结果：

- 首传（2.33 GiB，140 个对象）：**534 秒**，约 4.6 MiB/s；提交（commit）本身 184 秒
- 第二次（重打包后的差异，28 个对象）：**1.12 GiB / 313 秒** —— 因为基础包 `pakchunk0` 也重建了
- 落位校验：`pakchunk0-Windows.ucas` 公网 `200` + `Content-Length: 961189680`，与本地字节数一致
- 清单：`https://dl.crossingvoid.top/games/crossingvoid/manifests/0.5.13.json`（200 / `application/json` / 87 条）
- 服务器体积：`/srv/downloads/games/crossingvoid` = **2.6 GB**，87 个文件

### P2 现场信息（后续都要用）

| 项 | 值 |
| --- | --- |
| 裸仓库 | `/srv/repos/game-crossingvoid.git`（`gc.auto=0`） |
| 工作树 | `/srv/downloads`（docroot；**里面只允许 `games/`**，顶层是 root 属主，别往顶层放文件） |
| push 地址 | `yecaoyun-upload:/srv/repos/game-crossingvoid.git`（ssh 别名 `yecaoyun-upload`，密钥 `~/.ssh/yecaoyun_uploader`） |
| 上传账号 | `uploader`（登录 shell 是 `/bin/bash`，但 `authorized_keys` 里加了强制命令 + 白名单脚本，只能 push/pull `game-*.git`；实测 `ssh yecaoyun-upload id` 被拒） |
| hook | `post-receive` → `git checkout -f main` 落到 `/srv/downloads`；日志 `/srv/repos/game-crossingvoid.hook.log`；失败会明确打印「落位失败」 |
| ⚠️ 教训 | 仓库里**不要放顶层文件**（我放过 `.gitattributes`，checkout 写 `/srv/downloads/.gitattributes` 权限不足报错） |
| 本机暂存仓库 | `F:\DaBaoV\下载站\crossingvoid`（内容 = `games/crossingvoid/<版本>/…` + `games/crossingvoid/manifests/<版本>.json`） |

## 磁盘与仓库体积（2026-09-20 实测，必须知道）

| 项 | 值 |
| --- | --- |
| 工作树 `/srv/downloads`（对外下载，P2 时） | **4.9 GB**（PC 2.6 + 安卓 2.1 + 测试文件 0.25） |
| 裸仓库 `/srv/repos`（git 对象，不对外，P2 时） | **5.7 GB**（PC 3.6 + 安卓 2.0） |
| 磁盘（P2 时） | 30 GB 里已用 **13 GB**，剩余 **17 GB** |

**P3 收工后（2026-09-20 实测）**：

| 项 | 值 |
| --- | --- |
| `/srv/downloads/games` | **4.7 GB**（PC 最新版 2.6 + 安卓最新版 2.1；两个 `*-archive/` 都是硬链接，`du` 上各只占 16~200 K） |
| `/srv/repos` | **4.5 GB**（PC 2.4 + 安卓 2.1，两个仓库都只剩 1 条提交） |
| 磁盘 | 30 GB 里已用 **11 GB**，剩余 **18 GB** |

### 保留策略（用户 2026-09-20 明确）

**只留「最新的一版 + 它的上一版」，各一个**（= 2 个目录／产品）。更早的删掉。

### 省空间做法（避免"文件一份、git 对象又一份"的双倍占用）

朴素做法会让每个版本在磁盘上存在两份（工作树一份 + git 对象一份），两个平台留 2 版 ≈ 19 GB，
余量不够。改成下面这套，占用降到约 **10 GB**：

1. **仓库树里只放当前版本**：`games/<产品>/<最新版本>/…`。上传时若发现树里有更老的版本，
   先 `git rm` 掉再提交 → git 对象只涨"一个版本"的量。
2. **"上一个版本"由服务端硬链接归档**：落位前把树里现有的版本目录 `cp -al` 到
   `games/<产品>-archive/<版本>/`（连带把该版本的清单也拷一份 `*.manifest.json`）。
   硬链接的准确说法是：**归档那一刻不复制数据**；随后 `checkout` 会删掉树里的原件，
   最终磁盘上仍然只有一份（这就是为什么要用硬链接而不是 `cp -r`：不用先多占一份）。
3. **每次推送压平历史（squash）+ 服务端回收**：
   - 客户端：`git commit --amend`（保持"一个提交"）→ `git push --force`
   - 服务端：hook 里跑一次省内存 `gc --prune=now`，把被替换掉的旧对象回收
   - 增量不受影响：推送时服务端仍会公布旧的 `main`，git 据此算出差异，**只发新增对象**
4. 稳态占用（两个平台都留"最新 + 一个旧的"）：

   | 项 | PC | 安卓 |
   | --- | --- | --- |
   | 工作树（最新版） | 2.4 GB | 2.1 GB |
   | 归档（上一版，硬链接→实际一份） | 2.4 GB | 2.1 GB |
   | git 对象（只含最新版） | ~2.4 GB | ~2.1 GB |
   | 小计 | ~7.2 GB | ~6.2 GB |

   **总计 ≈ 13.4 GB**（+ 系统 ~3 GB）→ 现有 17 GB 余量**刚好够但不宽裕**，
   所以先在 AxTools 里把体积显示出来，真顶不住再扩盘或只留最新版。

**⚠️ 在这台机器上跑 gc 必须带省内存参数**，否则会被 OOM 杀掉（实测 `pack-objects died of signal 9`）：

```bash
runuser -u uploader -- git --git-dir=/srv/repos/<repo>.git \
  -c pack.windowMemory=128m -c pack.threads=1 -c pack.packSizeLimit=1g \
  gc --prune=now
```

> 这台 VPS 是 2 GB 内存、**没有 swap**。要么一直用上面的参数，要么加个 1~2 GB 的 swapfile（需要你点头）。

### 上面这套要落到代码里（用户要求"能省就省，把代码记住就行，做进 ax 上传里面"）

**✅ 服务端这一半已经做完并实测**（2026-09-20）：

- hook 源码存进代码库：`Scripts/Servers/Aliyun/game-download-post-receive.sh`
  （已部署到两个真仓库，md5 一致、`bash -n` 通过）
- 用临时小仓库 `game-selftest.git` 跑了两轮推送验证：推 v0.2 时**只**归档本产品的 0.1、
  工作树只剩最新版、`*-archive/` 里只有上一版 + 它的清单、每次推送打印仓库对象/工作树/磁盘余量
- 顺带抓出并修掉两个 bug：① 归档范围过宽（会去动别的产品目录）② 把 `manifests/` 当版本目录

**✅ AxTools 那一半也做完（P3，2026-09-20）**：提交前自动清理树里比"上一版"更老的版本；
用 `commit-tree` 写**没有父提交**的新提交（不是 amend，原因见 P3 的坑）保持单提交；
把 hook 报回来的体积显示在日志里；发布成功后自动回收本机旧对象（见上「本地体积」），
回收完仍然偏大才发 `warning` 提示去点维护区的「重整仓库」。

## P3 · AxTools 动作改造

| 项 | 内容 |
| --- | --- |
| 做什么 | **动作集按流程重做，内核换掉**：面板上只剩「只检查，不发布 / 发布新版本」，维护动作「重整仓库（压平历史）」收进折叠区；内核脚本换成通用版 `Scripts\Publishing\Games\Publish-GameDownloadRelease.ps1`（P4 的火影/幻杀只换参数就能复用） |
| 产出 | 面板上点一次就把新版本传上去；日志里能看到每个阶段、差异报告和上传百分比 |
| 怎么验证 | ① 点一次能把 0.5.13 传上去 ② **什么都不改再点一次 → 0 字节** ③ 公网抽样（HEAD 字节数 + 清单条数）与本地一致 ④ 树里只有最新版、`*-archive/` 里只有上一版、`du` 不爆炸 |
| 状态 | ☑ **2026-09-20 完成**（PC + 安卓都跑过真机；21 个与本次无关的旧测试仍失败，见文末） |

### 实测结果（2026-09-20）

| 动作 | 结果 |
| --- | --- |
| PC「发布新版本」首次（内容与站上一致，只有清单/标记变） | 推送 29 个对象 / **1.12 GiB**（因为服务端缺的是重新打包过的基础包），落位成功 |
| PC「发布新版本」再点一次 | **跳过提交与推送（0 字节）**；公网校验：清单 86 条 + 抽样 5 个文件字节数一致 ✅ |
| PC「重整仓库」 | 本机仓库重建为单提交；服务端对象 **3.7 GiB → 2.4 GiB**，历史只剩 1 条提交 ✅ |
| 安卓「发布新版本」 | 推送 10 个对象 / 2.02 GiB，落位 + 归档 + 回收成功；公网校验 3 条清单 + 抽样 2 个文件 ✅ |
| 磁盘 | 30 GB 盘：已用 **11 GB**、可用 **18 GB**（动手前是 14G 已用 / 15G 可用） |

### 本地体积（2026-09-20 收尾，用户要求"本地别存两份"）

- 暂存目录里的产物改成**硬链接**（`New-Item -ItemType HardLink`，跨盘自动退回复制）：
  同一个数据块两个名字，镜像照旧能翻看，但**不额外占盘**。PC 86 个文件里 85 个是硬链接
  （剩下那个是脚本生成的 77 字节版本标记）。
- 每次发布成功后自动回收本机仓库旧对象（`reflog expire` + `prune`，体积还降不下来才做一次
  `repack -a -d`；同样不能用 `git gc`，它会把不可达对象留住）→ 本机只留**当前这一版**。
- 实测：`下载站` 的两个产品从 **8.97 GB → 4.38 GB**（D 盘可用 143.5 GB → 148.1 GB）；
  剩下的 4.38 GB 全是 `.git`（PC 2.36 + 安卓 2.02），也就是内容的压缩副本，用 git 就省不掉。

### 本机路径搬到了 F 盘（2026-09-20，用户要求）

用户担心 D 盘吃紧时会清东西，所以把**发布链路相关的两个目录**整体搬到 F 盘：

| 东西 | 旧位置 | 新位置 |
| --- | --- | --- |
| 本机暂存仓库（.git + manifests） | `D:\DabaoV\下载站` | **`F:\DaBaoV\下载站`** |
| PC 打包产物 | `D:\DabaoV\Crossingvoid\Windows` | **`F:\DaBaoV\Crossingvoid\Windows`** |
| 安卓打包产物 | `D:\DabaoV\Crossingvoid\Android` | **`F:\DaBaoV\Crossingvoid\Android`** |

搬之前先把 F 盘副本按**线上清单逐个 sha256 校验**（PC 85/85、安卓 2/2 全对）才删的原件。
实测：D 盘可用 148.1 → **159.2 GB**，F 盘可用 124.2 → **113 GB**；搬完再发一次，F 盘可用
一分未减（112.987 → 112.987 GB），证明镜像那份确实是零成本硬链接。

**两个必须记住的前提**：

1. **打包产物和暂存仓库必须在同一个盘**（硬链接的前提）。现在是 F↔F ✅。如果哪个游戏的产物
   留在别的盘，内核会自动退回复制，那个产品就会多占"一个版本"的体积（日志里会写"复制 N"）。
2. **UE 的打包输出目录还指着 D 盘**：`C:\CrossingVoid\Saved\Config\WindowsEditor\Game.ini` 里
   `StagingDirectory=(Path="D:/DabaoV/Crossingvoid")`。不改的话，下次打包又会生成
   `D:\DabaoV\Crossingvoid\Windows` → 跨盘、没有硬链接。**还没改，等用户拍板**（一行的事）。
   另外 `C:\CrossingVoid\Tools\Pack-CrossingVoid.ps1` 里 `$ReleaseRoot = 'D:\DabaoV\Crossingvoid\Release'`
   是**基线/补丁**那一套，跟本链路无关，暂不动（`D:\DabaoV\Crossingvoid\Release` 2.82 GB 还在 D 盘）。

### 顺手修掉的一个环境坑：系统代理

这台机器的"系统代理"是开关型的（`127.0.0.1:7897` + 用户随时切换/关掉）。公网校验原先用
`Invoke-WebRequest` 默认行为 → 走系统代理 → **代理程序没开时直接报 SSL 失败**（2026-09-20 实测撞到，
同一时刻 `curl` 直连是 200）。现在内核改成**先直连、失败再退回系统代理**。

> 还没动的一处：面板上"线上当前版本"和发布门禁走的是 `HttpClient`（默认也用系统代理）。
> 代理关着时它会失败并拦下发布（提示"无法确认线上最新游戏版本"）。要不要也给那侧加"直连优先"，
> 等用户确认——因为同一个 HttpClient 还要读 GitHub/Gitee，那边在国内可能**必须**走代理。

### 代码落点（P3）

| 文件 | 作用 |
| --- | --- |
| `Scripts\Publishing\Games\Publish-GameDownloadRelease.ps1` | 新内核（通用，参数化产品段）。旧的 `Scripts\Publishing\CrossingVoid\Publish-CrossingVoidGame.ps1`（OSS + 整体 zip + 切片）**已删除** |
| `Scripts\Adapters\CrossingVoidGame\Invoke-CrossingVoidGameAction.ps1` | 适配入口：新动作名 → 新内核；补上了 `Write-AxTaskResult`（旧版从来没发过 result 事件，宿主一律判失败） |
| `Scripts\Adapters\CrossingVoidPc\…`、`Scripts\Adapters\CrossingVoidAndroid\…` | 删掉够不到的游戏分包死分支（游戏发布只属于「零境交错：空界幻境」） |
| `AxTools.Core\Models\ManagedToolAction.cs` 等 | 枚举/描述/动作表/视图模型/XAML 全部跟着换；新增「维护（额外）」折叠区 |
| `Scripts\Servers\Aliyun\game-download-post-receive.sh` | 服务端 hook：两处修复见下 |
| `Scripts\Tests\Test-CrossingVoidGamePackage.ps1` | 契约 + 端到端测试（临时裸仓库真跑一遍：过滤规则、清单、幂等、压平、重整） |

### 这一轮踩出来的两个坑（都已在代码里修掉，别再犯）

1. **`git commit --amend` 压不住历史。** amend 只是换掉末端提交、**保留父提交**，
   所以 P2 留下的 3 条提交一直"可达"，服务端 gc 收不掉它们的 1.1 GiB 旧对象
   （实测 2.5 GB 内容把仓库撑到 3.7 GiB）。
   → 改成 `git write-tree` + `git commit-tree`（**不写父提交**）+ `git update-ref`：
   仓库永远只有 1 条提交、只含当前版本。增量不受影响（客户端仍然只发服务端没有的对象）。
2. **硬链接归档会被 `checkout -f` 打断。** `cp -al` 会刷新每个文件的 **ctime**，
   而 git 默认把 ctime 当"文件变脏"的依据 → `checkout -f` 认为 87 个文件全被改过、
   逐个大文件重写一遍，硬链接当场全断（工作树凭空多出 2.6 GB）。
   → 服务端仓库设 `core.trustctime false`（hook 里每次也顺手设一遍），
   checkout 之后补一条 `git update-index --refresh`。实测安卓那边归档的硬链接已经保住
   （`links=2`、同 inode），`du` 上归档只占 16 K。
**附带两条（也在 hook 里一起修了）**：服务端 `git gc` 走的是 `repack -A`，会**留住**不可达对象，
要回收得用 `repack -a -d` + `prune`；`pack.packSizeLimit` 拆包会把同一份大对象重复写进不同包
（实测多出 0.6 GB），所以去掉了这个参数（仍然保留 `pack.windowMemory=128m` + `pack.threads=1` 防 OOM）。
| 用户已确认（2026-09-20） | ① 暂存仓库路径 = `F:\DaBaoV\下载站\<key>`（原先在 D 盘，当天搬到 F 盘）✅<br>② **版本号先由用户手填**（以后改成从文件读）✅<br>③ 清单顺手在 dl 上放一份 ✅<br>④ 三个动作名**要改**，改得更贴近新流程 ✅<br>⑤ 上传密钥**写死** `~/.ssh/yecaoyun_uploader`（只有这台设备能发）✅<br>⑥ 本地不要存两份（镜像用硬链接）✅<br>⑦ 不做"回退到上一版"动作 ✅ |

### 动作集重新设计（2026-09-20 用户要求：按流程重做，不要硬套三个动作）

从"操作者实际要干什么"反推：日常只有一件事 —— **把这一版发出去**。

| 位置 | 动作 | 干什么 | 什么时候用 |
| --- | --- | --- | --- |
| 主区 | **发布新版本**（主按钮） | 检查产物 → 生成 v1 清单与哈希 → 提交本机仓库（树里只留这一版）→ push（增量、带进度）→ 落位校验 → 报体积与结果 | 每次发版 |
| 主区（小按钮） | **只检查，不发布** | 只做前半：产物检查 + 清单生成 + 体积/差异报告，**不联网** | 想先看一眼再发 |
| 维护（可折叠「维护（额外）」） | **重整仓库** | 把仓库压成单提交（全量重传、耗时长，需二次确认） | 体积超过阈值时 |

要点：

- **"续传 / 重发"不需要单独动作**：整套是幂等的 —— 没变化就 push 0 字节，断了就重跑同一个动作。
  这也是验收②（无改动再点一次 → 0 字节）的实际意义。
- **版本号由用户手填**（输入框，记住上次填的）。
- **两个平台是参数**（Windows / 安卓选择框），不是两个动作。
- 老的三个动作（制作游戏分片 / 上传游戏分片 / 制作并上传游戏）**退役**。
- 将来"发布清单到 www/Gitee"上线时，作为「发布新版本」的最后一步（或一个单独小动作），本期不做。

### 下载路径命名约定

「产品 = 游戏 + 平台」，因为两个平台的启动器与清单是分开的（`crossingvoid-game` / `crossingvoid-android-game`）：

| 产物 | dl 上的路径 | 清单 |
| --- | --- | --- |
| PC | `games/crossingvoid/<版本>/` | `games/crossingvoid/manifests/<版本>.json` |
| 安卓 | `games/crossingvoid-android/<版本>/` | `games/crossingvoid-android/manifests/<版本>.json` |

> PC 那份用的是短名（P2 先上线，改名要重传 2.6 GB）；要严格一致以后再改。

## P4 · 铺开 + 启动器对接

**先记一条 P3 留下的不一致（下一期一起解决）**：游戏页上显示的"线上当前版本"和
「发布新版本」的版本门禁（"新版本必须比线上高"）**读的还是 www 的 v2 清单**（现在停在 `V0.5.12`），
而分发侧真正的线上版本在 dl。本轮按方案 A 没动它——门禁判断依然有效（`0.5.13 > 0.5.12` 放行，
同版本重发也放行），但显示的数字会越来越旧。等启动器改造时把这一处指到 dl 的清单。

| 项 | 内容 |
| --- | --- |
| 做什么 | 火影 / 幻杀接同一套（只改配置）；安卓形态核对后接入；**启动器按对接文档改造**（属于下一期主线） |
| 产出 | 三个游戏、两个平台同一套上传 |
| 怎么验证 | 每个游戏各跑一次完整上传 + 客户端实际更新成功 |
| 状态 | P4.1 ✅ / P4.2 ✅ / P4.3 ⬜（见下） |

### P4.1 ✅ 内核去硬编码（2026-09-20）

版本标记文件名参数化成 `-VersionMarkerName`（默认仍是 `CrossingVoid.version.json`）。
产品段 / 上传目标 / 公网基址本来就是参数；Windows 排除规则与安卓过滤规则实测对幻杀同样成立。
零境侧复查：`差异 0`、`硬链接 0 · 复制 0 · 跳过 85` —— 没动到已上线的东西。

**三个游戏的命名（2026-09-20 用户确认）**：

| 游戏 | 工程根目录（UE） | dl 产品段（PC / 安卓） | 裸仓库 | 版本标记文件 |
| --- | --- | --- | --- | --- |
| 零境交错 | `C:\CrossingVoid` | `crossingvoid` / `crossingvoid-android` | `game-crossingvoid{,-android}.git` | `CrossingVoid.version.json` |
| 幻杀 | `D:\UnrealMap\FantasyProject` | `fantasyproject` / `fantasyproject-android` | `game-fantasyproject{,-android}.git` | `FantasyProject.version.json` |
| 火影 | `D:\UnrealMap\NarutoBP` | `narutobp` / `narutobp-android` | `game-narutobp{,-android}.git` | `NarutoBP.version.json` |

### P4.2 ✅ 服务端建仓库（2026-09-20）

四个新裸仓库 `game-{fantasyproject,fantasyproject-android,narutobp,narutobp-android}.git` 已建好：
同一份 hook（md5 `99cbd3f3…` 与零境那两个一致）、`gc.auto=0`、`core.trustctime=false`、属主 `uploader`。
uploader 的强制命令白名单本来就是 `/srv/repos/game-*.git`，**不需要改账号**；
`git ls-remote yecaoyun-upload:/srv/repos/game-<产品>.git` 四个都返回 0（空仓库）✅。

### P4.3 ✅ AxTools 铺开（2026-09-20）

| 改了什么 | 说明 |
| --- | --- |
| 抽共用基类 | `AxTools.Core\Adapters\GameUploadAdapter.cs`：动作集合、校验（必须先选产物目录）、参数拼装都在这里；子类只给「显示名 + 产品段名 + 版本标记文件名」 |
| 三个游戏各一个页面 | 零境 `CrossingVoidGame`（→`crossingvoid`）、幻杀 `FantasyGame`（→`fantasyproject`，从占位升级成真页面）、**火影 `NarutoGame` 新增**（导航项 + 页面 + 适配器 + 包装脚本 + 设置项） |
| 安卓段名自动推导 | 传 PC 段名，安卓固定用 `<段>-android`（零境已经这么上线，保持一致） |
| **产物目录按平台分开记** | `ManagedToolPaths.GamePackageRoot`（PC）+ `GamePackageRootAndroid`（安卓）；切平台时页面上那一格自动换成对应那份，校验也跟着重算（幻杀是 `Windows` vs `Android_ASTC`） |
| 内核参数化 | `-ProductSegment` / `-VersionMarkerName` 由适配器下发，内核不再认识任何具体游戏 |
| 包装脚本分两层 | 实现只有一份 `Scripts\Adapters\GameUpload\Invoke-GameUploadAction.ps1`；每家的 `Invoke-<游戏>Action.ps1` 是十几行转发，只带自己的段名和标记名 |
| 顺手修的两个真 bug | ① `AppSettingsService` 用枚举名 `"CrossingVoidGame"` 当设置键，而目录里的稳定键是 `"CrossingVoid"` → 多出一个没人用的空条目、迁移写错地方；② 面板上"线上版本"文案与门禁在不接线上检查的游戏上会拦死发布 |
| 测试 | 新增"三个游戏共用一套适配器"的参数化测试（段名/标记名/按平台目录校验）；顺手把 4 个"工具数=7"的老断言改成实际数量，测试从 **29 失败** 降到 **12 失败 / 321 通过**（剩下 12 个都是与本次无关的老账：AxTaskRunner 集成夹具、旧的 Release 介绍内联框、旧的分片包页面） |

关于版本门禁（用户 2026-09-20 确认）：**幻杀/火影还没上过任何清单**，所以不再硬套"目标版本必须比线上高"；
这两个游戏直接放行，由发布时的下载站公网校验兜底。零境保持原状（读 www，停在 `V0.5.12`），
等启动器那一期一起指到 dl。

### ✅ 游戏包发布入口搬进「服务器」页（2026-09-20）

用户反馈左侧那三个「游戏-*」页和「服务器」页里的同名页签分不清，于是把发布入口并进服务器分区：

| 改了什么 | 说明 |
| --- | --- |
| 左侧导航 | 去掉 `游戏-零境交错` / `游戏-幻杀` / `游戏-火影BP` 三项；`MainWindow.Navigation.cs` 里旧页签标识统一落到「服务器」（设置里存着旧值时重开不会再弹回 Ax管理器） |
| 新位置 | 「服务器」页 → 对应游戏页签（零境交错 / 火影BP / 幻杀）→ 底部卡片「客户端发布（下载站）」 |
| 实现方式 | 三个 `ToolPage` 实例只是换了宿主：启动时 `ServerAliyunPage.AttachGamePublishPanel(<产品键>, <页>)`，`ToolPage.HostAsGamePublishPanel()` 去掉重复页头 / 模式选择器 / 内层卡片边框。动作、进度、确认弹窗、日志仍走 MainWindow 里既有那套事件，**没有第二份执行逻辑** |
| 为什么挂在固定宿主 | 服务器页每 15 秒轮询会重建动作区；发布卡片挂在 `GamePublishPanelHost` 上、只在真的换页签时才动一次，否则正在填版本号时输入焦点会被刷新打断 |
| 顺带修 | 幻杀的「选择游戏包目录」按钮原先没接线（`FantasyGamePage.GamePackageRootSelectionRequested` 漏订阅），已补上 |
| 测试 | 新增一条断言覆盖这次搬家；全量 334 项 → **11 失败 / 323 通过**，11 条仍是老账：AxTaskRunner 集成夹具 5、旧的 Release 介绍内联框 3、旧的分片包页面 2、ReleaseConfiguration 1 |

### ✅ 发布面板收尾：去渠道、修线上版本检查、三游戏统一（2026-09-20）

| 改了什么 | 说明 |
| --- | --- |
| 撤掉「游戏通道」 | 目前没余力开测试服，发布面板上那个下拉（正式服/测试服）去掉；`GamePublishChannel` 一律按 `stable` 走（内核用它决定 `productKey` 是 `<产品段>-game` 而不是 `-game-test`），以后要开测试服时界面和状态一起加回来 |
| 修回线上版本检查 | 嵌进服务器页以后没人触发它，一直停在"尚未检查"。现在**进对应页签就自动查一次**（`ServerPage.CheckGameVersionAsync`），结果同时写进输出日志 |
| 检查源换成下载站 | 以前零境读 www 的 `manifests/game/*-latest.json`（停在 V0.5.12），现在三个游戏统一读**下载站清单目录**：`/games/<产品段>[-android]/manifests/`，取目录里版本号最高的一份（发布内核只留当前版本）。实测：零境 PC/安卓都是 `0.5.13`，幻杀安卓 `0.1.1`，幻杀 PC 与火影还没发过 |
| 门禁按平台 | 不再要求"PC 与安卓版本必须一致"（幻杀 PC 还没发过会被误拦），改成**只看当前选中平台**：目标版本低于该平台线上版本才拦；读不到清单（网络/代理）时不再拦死，放行并由发布内核的公网校验兜底 |
| 火影/幻杀统一 | 三个游戏各有一份发布状态（版本号 / Release 介绍 / 线上版本），默认 Release 介绍不再写死"零境交错"，改成各自的产品名 |
| 测试 | 新增/改写 6 条断言覆盖：目录索引取最高版本、未发布平台、双端一致、按平台门禁、读不到时放行、产品段经接口取到；全量 337 项 → **11 失败 / 326 通过**，11 条仍是老账（AxTaskRunner 集成夹具 5、旧的 Release 介绍内联框 3、旧的分片包页面 2、ReleaseConfiguration 1） |

### ✅ 游戏包发布第一次真跑：`-ProjectRoot` 绑定失败（2026-09-20 晚）

第一次点「发布新版本」直接挂了，日志只有一行乱码：`Invoke-CrossingVoidGameAction.ps1: 无法将参数绑定到参数"ProjectRoot"…`。根因两条：

| 问题 | 说明 | 修法 |
| --- | --- | --- |
| `-ProjectRoot` 传的是空字符串 | 协议里那个参数来自 `paths.SourceRoot`，而**游戏工具根本没有源码目录**（它们的"项目"是产物目录），空字符串撞上 `[Parameter(Mandatory)][string]` 就在绑定阶段失败——脚本一行都没跑 | `ManagedToolAdapterBase.ResolveProjectRootArgument` 做成虚方法，`GameUploadAdapter` 覆盖成**当前平台的产物目录**（PC/安卓各一个） |
| 报错是乱码、看不出原因 | PowerShell **宿主层**的错（绑定失败/语法错/脚本不存在）用系统本地代码页（GBK）写 stderr，而 AxTools 按 UTF-8 读；脚本**体内** `throw` 反而走 UTF-8 协议，是好的 | ① 游戏四个脚本的参数改成"可空 + 体内校验"，报错走协议；② `PowerShellProcessHost` 改成按字节读行：UTF-8 解不出就按系统 ANSI 回退 |
| 修完上面又露出第二层：`找不到与参数名称 'ProductSegment' 匹配的参数` | "本游戏是谁"被下发了两遍——适配器和包装脚本各带一份，而包装脚本的 param 里没有这个参数（它的注释明确写着"只带本游戏的产品段名"）。同一份信息两处下发迟早打架 | 改成**包装脚本单一来源**：适配器不再下发 `-ProductSegment` / `-VersionMarkerName`；加测试钉住三个包装脚本里的常量，并要求段名与适配器给"线上版本检查"用的那份一致 |

顺带把 `AxTaskRunnerIntegrationTests` 的路径少退一级修好（它指向了 `AxTools.Tests\Scripts`）：5 条集成测试**全部转绿**，其中一条正好断言"中文 UTF-8 输出能原样带回来"。全量：**331 通过 / 6 失败**（剩下 6 条是老账：旧的 Release 介绍内联框 3、旧的分片包页面 2、ReleaseConfiguration 1）。

### ✅ 传输换成「逐文件同步」，git 退场（2026-09-20）

用户拍板："git 就完全不用了，直接删掉省空间"。于是：

**1) 新的传输内核（files 模式）**——`Publish-GameDownloadRelease.ps1` 加 `-TransferMode files`（默认）
＋服务端脚本 `/srv/scripts/game-file-sync.sh`：

| 步骤 | 做什么 |
| --- | --- |
| 比对 | 拉线上 `latest.json` → 上一版清单，逐文件比 sha256 ⇒ 只挑"和上一版不一样的文件" |
| 准备 | `game-file-sync.sh prepare`：服务端 `cp -al` 把上一版**硬链接**成新版本目录（不额外占空间） |
| 上传 | `sftp -b`：变化文件先传 `.axtmp` 再原子 `rename`；**清单最后传**（启动器读不到半截文件） |
| 收尾 | `game-file-sync.sh finalize`：归档上一版（硬链接）→ 树里只留当前版 → 写 `games/<段>/latest.json` |
| 校验 | 服务端 `sha256sum` 核这次传过的文件，再走原有公网抽样校验 |

实测：探针段 v2（内容一字未改）只传 **1 个文件（版本标记，0 KB）**，其余 2 个硬链接；v3（改一个文件）传 2 个。
**真实零境 PC 0.5.14：上传 1 个文件 + 清单，公网校验 86 条通过** ✅（以前是每次 2.5 GB）

**2) 空间**：本机三个暂存 `.git`（4.46 GB）＋服务端六个 `game-*.git`（8.6 GB）删除，合计约 13 GB；
对外 `/srv/downloads` 仍是 5.1 GB（硬链接不翻倍）。`-TransferMode git` 代码保留但不再使用。

> 为什么必须换掉 git：本机暂存仓库里 `git pack-objects` 就是不排除服务端已有的对象——`rev-list` 算得对（8 个），
> 打包却输出 141 个 / 2.33 GiB；换 LF 干净的 revs、关 pack 复用、甚至硬链接克隆一个新仓库，结果一样。
> 逐文件同步完全不依赖这个行为。

### P4.4 🔄 每个游戏跑一次真机发布

| 产品 | 状态 |
| --- | --- |
| 幻杀 · 安卓 | ✅ 2026-09-20 跑通：106 MB（apk 67.4 MB + obb 43.8 MB），清单 3 条（含 `FantasyProject.version.json`），全新裸仓库落位 + 回收 + 公网校验通过 → `https://dl.crossingvoid.top/games/fantasyproject-android/0.1.0/` |
| 幻杀 · 安卓（**chunk 化**） | ✅ 2026-09-20 跑通 `0.1.1`：不再发 OBB 本体，改发 **OBB 里的 3 个条目** + `FantasyProject.obb.json` 旁车，清单 6 条；公网抽样 4 个文件通过 → `/games/fantasyproject-android/0.1.1/`（旧 `0.1.0` 已归档） |
| 幻杀 · PC | ⬜ 产物就位（`F:\DaBaoV\Fantasy\Windows` 1.9 GB），等一次发布 |
| 火影 · PC / 安卓 | ⬜ 还没有打包产物（工程在 `D:\UnrealMap\NarutoBP`，版本号 `0.1`）；页面上产物目录本来也是空的 |
| 零境 · PC / 安卓 | ✅ P3 已跑通 |

### 幻杀 / 火影 的产物位置（2026-09-20 搬运后）

| 游戏 | 产物根 | 已知内容 |
| --- | --- | --- |
| 幻杀 | `F:\DaBaoV\Fantasy` | `Windows\`（1.9 GB，26 文件）、`Android_ASTC\`（2.31 GB，8 文件，真正要发的只有 apk 64 MB + obb 42 MB）、`Server\`（2.75 GB 的旧服务端测试包，其 `WindowsServer\` 是空的） |
| 火影 | `F:\DaBaoV\NarutoBP` | 空目录（还没有打包产物，先按推测预留） |

搬运用的是 robocopy + **逐文件 sha256 校验后再删原件**（幻杀三个目录 79 个文件全部一致）。
幻杀没有任何本地脚本引用这些目录（`FantasyProject\Config\DefaultGame.ini` 里那两个
`ServerProgramPath` 指的是**阿里云远端**的 `C:\Users\Administrator\Desktop\幻杀_Server\…`），所以搬动不影响任何流程。

## 明确不做（本期）

- UE patch pak（`_P`）：暂缓，理由是 chunk 已承担 patch 职责；打包脚本的 `-Mode Patch` 与契约里的 `patches` 字段**保留不删**（触发条件见设计文档）
- chunk 怎么分：**用户自己分**，插件侧不动
- 游戏内分片下载器（`BuildManifest`，服务器日志里那批 404 的来源）：本期不碰
- 启动器"边下边装 / 省空间"：下一期

## 相关文档

- 设计定稿：`2026-09-20-game-upload-design.md`
- 启动器对接契约：`2026-09-20-game-upload-launcher-contract.md`
- 服务端更新（已实现、可参考同一套 git 做法）：`2026-09-18-server-git-update-design.md`
- 安卓分包报告待办：`C:\CrossingVoid\Plugins\CrossingChunkEditor\Docs\2026-09-20-待办-安卓分包报告.md`

## 侦察基线（2026-09-20 只读实测）

| 项 | 值 |
| --- | --- |
| 主机 | SSH 别名 `yecaoyun-hk`（`~/.ssh/config`）→ `207.57.125.218`，用户 `root`，密钥 `yecaoyun_hk` |
| 系统 | Debian 12 (bookworm) / 内核 6.1 cloud / **2 核 2 GB 内存** |
| 磁盘 | 30 GB 盘，已用 1.8 GB，**余 27 GB** |
| 已装 | nginx 1.22.1（active + enabled）、certbot（`dl.crossingvoid.top` 证书有效至 2026-12-16）、curl |
| 未装 | **git、rsync、zip/unzip、ossutil 都没有** |
| nginx 站点 | `/etc/nginx/sites-enabled/dl` → `root /srv/downloads`；autoindex 开；`limit_rate_after 64m` + `limit_rate 8m` + `limit_conn perip 6`；**没有任何 Cache-Control 头**（清单不缓存这条还没做） |
| `/srv/downloads` | 只有 `speedtest.bin`(200 MB) + `test-50m.bin`(50 MB)，无真实内容 |
| 账号 | 只有 root 能登（`authorized_keys` 1 条，注释 `workbuddy-yecaoyun-hk`）；**无 uploader** |
| 公网 | `dl.crossingvoid.top → 207.57.125.218`，HTTPS HEAD 实测 `200` |
| 清单现状 | 发布走 Gitee API（contents）→ 镜像到 www；启动器读 www 的 `manifests/game/{windows,android}-latest.json`，校验 `schemaVersion` + `productKey` |
| 清单两种形态 | `schemaVersion=1` = 文件级（`files[]` 带 `sha256`，**已存在但没人用**）；`schemaVersion=2` = 整体 zip + 500 MB 字节切片（**启动器只认 2**） |
| 现有大文件落点 | 阿里云 OSS `download-server-xj` + GitHub Release `PC-<版本>`；野草云还没被用起来 |

## 待查疑点（决定文件级增量的收益）

2026-09-20 重打包实验：用户重打包后，**三个 pak 容器大小完全不变、内容却全变了**
（`pakchunk0` 916 MB、`pakchunk2` 110 MB、`pakchunk3` 234 MB），于是第二次 push 传了 1.12 GiB。

两种可能，含义完全不同：

| 可能 | 含义 | 下一步 |
| --- | --- | --- |
| A 内容/规则真变了 | 1.12 GiB 合理，且印证"改基础包＝重下 900 MB" | 按计划把容器切细（用户自己分） |
| B 什么都没改、字节也不同（IoStore 非确定性） | 文件级增量收益被吃掉 | 需要新策略：切细容器 / 上 patch pak / 找确定性开关 |

**判定方法**：什么都不改，再打一次包，比对容器 SHA256：

- 全一致 → A（也可能是这次没触发重 cook）；至少说明"相同输入 → 相同产物"成立
- 有差异 → B，需要进一步定位是 cook 还是 IoStore 写入阶段引入的

**还需要问用户**：这次重打包之前改了哪些东西（改图 / 改 chunk 规则 / 只编译插件 / 什么都没改）。

**用户 2026-09-20 的决定**：chunk 先保持现状，**后面改内容时顺带做这个测试**（所以这一条不算阻塞，
留在本轮之后观察；P3 的验收②「无改动再点一次 → push 0 字节」也能顺带给出证据）。

**P3 之后的新证据**：内核现在每次都会拿"新扫描的 sha256"和"仓库里那一版的清单"逐条比，
在日志里直接给出 `新增 / 变化 / 删除` 和"按清单口径需要传输多少"。也就是说，
以后不用再猜——**重打包后哪个容器真的变了，点一次「只检查，不发布」就能看见**（不联网）。
另外实测确认：同一份产物重复发布 = 0 字节；内容变了才会传（PC 那次传的 1.12 GiB
正好是重新打包过的基础包那几个容器）。

## 本轮结束时已知的测试现状（2026-09-20）

`AxTools.Tests` 在 P3 开始前就是 **29 个失败**（仓库里留着上一轮"空界幻境"页面改造的尾巴，
与游戏上传无关）。P3 把其中 8 个**和游戏发布直接相关**的失败修掉了（改的是断言本身已经过时的部分），
现在是 **21 个失败 / 312 通过**，剩下这些仍然与本次无关：

- `ApplicationViewModelTests`（3）：工具数/旧页面迁移的期望还停在 7 个工具
- `AppSettingsServiceTests`（3）：同上，"七个工具条目"
- `ManagedToolCatalogTests` / `ManagedToolAdapterTests.Catalog_ReturnsSevenAdapters…`（2）：同上
- `AxTaskRunnerIntegrationTests`（5）：测试用的临时脚本路径找不到
- `CrossingVoidPackagePageXamlTests`（2）：旧的"游戏分片包"页面早就改造过
- `ToolPageXamlLayoutTests` 里 4 个：Release 介绍从"内联 TextBox"改成了"按钮 + 弹窗"
- `ManagedToolAdapterTests.DevelopmentLaunchActions…`、`ReleaseConfigurationTests`（2）

顺手记一个**真实存在的旧 bug**（本次没动）：`Scripts\Adapters\FantasyGame\…` 与
`Scripts\Adapters\FantasyAndroid\…` 两个占位适配器没有声明 `-DevelopmentExecutable` /
`-ReleaseExecutable`，而任务协议一定会传这两个参数 → 它们的「检查环境」按钮点了必然报
`A parameter cannot be found`。（零境那个适配器本次已经补上了。）
