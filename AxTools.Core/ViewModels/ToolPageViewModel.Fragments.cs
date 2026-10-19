using AxTools.Core.Models;
using AxTools.Core.Services;

namespace AxTools.Core.ViewModels;

/// <summary>
/// 《生成碎片》/《发布碎片》的页面状态、文案和进度文字。
///
/// **从 ToolPageViewModel 主文件拆出来**：碎片是独立功能，主文件只留通用的工具页状态
/// （路径、版本、动作可用性）；以后安卓拆 OBB、或者加别的游戏碎片入口，只动这个文件。
/// </summary>
public sealed partial class ToolPageViewModel
{
    private bool _isFragmentRunning;
    private bool _isFragmentPreparing;
    private string _fragmentLastResultText = "尚未生成碎片。";

    // ==================== 《生成碎片》 ====================
    //
    // 只有零境的两个工具页有这一块：
    //   · TFAC平台页（PC 产物）      → 《零境碎片生成》
    //   · 平台-零境-安卓（安卓产物）  → 《碎片生成》
    // TFAC 那个按钮必须带"零境"：那个页面以后还要放别的游戏，不写清楚就分不出是谁的。
    // 安卓页自己就是零境专用的，所以不用重复。

    /// <summary>这个工具页有没有《生成碎片》。</summary>
    public bool HasFragmentGeneration =>
        Descriptor.Key is ManagedToolKey.CrossingVoidPc or ManagedToolKey.CrossingVoidAndroid;

    private bool IsAndroidFragmentPage => Descriptor.Key is ManagedToolKey.CrossingVoidAndroid;

    /// <summary>
    /// 碎片产物的形态：PC 按体积切片（每片一个独立 zip），安卓暂时仍是"三组"旧机制
    /// （它的散件在 OBB 里，等 OBB 拆解做完再切到 <see cref="CrossingVoidFragmentLayout.SizeSlices"/>）。
    /// </summary>
    public CrossingVoidFragmentLayout FragmentLayout =>
        IsAndroidFragmentPage
            ? CrossingVoidFragmentLayout.ThreeGroups
            : CrossingVoidFragmentLayout.SizeSlices;

    public string FragmentGenerationActionText => IsAndroidFragmentPage ? "碎片生成" : "零境碎片生成";

    public string FragmentGenerationTitle => $"{FragmentGenerationActionText}";

    /// <summary>
    /// 碎片里版本标记和包名用的游戏版本 —— 读**游戏包发布**那份（<c>CrossingVoid</c>），
    /// 不是启动器自己的 <c>PublishVersion</c>（那是 1.4.x，和游戏 0.6.x 不是一回事）。
    /// </summary>
    public string FragmentGameVersion =>
        _gamePublishPaths?.GamePublishVersion?.Trim() ?? string.Empty;

    /// <summary>
    /// 基础碎片的名字前缀，例如 <c>零境V0.6</c>（版本只取主/次版，补丁版本不混进基础碎片）。
    /// </summary>
    public string FragmentArchiveNamePrefix =>
        CrossingVoidFragmentPlanner.BuildArchiveNamePrefix(FragmentGameVersion);

    public string FragmentGenerationDescription =>
        FragmentLayout == CrossingVoidFragmentLayout.SizeSlices
            ? "把打包产物按大小切成若干基础碎片（每片最高 1 GiB，独立 zip，内部保持原目录树）。" +
              $"文件名形如 {FragmentArchiveNamePrefix}基础碎片001.zip；" +
              "全部解压到同一个游戏安装目录即可，不需要合并。生成前会清空输出目录。"
            : "把打包产物拆成三份：分片散件（保持目录树）+ 影片各一个压缩包 + 其余文件一个压缩包。" +
              "分片不压缩是因为它本身已经是压过的；影片不压缩是因为再压一遍只省 0.0%。" +
              "生成完自己发到网盘或群里就行。";

    /// <summary>
    /// 碎片从哪个目录生成 —— **直接复用「服务器 → 零境交错」那份游戏包目录**
    /// （PC / 安卓各记一个），不在碎片区再填一遍。
    ///
    /// TFAC平台页和安卓页本来都不显示"游戏包发布"那一区，所以这份引用是注入进来的：
    /// PC 页读 PC 那份、安卓页读安卓那份，用户在服务器那边选过一次，两边就一直是同一个源。
    /// </summary>
    public string FragmentGameDirectory => _gamePublishPaths is null
        ? string.Empty
        : IsAndroidFragmentPage
            ? _gamePublishPaths.GamePackageRootAndroid
            : _gamePublishPaths.GamePackageRoot;

    public string FragmentSourceText => string.IsNullOrWhiteSpace(FragmentGameDirectory)
        ? "还没有游戏包目录 —— 先到「服务器 → 零境交错」的游戏包发布里选一次。"
        : FragmentGameDirectory;

