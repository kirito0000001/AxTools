using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class StorageCatalogService
{
    private readonly SharedCacheRoots _sharedRoots;
    private readonly UnrealProjectRoots _unrealRoots;

    public StorageCatalogService(
        SharedCacheRoots? sharedRoots = null,
        UnrealProjectRoots? unrealRoots = null)
    {
        _sharedRoots = sharedRoots ?? new SharedCacheRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath());
        _unrealRoots = unrealRoots ?? new UnrealProjectRoots(
            @"D:\UnrealMap\CrossingVoid",
            @"D:\UnrealMap\FantasyProject");
    }

    public IReadOnlyList<StorageCandidate> CreateCandidates(AppSettings settings)
    {
        var candidates = new List<StorageCandidate>();
        AddAxToolsCandidates(settings, candidates);
        AddFantasyToolsCandidates(settings, candidates);
        AddGalExcleToolsCandidates(settings, candidates);
        AddCrossingVoidZDToolCandidates(settings, candidates);
        AddFantasyProjectPcCandidates(settings, candidates);
        AddCrossingCandidates(settings, candidates);
        AddCrossingAndroidCandidates(settings, candidates);
        AddAndroidSdkCandidates(settings, candidates);
        AddSharedCacheCandidates(candidates);
        AddUnrealCandidates(candidates);

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            Add(
                candidates,
                "AxTools",
                "更新下载缓存",
                Path.Combine(localAppData, "AxTools", "Updates"),
                StorageCategory.SafeTemporary,
                "删除后会重新下载尚未安装的更新包。");
        }

        return candidates
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void AddUnrealCandidates(ICollection<StorageCandidate> candidates)
    {
        Add(candidates, "UnrealGlobal", "Unreal 全局派生数据缓存",
            Path.Combine(_sharedRoots.LocalAppData, "UnrealEngine", "Common", "DerivedDataCache"),
            StorageCategory.HighCostCache,
            "删除后所有 Unreal 项目会重新生成着色器和派生数据，首次打开会明显变慢。");
        Add(candidates, "UnrealGlobal", "Unreal AutomationTool 日志",
            Path.Combine(_sharedRoots.UserProfile, "AppData", "Roaming", "Unreal Engine", "AutomationTool", "Logs"),
            StorageCategory.SafeTemporary,
            "仅删除历史自动化与打包日志，不影响项目源码。");

        AddUnrealProjectCandidates(candidates, "CrossingVoid", _unrealRoots.CrossingVoid);
        AddUnrealProjectCandidates(candidates, "FantasyProject", _unrealRoots.FantasyProject);
    }

    private static void AddUnrealProjectCandidates(
        ICollection<StorageCandidate> candidates,
        string name,
        string root)
    {
        Add(candidates, name, $"{name} 派生数据缓存", Path.Combine(root, "DerivedDataCache"),
            StorageCategory.RebuildableCache, "删除后项目会重新生成派生数据和着色器缓存。");
        Add(candidates, name, $"{name} Intermediate", Path.Combine(root, "Intermediate"),
            StorageCategory.RebuildableCache, "删除后 Unreal 会重新生成项目中间文件。");
        Add(candidates, name, $"{name} Cooked", Path.Combine(root, "Saved", "Cooked"),
            StorageCategory.RebuildableCache, "删除后需要重新烘焙目标平台内容。");
        Add(candidates, name, $"{name} StagedBuilds", Path.Combine(root, "Saved", "StagedBuilds"),
            StorageCategory.ReleaseArtifact, "删除后需要重新暂存打包内容；可能包含尚未归档的构建。");
        Add(candidates, name, $"{name} 历史日志", Path.Combine(root, "Saved", "Logs"),
            StorageCategory.SafeTemporary, "删除项目历史日志，不删除配置、存档或崩溃证据目录。");
        Add(candidates, name, $"{name} Binaries", Path.Combine(root, "Binaries"),
            StorageCategory.HighCostCache, "删除后必须重新编译项目和插件二进制，成本较高。");
    }

    private void AddSharedCacheCandidates(ICollection<StorageCandidate> candidates)
    {
        Add(candidates, "SharedCache", "NuGet 全局包缓存", Path.Combine(_sharedRoots.UserProfile, ".nuget", "packages"),
            StorageCategory.HighCostCache, "删除后 .NET 依赖需要重新下载，也会影响其他项目的首次构建。共享高成本缓存。 ");
        Add(candidates, "SharedCache", "NuGet HTTP 缓存", Path.Combine(_sharedRoots.LocalAppData, "NuGet", "v3-cache"),
            StorageCategory.RebuildableCache, "删除后 NuGet 会重新下载索引和包，也可能影响其他项目。 ");
        Add(candidates, "SharedCache", "NuGet 临时缓存", Path.Combine(_sharedRoots.Temp, "NuGetScratch"),
            StorageCategory.SafeTemporary, "删除未使用的 NuGet 临时内容，可能影响其他项目正在执行的还原任务。 ");
        Add(candidates, "SharedCache", "npm 下载缓存", Path.Combine(_sharedRoots.LocalAppData, "npm-cache"),
            StorageCategory.HighCostCache, "删除后 npm 依赖需要重新下载，也会影响其他项目。共享高成本缓存。 ");
        Add(candidates, "SharedCache", "Cargo Registry 缓存", Path.Combine(_sharedRoots.UserProfile, ".cargo", "registry"),
            StorageCategory.HighCostCache, "删除后 Rust crates 需要重新下载和解包，也会影响其他项目。共享高成本缓存。 ");
        Add(candidates, "SharedCache", "Cargo Git 缓存", Path.Combine(_sharedRoots.UserProfile, ".cargo", "git"),
            StorageCategory.HighCostCache, "删除后 Cargo Git 依赖需要重新拉取，也会影响其他项目。共享高成本缓存。 ");
        Add(candidates, "SharedCache", "Gradle 依赖缓存", Path.Combine(_sharedRoots.UserProfile, ".gradle", "caches"),
            StorageCategory.HighCostCache, "删除后 Android 依赖需要重新下载，也会影响其他项目。共享高成本缓存。 ");
        Add(candidates, "SharedCache", "Gradle Wrapper 缓存", Path.Combine(_sharedRoots.UserProfile, ".gradle", "wrapper"),
            StorageCategory.HighCostCache, "删除后 Gradle 发行版需要重新下载，也会影响其他项目。共享高成本缓存。 ");
    }

    private static void AddCrossingAndroidCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "CrossingVoidinitiator-Android");
        if (root is null)
        {
            return;
        }

        Add(candidates, "CrossingVoidinitiator-Android", "前端构建输出", Path.Combine(root, "dist"),
            StorageCategory.RebuildableCache, "删除后需要重新构建 Android 启动器前端。");
        Add(candidates, "CrossingVoidinitiator-Android", "Node 依赖缓存", Path.Combine(root, "node_modules"),
            StorageCategory.HighCostCache, "删除后需要重新下载 npm 依赖。");
        Add(candidates, "CrossingVoidinitiator-Android", "Android 工程构建缓存", Path.Combine(root, "android", "build"),
            StorageCategory.RebuildableCache, "删除后 Gradle 会重新生成工程级构建缓存。");
        Add(candidates, "CrossingVoidinitiator-Android", "Android App 构建输出", Path.Combine(root, "android", "app", "build"),
            StorageCategory.RebuildableCache, "删除后需要重新构建 APK。");
        Add(candidates, "CrossingVoidinitiator-Android", "Android Gradle 项目缓存", Path.Combine(root, "android", ".gradle"),
            StorageCategory.HighCostCache, "删除后 Gradle 会重新初始化项目缓存并可能重新下载依赖。");
    }

    private static void AddAndroidSdkCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var sdk = settings.Toolchains.AndroidSdkRoot;
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }

        var sdkRoot = Path.GetFullPath(sdk);
        AddReadOnly(candidates, "AndroidSdk", "Android NDK", Path.Combine(sdkRoot, "ndk"),
            "只统计。NDK 可能被其他 Android 或 Unreal 项目使用，AxTools 不直接删除。");
        AddReadOnly(candidates, "AndroidSdk", "Android CMake", Path.Combine(sdkRoot, "cmake"),
            "只统计。CMake 可能被其他原生项目使用，AxTools 不直接删除。");
        AddReadOnly(candidates, "AndroidSdk", "Android 模拟器", Path.Combine(sdkRoot, "emulator"),
            "只统计。需要通过 SDK Manager 管理，AxTools 不直接删除。");
        AddReadOnly(candidates, "AndroidSdk", "Android 系统镜像", Path.Combine(sdkRoot, "system-images"),
            "只统计。系统镜像应通过 SDK Manager 卸载，AxTools 不直接删除。");
        AddReadOnly(candidates, "AndroidSdk", "Android 平台", Path.Combine(sdkRoot, "platforms"),
            "只统计。平台版本应通过 SDK Manager 管理，AxTools 不直接删除。");
        AddReadOnly(candidates, "AndroidSdk", "Android Build Tools", Path.Combine(sdkRoot, "build-tools"),
            "只统计。构建工具版本应通过 SDK Manager 管理，AxTools 不直接删除。");
    }

    private static void AddAxToolsCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "AxTools");
        if (root is not null)
        {
            Add(candidates, "AxTools", ".NET 构建输出", Path.Combine(root, "bin"),
                StorageCategory.RebuildableCache, "删除后需要重新构建 AxTools。");
            Add(candidates, "AxTools", ".NET 中间缓存", Path.Combine(root, "obj"),
                StorageCategory.RebuildableCache, "删除后首次构建会重新生成中间文件。");
        }

        var outputRoot = GetOutputRoot(settings, "AxTools");
        if (outputRoot is not null)
        {
            Add(candidates, "AxTools", "可回收发布工作区", Path.Combine(outputRoot, ".work"),
                StorageCategory.SafeTemporary, "仅包含 AxTools 打包过程的可回收中间文件。");
            Add(candidates, "AxTools", "AxTools 发布产物", outputRoot,
                StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未上传的版本。");
        }
    }

    private static void AddFantasyToolsCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "FantasyTools");
        if (root is not null)
        {
            Add(candidates, "FantasyTools", ".NET 构建输出", Path.Combine(root, "bin"),
                StorageCategory.RebuildableCache, "删除后需要重新构建 FantasyTools。");
            Add(candidates, "FantasyTools", ".NET 中间缓存", Path.Combine(root, "obj"),
                StorageCategory.RebuildableCache, "删除后首次构建会重新生成中间文件。");
        }

        var outputRoot = GetOutputRoot(settings, "FantasyTools");
        if (outputRoot is not null)
        {
            Add(candidates, "FantasyTools", "FantasyTools 发布产物", outputRoot,
                StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未上传的版本。");
        }
    }

    private static void AddCrossingCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "CrossingVoidinitiator-PC");
        if (root is null)
        {
            return;
        }

        Add(candidates, "CrossingVoidinitiator-PC", "前端构建输出", Path.Combine(root, "dist"),
            StorageCategory.RebuildableCache, "删除后需要重新构建启动器前端。");
        Add(candidates, "CrossingVoidinitiator-PC", "调试更新输出", Path.Combine(root, "dist-launcher-update"),
            StorageCategory.RebuildableCache, "删除后需要重新生成启动器更新包。");
        Add(candidates, "CrossingVoidinitiator-PC", "Rust 构建缓存", Path.Combine(root, "src-tauri", "target"),
            StorageCategory.RebuildableCache, "删除后 Cargo 需要重新编译依赖和启动器。");
        Add(candidates, "CrossingVoidinitiator-PC", "Node 依赖缓存", Path.Combine(root, "node_modules"),
            StorageCategory.HighCostCache, "删除后需要重新下载 npm 依赖。");
        Add(candidates, "CrossingVoidinitiator-PC", "启动器历史日志", Path.Combine(root, "Logs"),
            StorageCategory.SafeTemporary, "删除启动器历史日志，不影响启动器配置或发布脚本。");
        Add(candidates, "CrossingVoidinitiator-PC", "OSS 发布演练输出", Path.Combine(root, "Saved", "OssDryRun"),
            StorageCategory.SafeTemporary, "删除 OSS 发布演练生成的本地临时输出。");
        Add(candidates, "CrossingVoidinitiator-PC", "调试更新输出", Path.Combine(root, "dist-launcher-update-debug"),
            StorageCategory.SafeTemporary, "删除调试用启动器更新输出，正式发布前需要重新生成。");
        AddReadOnly(candidates, "CrossingVoidinitiator-PC", "游戏发布包", Path.Combine(root, "Saved", "GamePackages"),
            "只统计已生成游戏发布包占用；可能包含待上传内容，AxTools 不直接删除。");
        var outputRoot = GetOutputRoot(settings, "CrossingVoidinitiator-PC");
        if (outputRoot is not null)
        {
            Add(candidates, "CrossingVoidinitiator-PC", "启动器与游戏发布产物", outputRoot,
                StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未上传的版本。");
        }
    }

    private static void AddGalExcleToolsCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "GalExcleTools");
        if (root is not null)
        {
            Add(candidates, "GalExcleTools", ".NET 构建输出", Path.Combine(root, "bin"),
                StorageCategory.RebuildableCache, "删除后需要重新构建剧情工具箱。");
            Add(candidates, "GalExcleTools", ".NET 中间缓存", Path.Combine(root, "obj"),
                StorageCategory.RebuildableCache, "删除后首次构建会重新生成中间文件。");
            Add(candidates, "GalExcleTools", "AppPackages 历史打包产物", Path.Combine(root, "AppPackages"),
                StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未迁移的版本。");
        }

        if (!settings.ManagedTools.TryGetValue("GalExcleTools", out var paths) ||
            string.IsNullOrWhiteSpace(paths.OutputRoot))
        {
            return;
        }

        var outputRoot = Path.GetFullPath(paths.OutputRoot);
        Add(candidates, "GalExcleTools", "可回收打包工作区", Path.Combine(outputRoot, ".package-work"),
            StorageCategory.SafeTemporary, "仅包含剧情工具箱打包过程的可回收中间文件。");
        Add(candidates, "GalExcleTools", "剧情工具箱发布产物", outputRoot,
            StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未上传的版本。");
    }

    private void AddCrossingVoidZDToolCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "CrossingVoidZDTool");
        if (root is not null)
        {
            Add(candidates, "CrossingVoidZDTool", ".NET 中间缓存", Path.Combine(root, "obj"),
                StorageCategory.RebuildableCache, "删除后首次构建会重新生成 ZD空界幻境中间文件。");
            Add(candidates, "CrossingVoidZDTool", "历史验证构建", Path.Combine(root, "bin", "verify"),
                StorageCategory.SafeTemporary, "删除历史验证构建，不影响源码和正式版。");
            Add(candidates, "CrossingVoidZDTool", "回归测试中间缓存", Path.Combine(
                    root, "Tests", "CrossingVoidZDTool.RegressionTests", "obj"),
                StorageCategory.RebuildableCache, "删除后回归测试会重新生成中间文件。");
            Add(candidates, "CrossingVoidZDTool", "回归测试构建输出", Path.Combine(
                    root, "Tests", "CrossingVoidZDTool.RegressionTests", "bin"),
                StorageCategory.RebuildableCache, "删除后需要重新构建回归测试。");
            Add(candidates, "CrossingVoidZDTool", "完整开发构建输出", Path.Combine(root, "bin"),
                StorageCategory.HighCostCache, "删除后开发版不能直接启动，必须重新恢复依赖并构建。");
        }

        Add(candidates, "CrossingVoidZDTool", "Pakout 临时工作区",
            Path.Combine(_sharedRoots.Temp, "CrossingVoidZDTool-Pakout"),
            StorageCategory.SafeTemporary, "仅在没有 ZD空界幻境打包任务运行时清理。");

        if (!settings.ManagedTools.TryGetValue("CrossingVoidZDTool", out var paths) ||
            string.IsNullOrWhiteSpace(paths.OutputRoot))
        {
            return;
        }

        var outputRoot = Path.GetFullPath(paths.OutputRoot);
        Add(candidates, "CrossingVoidZDTool", "AxTools 打包暂存目录",
            Path.Combine(outputRoot, ".axtools-staging"),
            StorageCategory.SafeTemporary, "删除后尚未确认替换的已验证临时包会失效。");
        AddReadOnly(candidates, "CrossingVoidZDTool", "ZD空界幻境正式包", outputRoot,
            "只统计正式包占用；AxTools 不把整个正式输出根目录作为缓存删除。");
    }

    private static void AddFantasyProjectPcCandidates(
        AppSettings settings,
        ICollection<StorageCandidate> candidates)
    {
        var root = GetSourceRoot(settings, "FantasyProject-PC");
        if (root is null)
        {
            return;
        }

        Add(candidates, "FantasyProject-PC", "前端构建输出", Path.Combine(root, "dist"),
            StorageCategory.RebuildableCache, "删除后需要重新构建 FantasyProject-PC 前端。");
        Add(candidates, "FantasyProject-PC", "启动器更新中间产物", Path.Combine(root, "dist-launcher-update"),
            StorageCategory.RebuildableCache, "删除后需要重新生成启动器更新包。");
        Add(candidates, "FantasyProject-PC", "Rust 构建缓存", Path.Combine(root, "src-tauri", "target"),
            StorageCategory.HighCostCache, "删除后 Cargo 需要重新编译依赖和启动器。");
        Add(candidates, "FantasyProject-PC", "Node 依赖缓存", Path.Combine(root, "node_modules"),
            StorageCategory.HighCostCache, "删除后需要重新下载 npm 依赖。");

        var paths = settings.ManagedTools["FantasyProject-PC"];
        if (!string.IsNullOrWhiteSpace(paths.OutputRoot))
        {
            Add(candidates, "FantasyProject-PC", "启动器发布产物", paths.OutputRoot,
                StorageCategory.ReleaseArtifact, "删除后需要重新打包，且可能包含尚未上传的版本。");
        }
    }

    private static string? GetSourceRoot(AppSettings settings, string stableKey)
    {
        if (!settings.ManagedTools.TryGetValue(stableKey, out var paths) ||
            string.IsNullOrWhiteSpace(paths.SourceRoot))
        {
            return null;
        }

        return Path.GetFullPath(paths.SourceRoot);
    }

    private static string? GetOutputRoot(AppSettings settings, string stableKey)
    {
        if (!settings.ManagedTools.TryGetValue(stableKey, out var paths) ||
            string.IsNullOrWhiteSpace(paths.OutputRoot))
        {
            return null;
        }

        return Path.GetFullPath(paths.OutputRoot);
    }

    private static void Add(
        ICollection<StorageCandidate> candidates,
        string stableKey,
        string displayName,
        string path,
        StorageCategory category,
        string impact)
    {
        candidates.Add(new StorageCandidate(
            stableKey,
            displayName,
            Path.GetFullPath(path),
            category,
            impact));
    }

    private static void AddReadOnly(
        ICollection<StorageCandidate> candidates,
        string stableKey,
        string displayName,
        string path,
        string impact)
    {
        candidates.Add(new StorageCandidate(
            stableKey,
            displayName,
            Path.GetFullPath(path),
            StorageCategory.HighCostCache,
            impact,
            CanClean: false));
    }
}
