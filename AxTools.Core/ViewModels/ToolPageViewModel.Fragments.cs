using AxTools.Core.Catalog;
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
    // 「碎片制作」是 ToolPage 里的独立页签；支持哪些页面由 GameProfileCatalog 决定。
    // 现在零境 PC/安卓、幻杀 PC/安卓各一条；以后火影接上 profile 后自动出现。

    /// <summary>当前页对应的游戏档案；非碎片页返回 null。</summary>
    private GameProfile? FragmentProfile => GameProfileCatalog.FindByToolKey(Descriptor.Key);

    /// <summary>这个工具页有没有《生成碎片》。</summary>
    public bool HasFragmentGeneration => FragmentProfile is not null;

    private bool IsAndroidFragmentPage =>
        FragmentProfile is { } profile && Descriptor.Key == profile.AndroidToolKey;

    /// <summary>包名/发布用的平台字样：<c>PC</c> / <c>安卓</c>。</summary>
    public string FragmentPlatformLabel => IsAndroidFragmentPage ? "安卓" : "PC";

    /// <summary>下载站上的产品段：零境 <c>crossingvoid</c>，幻杀 <c>fantasyproject</c>；安卓加 -android。</summary>
    public string FragmentProductSegment =>
        FragmentProfile?.ProductSegmentFor(IsAndroidFragmentPage) ?? string.Empty;

    /// <summary>
    /// 版本标记/旁车用的渠道：游戏包发布是 Test 就写 test，其余一律 stable
    /// （发布脚本只认这两个值；test 的 productKey 还会多一个 <c>-test</c> 后缀）。
    /// </summary>
    public string FragmentChannel =>
        CrossingVoidVersionMarker.NormalizeChannel(_gamePublishPaths?.GamePublishChannel);

    /// <summary>安卓旁车里的 productKey；PC 用不到，但保持同一份口径。</summary>
    public string FragmentProductKey => FragmentProfile is null
        ? string.Empty
        : CrossingVoidAndroidObbSidecar.BuildProductKey(FragmentProductSegment, FragmentChannel);

    /// <summary>包名前缀里的游戏短名：零境 / 幻杀。</summary>
    public string FragmentGameShortName => FragmentProfile?.ShortName ?? "零境";

    public string FragmentGenerationActionText =>
        IsAndroidFragmentPage ? "碎片生成" : $"{FragmentGameShortName}碎片生成";

    /// <summary>
    /// 碎片里版本标记和包名用的游戏版本 —— 读**当前游戏包发布**那份，
    /// 不是启动器自己的 <c>PublishVersion</c>（那是启动器版本，和游戏版本不是一回事）。
    /// </summary>
    public string FragmentGameVersion =>
        _gamePublishPaths?.GamePublishVersion?.Trim() ?? string.Empty;

    /// <summary>
    /// 基础碎片的名字前缀，例如 <c>零境V0.6PC</c> / <c>幻杀V0.1安卓</c>
    /// （版本只取主/次版，补丁版本不混进基础碎片）。
    /// </summary>
    public string FragmentArchiveNamePrefix =>
        CrossingVoidFragmentPlanner.BuildArchiveNamePrefix(
            FragmentGameVersion,
            FragmentPlatformLabel,
            FragmentGameShortName);

    public string FragmentGenerationDescription =>
        IsAndroidFragmentPage
            ? "从 OBB 里读出散件，和主 APK 一起切成「安卓安装包 + 安卓基础碎片」（每片最高 1 GiB）。" +
              "必须用启动器导入：它会按清单组装 OBB 并安装 APK，不能手动解压。生成前会清空输出目录。"
            : "把打包产物按大小切成若干 PC 基础碎片（每片最高 1 GiB，独立 zip，内部保持原目录树）。" +
              $"文件名形如 {FragmentArchiveNamePrefix}基础碎片001.zip；" +
              "全部解压到同一个游戏安装目录即可，不需要合并。生成前会清空输出目录。";

    /// <summary>
    /// 碎片从哪个目录生成 —— **直接复用「服务器 → 当前游戏」那份游戏包目录**
    /// （PC / 安卓各记一个），不在碎片制作里再填一遍。
    ///
    /// 启动器页本来都不显示"游戏包发布"那一区，所以这份引用是注入进来的：
    /// PC 页读 PC 那份、安卓页读安卓那份，用户在服务器那边选过一次，两边就一直是同一个源。
    /// </summary>
    public string FragmentGameDirectory => _gamePublishPaths is null
        ? string.Empty
        : IsAndroidFragmentPage
            ? _gamePublishPaths.GamePackageRootAndroid
            : _gamePublishPaths.GamePackageRoot;

    public string FragmentSourceText => string.IsNullOrWhiteSpace(FragmentGameDirectory)
        ? $"还没有游戏包目录 —— 先到「服务器 → {FragmentProfile?.DisplayName ?? "零境交错"}」的游戏包发布里选一次。"
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

    /// <summary>两个页都有《发布碎片清单》：PC 传 PC 的清单，安卓传安卓的清单。</summary>
    public bool HasFragmentManifestPublishing => HasFragmentGeneration;

    /// <summary>
    /// 能不能发布碎片清单：两个页走同一条脚本，靠产品段区分 PC / 安卓。
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
        OnPropertyChanged(nameof(FragmentProductSegment));
        OnPropertyChanged(nameof(FragmentGameShortName));
        OnPropertyChanged(nameof(FragmentPlatformLabel));
        OnPropertyChanged(nameof(FragmentGenerationActionText));
        OnPropertyChanged(nameof(FragmentArchiveNamePrefix));
        OnPropertyChanged(nameof(FragmentChannel));
        OnPropertyChanged(nameof(FragmentProductKey));
        OnPropertyChanged(nameof(FragmentGenerationDescription));
        OnPropertyChanged(nameof(CanGenerateFragments));
        OnPropertyChanged(nameof(CanPublishFragmentManifest));
    }

    /// <summary>默认落在 D 盘（用户指定的位置）；D 盘不在就退到游戏包目录旁边，至少同一块盘。</summary>
    private string DefaultFragmentOutputDirectory()
    {
        var profile = FragmentProfile;
        var folderName = profile is null
            ? (IsAndroidFragmentPage ? "零境交错安卓碎片" : "零境交错PC碎片")
            : (IsAndroidFragmentPage ? profile.AndroidFragmentDirectoryName : profile.PcFragmentDirectoryName);
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