    public string FragmentOutputDirectory
    {
        get => string.IsNullOrWhiteSpace(_paths.FragmentOutputDirectory)
            ? DefaultFragmentOutputDirectory()
            : _paths.FragmentOutputDirectory;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(
                _paths.FragmentOutputDirectory,
                normalized,
                _paths,
                static (model, newValue) => model.FragmentOutputDirectory = newValue))
            {
                OnPropertyChanged(nameof(CanGenerateFragments));
                OnPropertyChanged(nameof(CanPublishFragmentManifest));
            }
        }
    }

    public string FragmentLastResultText
    {
        get => _fragmentLastResultText;
        private set => SetProperty(ref _fragmentLastResultText, value);
    }

    public bool IsFragmentRunning
    {
        get => _isFragmentRunning;
        private set
        {
            if (SetProperty(ref _isFragmentRunning, value))
            {
                OnPropertyChanged(nameof(CanGenerateFragments));
                OnPropertyChanged(nameof(CanPublishFragmentManifest));
            }
        }
    }

    /// <summary>
    /// 正在获取版本标记（网络阶段）。进度条走全局那一条（<c>GlobalProgress</c>），
    /// 这里只负责禁用按钮、把"在忙"写到页面文字上，避免用户重复点。
    /// </summary>
    public bool IsFragmentPreparing
    {
        get => _isFragmentPreparing;
        private set
        {
            if (SetProperty(ref _isFragmentPreparing, value))
            {
                OnPropertyChanged(nameof(CanGenerateFragments));
                OnPropertyChanged(nameof(CanPublishFragmentManifest));
            }
        }
    }

    public bool CanGenerateFragments =>
        HasFragmentGeneration &&
        !IsFragmentRunning &&
        !IsFragmentPreparing &&
        !IsRunning &&
        !string.IsNullOrWhiteSpace(FragmentGameDirectory) &&
        !string.IsNullOrWhiteSpace(FragmentOutputDirectory);

    /// <summary>碎片清单发布只接了 PC 这条链路（安卓还要先拆 OBB），安卓页整块按钮不出现。</summary>
    public bool HasFragmentManifestPublishing =>
        HasFragmentGeneration && !IsAndroidFragmentPage;

    /// <summary>
    /// 能不能发布碎片清单：只有 PC 页有这条链路。
    /// 清单存在可以直接发；清单不存在但目录里有这一版的碎片包时也允许 ——
    /// 发布脚本会按现有 zip 读中央目录重建一份清单，省掉重新压 2.5 GB。
    /// </summary>
    public bool CanPublishFragmentManifest =>
        HasFragmentManifestPublishing &&
        _isRunnerAvailable &&
        !IsFragmentRunning &&
        !IsFragmentPreparing &&
        !IsRunning &&
        !string.IsNullOrWhiteSpace(FragmentGameVersion) &&
        HasFragmentPackageOrManifest();

    private bool HasFragmentPackageOrManifest()
    {
        if (string.IsNullOrWhiteSpace(FragmentOutputDirectory) ||
            !Directory.Exists(FragmentOutputDirectory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(
            FragmentOutputDirectory,
            CrossingVoidFragmentManifestWriter.FileName)))
        {
            return true;
        }

        var pattern = $"{FragmentArchiveNamePrefix}基础碎片*.zip";
        return Directory.EnumerateFiles(FragmentOutputDirectory, pattern).Any();
    }

    public void BeginFragmentManifestPublishing()
    {
        IsFragmentRunning = true;
        FragmentLastResultText = "正在发布碎片清单…";
    }

    public void CompleteFragmentManifestPublishing(string resultText)
    {
        IsFragmentRunning = false;
        FragmentLastResultText = string.IsNullOrWhiteSpace(resultText) ? "已结束。" : resultText;
    }

    /// <summary>进入"获取版本标记"阶段：按钮禁用，页面文字显示在忙。</summary>
    public void BeginFragmentPreparation(string message)
    {
        IsFragmentPreparing = true;
        FragmentLastResultText = string.IsNullOrWhiteSpace(message) ? "正在准备…" : message;
    }

    public void EndFragmentPreparation(string resultText)
    {
        IsFragmentPreparing = false;
        FragmentLastResultText = string.IsNullOrWhiteSpace(resultText) ? "尚未生成碎片。" : resultText;
    }

    public void BeginFragmentGeneration()
    {
        IsFragmentRunning = true;
        FragmentLastResultText = "正在生成碎片…";
    }

    /// <summary>任务推进时把阶段文字推到页面上（百分比走全局进度条）。</summary>
    public void ReportFragmentProgress(CrossingVoidFragmentProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        FragmentLastResultText = progress.Message;
    }

    public void CompleteFragmentGeneration(string resultText)
    {
        IsFragmentRunning = false;
        FragmentLastResultText = string.IsNullOrWhiteSpace(resultText) ? "已结束。" : resultText;
        // 生成成功时 fragments.json 刚出现，"发布碎片"按钮要跟着亮起来。
        OnPropertyChanged(nameof(CanPublishFragmentManifest));
    }

    /// <summary>游戏包目录在别处（服务器页）被选过之后，把这一块跟着刷新一下。</summary>
    public void RefreshFragmentBindings()
    {
        OnPropertyChanged(nameof(FragmentGameDirectory));
        OnPropertyChanged(nameof(FragmentSourceText));
        OnPropertyChanged(nameof(FragmentOutputDirectory));
        OnPropertyChanged(nameof(FragmentGameVersion));
        OnPropertyChanged(nameof(FragmentArchiveNamePrefix));
        OnPropertyChanged(nameof(FragmentGenerationDescription));
        OnPropertyChanged(nameof(CanGenerateFragments));
        OnPropertyChanged(nameof(CanPublishFragmentManifest));
    }

    /// <summary>默认落在 D 盘（用户指定的位置）；D 盘不在就退到游戏包目录旁边，至少同一块盘。</summary>
    private string DefaultFragmentOutputDirectory()
    {
        var folderName = IsAndroidFragmentPage ? "零境交错安卓碎片" : "零境交错PC碎片";
        const string preferredRoot = @"D:\";
        if (Directory.Exists(preferredRoot))
        {
            return Path.Combine(preferredRoot, folderName);
        }

        var gameDirectory = FragmentGameDirectory;
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            try
            {
                var fullPath = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    return Path.Combine(parent, folderName);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 目录格式坏了就落到临时目录，反正下面还会让用户改。
            }
        }

        return Path.Combine(Path.GetTempPath(), folderName);
    }
}
