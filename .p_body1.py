from pathlib import Path
import re

def edit(path, pairs):
    p = Path(path)
    raw = p.read_text(encoding='utf-8-sig')
    crlf = '\r\n' in raw
    t = raw.replace('\r\n', '\n')
    for old, new in pairs:
        assert old in t, (path, old[:60])
        t = t.replace(old, new, 1)
    if crlf:
        t = t.replace('\n', '\r\n')
    p.write_text(t, encoding='utf-8')

# 1) 新枚举值：生成本体碎片
edit('AxTools.Core/Models/ManagedToolAction.cs', [
    ('''    GenerateGamePatchFragments,''',
     '''    GenerateGamePatchFragments,
    GenerateGameBodyFragments,'''),
])

# 2) 描述
edit('AxTools.Core/Models/ManagedToolActionDescriptionCatalog.cs', [
    ('''        ManagedToolAction.PackageStable => "生成正式通道分发包，只写入 AxTools 工作区。",''',
     '''        ManagedToolAction.GenerateGameBodyFragments => "把碎片目录里的本体碎片清单发布到下载站，只发清单、不发包。",
        ManagedToolAction.PackageStable => "生成正式通道分发包，只写入 AxTools 工作区。",'''),
])

# 3) 适配器：动作定义 + 参数下发白名单
edit('AxTools.Core/Adapters/GameUploadAdapter.cs', [
    ('''            Action(ManagedToolAction.GenerateGamePatchFragments, "生成补丁碎片", ManagedToolActionSection.Publish, "\\uE7B8", true, true)''',
     '''            Action(ManagedToolAction.GenerateGamePatchFragments, "生成补丁碎片", ManagedToolActionSection.Publish, "\\uE7B8", true, true),
            Action(ManagedToolAction.GenerateGameBodyFragments, "生成本体碎片", ManagedToolActionSection.Publish, "\\uE898", true, true)'''),
    ('''                ManagedToolAction.GenerateGamePatchFragments or''',
     '''                ManagedToolAction.GenerateGamePatchFragments or
                ManagedToolAction.GenerateGameBodyFragments or'''),
])

# 4) VM：按钮启用 + 映射
edit('AxTools.Core/ViewModels/ToolPageViewModel.cs', [
    ('''        new("body-fragments", "生成本体碎片", "\\uE898") { IsEnabled = false }''',
     '''        new("body-fragments", "生成本体碎片", "\\uE898")'''),
    ('''        "patch-fragments" => ManagedToolAction.GenerateGamePatchFragments,''',
     '''        "patch-fragments" => ManagedToolAction.GenerateGamePatchFragments,
        "body-fragments" => ManagedToolAction.GenerateGameBodyFragments,'''),
])

print('ok')
