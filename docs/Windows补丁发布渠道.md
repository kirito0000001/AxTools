# Windows 补丁发布渠道（测试）

## 版本来源

游戏版本只认打包插件产出的 `Version.json`，位于包根或上一级。AX 不再允许手填版本号。

```json
{
  "version": "0.6.2",
  "mode": "Patch",
  "releaseVersion": "0.6",
  "platform": "Win64",
  "artifactDir": "Windows",
  "files": [
    { "path": "Windows\\CrossingVoid\\Content\\Paks\\pakchunk0-Windows_0_P.ucas", "size": 87488 }
  ]
}
```

完整发布与补丁发布都会用这里的 `version`；发布脚本自身也会再次读取，命令行传入的旧版本会被覆盖。

## 补丁渠道

AX 的「发布 Windows 补丁」只处理 `mode=Patch` 的 `Version.json`，并读取当前线上全量 manifest 作为基线：

- 插件 `Version.json.files` 列出的内容补丁；
- 再对比线上基线，自动补上所有实际变化的程序文件（例如 `CrossingVoid-Win64-Shipping.exe`、DLL 和 Manifest）。

产物放 GitHub Release：

- `PC-Patch-V<version>`
  - `patch.json`：补丁清单（路径已去掉 `Windows\` 前缀，相对游戏根目录）
  - `<产品段>-Windows-Patch-<version>.zip`：补丁包，包含 `patch.json`、`Version.json` 和补丁文件
- `PC-Patch-Latest`
  - `patch-latest.json`：固定指针，包含 `version/baseVersion/manifestUrl/packageUrl/packageSizeBytes/packageSha256`

启动器可以先读固定指针：

```text
https://github.com/<repo>/releases/download/PC-Patch-Latest/patch-latest.json
```

再按 `packageUrl` 下载补丁包，解压到游戏根目录并按 `patch.json` 的 SHA-256 校验。

## 边界

- 目前只支持 Windows；Android 补丁渠道未实现。
- 补丁必须能读到线上基线 manifest；读取失败会直接拒绝发布，避免生成缺程序文件的假补丁。
- 补丁体积取决于代码变化：本次 0.6.2 因包含新 `Shipping.exe`，补丁包约 68 MB；纯内容补丁仍可能只有几 MB。
- 同一基线可以生成 `_0_P`、`_1_P` 等；发布器按文件级 SHA-256 判断，新文件才会被下载。
- GitHub CLI 必须已登录；未登录时发布器会明确拒绝。
