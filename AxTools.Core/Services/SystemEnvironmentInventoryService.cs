using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed partial class SystemEnvironmentInventoryService
{
    public IReadOnlyList<ToolchainStatusItem> CreateInventory(
        ToolchainSettings toolchains,
        EnvironmentInventoryPaths paths,
        IReadOnlyList<ToolchainStatusItem> baseItems)
    {
        var items = new List<ToolchainStatusItem>();
        AddUnreal(items, paths);
        AddVisualStudio(items, paths);
        AddWindowsSdk(items, paths);
        AddAndroid(items, toolchains, paths);
        AddManagedJava(items, toolchains, paths);
        AddAndroidTaskJava(items, toolchains);

        foreach (var item in baseItems)
        {
            if (item.Key is "java" or "android-java" or "android-sdk")
            {
                continue;
            }

            if (items.All(existing => !string.Equals(existing.Key, item.Key, StringComparison.Ordinal)))
            {
                items.Add(EnrichBaseItem(item, paths));
            }
        }

        return items;
    }

    private static void AddUnreal(
        ICollection<ToolchainStatusItem> items,
        EnvironmentInventoryPaths paths)
    {
        var buildVersion = Path.Combine(paths.UnrealEngineRoot, "Engine", "Build", "Build.version");
        var project = Path.Combine(paths.CrossingVoidRoot, "CrossingVoid.uproject");
        var version = ReadUnrealVersion(buildVersion);
        var available = !string.IsNullOrWhiteSpace(version) && File.Exists(project);
        items.Add(new ToolchainStatusItem(
            "unreal-engine-5.6.1",
            "Unreal Engine 源码引擎",
            paths.UnrealEngineRoot,
            available,
            available ? "CrossingVoid 已绑定此源码引擎。" : "没有找到 CrossingVoid 绑定的源码引擎。",
            "负责 Unreal 项目编辑、烘焙、打包和原生代码构建。",
            version,
            "5.6.1（CrossingVoid 项目绑定版本）",
            available && version == "5.6.1" ? EnvironmentHealthState.Recommended : EnvironmentHealthState.Warning,
            [
                Consumer("CrossingVoid", "UE 5.6.1", project),
                Consumer(
                    "ZD空界幻境 Unreal 同步",
                    "UE 5.6.1 编辑器命令行只读导出",
                    @"D:\UnrealMap\CrossingVoidZDTool\CrossingVoidZDTool.csproj")
            ],
            Existing(buildVersion, project),
            EnvironmentManagementMode.ReadOnly,
            "引擎安装不由 AxTools 删除；仅管理派生缓存。",
            LastWrite(paths.UnrealEngineRoot)));
    }

    private static void AddVisualStudio(
        ICollection<ToolchainStatusItem> items,
        EnvironmentInventoryPaths paths)
    {
        var msvc2022 = GetDirectoryVersions(Path.Combine(
            paths.VisualStudio2022Root,
            "VC",
            "Tools",
            "MSVC"));
        var msvc2026 = GetDirectoryVersions(Path.Combine(
            paths.VisualStudio2026Root,
            "VC",
            "Tools",
            "MSVC"));
        var windowsSdkJson = Path.Combine(
            paths.UnrealEngineRoot,
            "Engine",
            "Config",
            "Windows",
            "Windows_SDK.json");

        items.Add(new ToolchainStatusItem(
            "visual-studio-2026",
            "Visual Studio Community 2026",
            paths.VisualStudio2026Root,
            Directory.Exists(paths.VisualStudio2026Root),
            "日常 IDE；不作为 UE 5.6 的 v143 编译器来源。",
            "用于浏览、编辑和调试现代项目。UE 5.6 构建仍使用独立 VS2022 Build Tools。",
            paths.VisualStudio2026Version,
            "18.8.2（主 IDE）",
            Directory.Exists(paths.VisualStudio2026Root)
                ? EnvironmentHealthState.Recommended
                : EnvironmentHealthState.Missing,
            [new EnvironmentConsumerInfo("日常代码编辑", "VS 2026", "已确认使用", paths.VisualStudio2026Root)],
            Existing(paths.VisualStudio2026Root),
            EnvironmentManagementMode.OfficialManager,
            "通过 Visual Studio Installer 修改或卸载。",
            LastWrite(paths.VisualStudio2026Root)));

        items.Add(new ToolchainStatusItem(
            "visual-studio-2022-buildtools",
            "Visual Studio 生成工具 2022",
            paths.VisualStudio2022Root,
            Directory.Exists(paths.VisualStudio2022Root),
            "为 UE 5.6 提供 v143、MSBuild 和 MSVC 14.44。",
            "最小化的 Unreal 原生编译环境，不包含完整 VS2022 IDE。",
            paths.VisualStudio2022Version,
            "17.14.x（UE 5.6 已验证）",
            Directory.Exists(paths.VisualStudio2022Root)
                ? EnvironmentHealthState.Recommended
                : EnvironmentHealthState.Missing,
            UnrealConsumers(paths, "VS2022 Build Tools 17.14"),
            Existing(windowsSdkJson, paths.VisualStudio2022Root),
            EnvironmentManagementMode.OfficialManager,
            "通过 Visual Studio Installer 保留最小 v143 组件。",
            LastWrite(paths.VisualStudio2022Root)));

        var msvcCurrent = msvc2022.FirstOrDefault() ?? string.Empty;
        items.Add(new ToolchainStatusItem(
            "msvc-v143",
            "MSVC v143 C++ 工具集",
            Path.Combine(paths.VisualStudio2022Root, "VC", "Tools", "MSVC"),
            !string.IsNullOrWhiteSpace(msvcCurrent),
            "UE 5.6 实际使用 VS2022 Build Tools 中的 v143 编译器。",
            "编译 Unreal C++ 模块、插件和 Editor 目标。",
            msvcCurrent,
            "14.44.x（本机 UE 5.6 已验证）；引擎原始首选 14.38.x",
            msvcCurrent.StartsWith("14.44", StringComparison.Ordinal)
                ? EnvironmentHealthState.Recommended
                : EnvironmentHealthState.Warning,
            UnrealConsumers(paths, "MSVC 14.44"),
            Existing(windowsSdkJson),
            EnvironmentManagementMode.OfficialManager,
            $"VS 2026 的 {string.Join(", ", msvc2026)} 不用于 UE 5.6 构建。",
            LastWrite(Path.Combine(paths.VisualStudio2022Root, "VC", "Tools", "MSVC"))));
    }

    private static void AddWindowsSdk(
        ICollection<ToolchainStatusItem> items,
        EnvironmentInventoryPaths paths)
    {
        var versions = GetDirectoryVersions(Path.Combine(paths.WindowsKitsRoot, "Lib"));
        var evidence = Path.Combine(
            paths.UnrealEngineRoot,
            "Engine",
            "Config",
            "Windows",
            "Windows_SDK.json");
        var recommended = ReadJsonString(evidence, "MainVersion") ?? "10.0.22621.0";
        items.Add(new ToolchainStatusItem(
            "windows-sdk",
            "Windows SDK",
            paths.WindowsKitsRoot,
            versions.Count > 0,
            versions.Contains(recommended, StringComparer.OrdinalIgnoreCase)
                ? "UE 推荐 SDK 已安装；其他版本可能由 VS 2026 或 WinUI 使用。"
                : "没有找到 UE 推荐的 Windows SDK。",
            "为 MSVC、WinUI 和 Unreal 提供 Windows 头文件、库和签名工具。",
            string.Join(", ", versions),
            recommended,
            versions.Contains(recommended, StringComparer.OrdinalIgnoreCase)
                ? EnvironmentHealthState.Recommended
                : EnvironmentHealthState.Warning,
            [
                Consumer("CrossingVoid", recommended, evidence),
                new EnvironmentConsumerInfo("AxTools / WinUI 工具", "Windows 10 SDK 19041+", "已确认使用", @"D:\UnrealMap\AxTools\AxTools.csproj")
            ],
            Existing(evidence),
            EnvironmentManagementMode.OfficialManager,
            "通过 Visual Studio Installer 或 Windows 设置维护。",
            LastWrite(paths.WindowsKitsRoot)));
    }

    private static void AddAndroid(
        ICollection<ToolchainStatusItem> items,
        ToolchainSettings toolchains,
        EnvironmentInventoryPaths paths)
    {
        var sdkJson = Path.Combine(
            paths.UnrealEngineRoot,
            "Engine",
            "Config",
            "Android",
            "Android_SDK.json");
        var projectIni = Path.Combine(paths.CrossingVoidRoot, "Config", "DefaultEngine.ini");
        var log = FindNewestLog(paths.CrossingVoidRoot);
        var platforms = GetDirectoryVersions(Path.Combine(paths.AndroidSdkRoot, "platforms"));
        var buildTools = GetDirectoryVersions(Path.Combine(paths.AndroidSdkRoot, "build-tools"));
        var cmake = GetDirectoryVersions(Path.Combine(paths.AndroidSdkRoot, "cmake"));
        var ndks = ReadNdkVersions(Path.Combine(paths.AndroidSdkRoot, "ndk"));
        var successfulNdk = ReadSuccessfulNdk(log) ?? "r25b";
        var preferredNdk = ReadJsonString(sdkJson, "MainVersion") ?? "r27c";
        var recommendedPlatform = ReadJsonString(sdkJson, "platforms") ?? "android-34";
        var recommendedBuildTools = ReadJsonString(sdkJson, "build-tools", "build_tools") ?? "35.0.1";
        var recommendedCmake = ReadJsonString(sdkJson, "cmake") ?? "3.22.1";
        var androidConsumers = new[]
        {
            Consumer("CrossingVoid", $"SDK 34 / NDK {successfulNdk} / JBR 21", log ?? projectIni),
            new EnvironmentConsumerInfo("零境启动器 Android", "SDK 36 / JDK 21 / Gradle Wrapper", "已确认使用", @"D:\UnrealMap\CrossingVoidinitiator-Android\android\gradle\wrapper\gradle-wrapper.properties")
        };

        items.Add(AndroidItem(
            "android-sdk-platforms",
            "Android SDK Platforms",
            Path.Combine(paths.AndroidSdkRoot, "platforms"),
            platforms,
            $"{recommendedPlatform}（Unreal）；android-36（Android 启动器）",
            platforms.Contains(recommendedPlatform, StringComparer.OrdinalIgnoreCase) && platforms.Contains("android-36", StringComparer.OrdinalIgnoreCase),
            "提供 Android API 编译平台；项目 Target SDK 与编译平台版本可以不同。",
            androidConsumers,
            Existing(sdkJson, projectIni)));

        items.Add(AndroidItem(
            "android-build-tools",
            "Android Build Tools",
            Path.Combine(paths.AndroidSdkRoot, "build-tools"),
            buildTools,
            $"{recommendedBuildTools}（引擎推荐）；当前成功环境可继续使用已安装版本",
            buildTools.Any(version => version.StartsWith("35.", StringComparison.Ordinal)),
            "包含 aapt、zipalign、apksigner 等 APK 构建和校验工具。",
            androidConsumers,
            Existing(sdkJson, log)));

        var ndkText = string.Join(", ", ndks.Select(item => $"{item.ReleaseName} ({item.Revision})"));
        items.Add(new ToolchainStatusItem(
            "android-ndk",
            "Android NDK",
            Path.Combine(paths.AndroidSdkRoot, "ndk"),
            ndks.Count > 0,
            $"CrossingVoid 最近成功环境使用 {successfulNdk}；{preferredNdk} 是引擎 AutoSDK 推荐值。",
            "为 Unreal Android 和其他原生项目编译 C/C++ 库。多个版本可并存。",
            ndkText,
            $"{successfulNdk}（CrossingVoid 已确认） / {preferredNdk}（UE AutoSDK 推荐）",
            ndks.Any(item => string.Equals(item.ReleaseName, successfulNdk, StringComparison.OrdinalIgnoreCase))
                ? EnvironmentHealthState.Compatible
                : EnvironmentHealthState.Warning,
            androidConsumers,
            Existing(sdkJson, log),
            EnvironmentManagementMode.OfficialManager,
            "通过 Android SDK Manager 卸载；AxTools 不直接删除 NDK。",
            LastWrite(Path.Combine(paths.AndroidSdkRoot, "ndk"))));

        items.Add(AndroidItem(
            "android-cmake",
            "Android CMake",
            Path.Combine(paths.AndroidSdkRoot, "cmake"),
            cmake,
            recommendedCmake,
            cmake.Contains(recommendedCmake, StringComparer.OrdinalIgnoreCase),
            "为 NDK 原生代码生成构建文件。",
            [Consumer("CrossingVoid", recommendedCmake, sdkJson)],
            Existing(sdkJson)));

        var jbrRelease = Path.Combine(paths.AndroidStudioRoot, "jbr", "release");
        var jbrVersion = ReadReleaseValue(jbrRelease, "JAVA_VERSION");
        items.Add(new ToolchainStatusItem(
            "android-studio-jbr",
            "Android Studio JBR",
            Path.Combine(paths.AndroidStudioRoot, "jbr"),
            !string.IsNullOrWhiteSpace(jbrVersion),
            "CrossingVoid 的 UE 5.6 Android 环境使用 Android Studio 自带 JBR。",
            "Unreal Android 打包使用的 Java 21 运行时，与 Android 启动器的 JDK 23 分开。",
            jbrVersion,
            "21.0.3（CrossingVoid 已验证）",
            jbrVersion.StartsWith("21.", StringComparison.Ordinal)
                ? EnvironmentHealthState.Recommended
                : EnvironmentHealthState.Warning,
            [Consumer("CrossingVoid", "JBR 21.0.3", jbrRelease)],
            Existing(jbrRelease, log),
            EnvironmentManagementMode.OfficialManager,
            "随 Android Studio 维护，不单独删除。",
            LastWrite(Path.Combine(paths.AndroidStudioRoot, "jbr"))));
    }

    private static void AddManagedJava(
        ICollection<ToolchainStatusItem> items,
        ToolchainSettings toolchains,
        EnvironmentInventoryPaths paths)
    {
        var available = !string.IsNullOrWhiteSpace(toolchains.JavaHome) &&
            Directory.Exists(toolchains.JavaHome);
        var conflict = !string.IsNullOrWhiteSpace(paths.GlobalJavaHome) &&
            !string.IsNullOrWhiteSpace(toolchains.JavaHome) &&
            !string.Equals(
                Path.GetFullPath(paths.GlobalJavaHome),
                Path.GetFullPath(toolchains.JavaHome),
                StringComparison.OrdinalIgnoreCase);
        items.Add(new ToolchainStatusItem(
            "managed-java",
            "AxTools 任务 JDK",
            toolchains.JavaHome,
            available,
            !available
                ? "未检测到 AxTools 通用 JDK，请在环境路径设置中指定 JDK 23。"
                : conflict
                ? $"全局 JAVA_HOME 指向 {paths.GlobalJavaHome}；AxTools 会为各任务注入独立版本，不修改全局设置。"
                : "AxTools 任务 JDK 与当前全局配置没有冲突。",
            "通用 Java 环境；不会替换 Android 启动器的专用 JDK 21、Unreal JBR 或其他项目 JDK。",
            DetectVersion(toolchains.JavaHome),
            "JDK 23（通用任务环境）",
            !available
                ? EnvironmentHealthState.Missing
                : conflict
                    ? EnvironmentHealthState.Warning
                    : EnvironmentHealthState.Recommended,
            Array.Empty<EnvironmentConsumerInfo>(),
            Existing(toolchains.JavaHome),
            EnvironmentManagementMode.OfficialManager,
            "通过应用安装器维护；AxTools 仅注入任务环境。",
            LastWrite(toolchains.JavaHome)));
    }

    private static void AddAndroidTaskJava(
        ICollection<ToolchainStatusItem> items,
        ToolchainSettings toolchains)
    {
        var available = !string.IsNullOrWhiteSpace(toolchains.AndroidJavaHome) &&
            Directory.Exists(toolchains.AndroidJavaHome);
        items.Add(new ToolchainStatusItem(
            "managed-android-java",
            "零境启动器 Android JDK",
            toolchains.AndroidJavaHome,
            available,
            available
                ? "AxTools 只为 Android 启动器任务注入此 JDK 21，不修改系统 JAVA_HOME。"
                : "未检测到 Android 启动器 JDK 21，请在环境路径设置中指定。",
            "Capacitor / Gradle 构建专用 Java 环境，与 AxTools 通用 JDK 和 Unreal Android JBR 分开。",
            DetectVersion(toolchains.AndroidJavaHome),
            "JDK 21（零境启动器 Android）",
            available && DetectVersion(toolchains.AndroidJavaHome).StartsWith("21.", StringComparison.Ordinal)
                ? EnvironmentHealthState.Recommended
                : available
                    ? EnvironmentHealthState.Warning
                    : EnvironmentHealthState.Missing,
            [new EnvironmentConsumerInfo("零境启动器 Android", "JDK 21", "已确认使用", @"D:\UnrealMap\CrossingVoidinitiator-Android\android\app\build.gradle")],
            Existing(toolchains.AndroidJavaHome),
            EnvironmentManagementMode.OfficialManager,
            "通过 Android Studio 或 JDK 安装器维护；AxTools 只注入任务环境。",
            LastWrite(toolchains.AndroidJavaHome)));
    }

    private static ToolchainStatusItem AndroidItem(
        string key,
        string name,
        string path,
        IReadOnlyList<string> versions,
        string recommended,
        bool valid,
        string description,
        IReadOnlyList<EnvironmentConsumerInfo> consumers,
        IReadOnlyList<string> evidence) => new(
            key,
            name,
            path,
            versions.Count > 0,
            valid ? "当前已安装满足已知项目需求。" : "当前安装与已知项目推荐值不完全匹配。",
            description,
            string.Join(", ", versions),
            recommended,
            valid ? EnvironmentHealthState.Recommended : EnvironmentHealthState.Warning,
            consumers,
            evidence,
            EnvironmentManagementMode.OfficialManager,
            "通过 Android SDK Manager 维护；AxTools 只读统计安装空间。",
            LastWrite(path));

    private static ToolchainStatusItem EnrichBaseItem(
        ToolchainStatusItem item,
        EnvironmentInventoryPaths paths)
    {
        var consumers = item.Key switch
        {
            "powershell" => new[] { Consumer("AxTools", "PowerShell 7", item.Path) },
            "dotnet" => new[]
            {
                Consumer("AxTools", ".NET 8", @"D:\UnrealMap\AxTools\AxTools.csproj"),
                Consumer("FantasyTools", ".NET 8", @"D:\UnrealMap\FantasyTools\FantasyTools.csproj"),
                Consumer("剧情工具箱", ".NET 8", @"D:\UnrealMap\GalExcleTools\GalExcleTools.csproj"),
                Consumer("ZD空界幻境", ".NET 8", @"D:\UnrealMap\CrossingVoidZDTool\CrossingVoidZDTool.csproj")
            },
            "node" or "npm" => new[]
            {
                Consumer("FantasyProject-PC", "Node / npm", @"D:\UnrealMap\FantasyProject-PC\package.json"),
                Consumer("CrossingVoidinitiator-PC", "Node / npm", @"D:\UnrealMap\CrossingVoidinitiator-PC\package.json"),
                Consumer("零境启动器 Android", "Node / npm", @"D:\UnrealMap\CrossingVoidinitiator-Android\package.json")
            },
            "cargo" => new[]
            {
                Consumer("FantasyProject-PC", "Rust / Cargo", @"D:\UnrealMap\FantasyProject-PC\src-tauri\Cargo.toml"),
                Consumer("CrossingVoidinitiator-PC", "Rust / Cargo", @"D:\UnrealMap\CrossingVoidinitiator-PC\src-tauri\Cargo.toml")
            },
            "bandizip" => new[] { Consumer("零境交错游戏分片", "Bandizip CLI", item.Path) },
            _ => Array.Empty<EnvironmentConsumerInfo>()
        };
        return item with
        {
            Description = string.IsNullOrWhiteSpace(item.Description)
                ? $"AxTools 管理的 {item.DisplayName} 工具链。"
                : item.Description,
            CurrentVersion = string.IsNullOrWhiteSpace(item.CurrentVersion)
                ? DetectBaseVersion(item, paths)
                : item.CurrentVersion,
            RecommendedVersion = string.IsNullOrWhiteSpace(item.RecommendedVersion)
                ? RecommendedBaseVersion(item.Key)
                : item.RecommendedVersion,
            Health = item.IsAvailable ? EnvironmentHealthState.Compatible : EnvironmentHealthState.Missing,
            Consumers = consumers,
            EvidencePaths = consumers.Select(consumer => consumer.EvidencePath).Where(File.Exists).ToArray(),
            ManagementMode = EnvironmentManagementMode.OfficialManager,
            ManagementHint = "安装环境不直接删除；相关下载缓存在存储管理中单独清理。",
            LastModifiedAt = LastWrite(item.Path)
        };
    }

    private static string DetectBaseVersion(
        ToolchainStatusItem item,
        EnvironmentInventoryPaths paths)
    {
        if (item.Key == "dotnet")
        {
            var sdks = GetDirectoryVersions(Path.Combine(paths.DotNetRoot, "sdk"));
            if (sdks.Count > 0)
            {
                return string.Join(", ", sdks);
            }
        }

        if (item.Key == "npm")
        {
            var directory = Path.GetDirectoryName(item.Path) ?? string.Empty;
            var packageJson = Path.Combine(directory, "node_modules", "npm", "package.json");
            var version = ReadJsonString(packageJson, "version");
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        if (item.Key == "cargo")
        {
            var toolchains = GetDirectoryVersions(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".rustup",
                "toolchains"));
            if (toolchains.Count > 0)
            {
                return string.Join(", ", toolchains);
            }
        }

        return DetectVersion(item.Path);
    }

    private static string RecommendedBaseVersion(string key) => key switch
    {
        "powershell" => "PowerShell 7.x",
        "dotnet" => ".NET SDK 8.0.x（AxTools、FantasyTools、剧情工具箱、ZD空界幻境）",
        "node" => "按各启动器 package.json / 锁文件",
        "npm" => "与当前 Node.js 配套版本",
        "cargo" => "Rust stable（按 Tauri 项目锁文件）",
        "bandizip" => "Bandizip 7.x CLI",
        _ => "按使用程序配置"
    };

    private static IReadOnlyList<EnvironmentConsumerInfo> UnrealConsumers(
        EnvironmentInventoryPaths paths,
        string requirement)
    {
        var consumers = new List<EnvironmentConsumerInfo>();
        var crossing = Path.Combine(paths.CrossingVoidRoot, "CrossingVoid.uproject");
        if (File.Exists(crossing))
        {
            consumers.Add(Consumer("CrossingVoid", requirement, crossing));
        }

        var fantasy = Path.Combine(paths.FantasyProjectRoot, "FantasyProject.uproject");
        if (File.Exists(fantasy))
        {
            consumers.Add(Consumer("FantasyProject", requirement, fantasy));
        }

        return consumers;
    }

    private static EnvironmentConsumerInfo Consumer(
        string name,
        string requirement,
        string evidence) => new(name, requirement, "已确认使用", evidence);

    private static string ReadUnrealVersion(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return $"{root.GetProperty("MajorVersion").GetInt32()}.{root.GetProperty("MinorVersion").GetInt32()}.{root.GetProperty("PatchVersion").GetInt32()}";
    }

    private static string? ReadJsonString(string path, params string[] names)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var name in names)
        {
            if (document.RootElement.TryGetProperty(name, out var value))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetDirectoryVersions(string path) =>
        Directory.Exists(path)
            ? Directory.GetDirectories(path)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    private static IReadOnlyList<NdkVersion> ReadNdkVersions(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.GetDirectories(path)
            .Select(directory =>
            {
                var properties = ReadProperties(Path.Combine(directory, "source.properties"));
                var revision = properties.GetValueOrDefault("Pkg.Revision") ?? Path.GetFileName(directory);
                var release = properties.GetValueOrDefault("Pkg.ReleaseName") ?? ToNdkRelease(revision);
                return new NdkVersion(revision, release);
            })
            .OrderBy(item => item.Revision, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, string> ReadProperties(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return File.ReadLines(path)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !parts[0].StartsWith('#'))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static string ToNdkRelease(string revision)
    {
        var parts = revision.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return revision;
        }

        return $"r{major}{(char)('a' + Math.Clamp(minor, 0, 25))}";
    }

    private static string? ReadSuccessfulNdk(string? log)
    {
        if (string.IsNullOrWhiteSpace(log) || !File.Exists(log))
        {
            return null;
        }

        var match = CurrentSdkRegex().Match(File.ReadAllText(log));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindNewestLog(string crossingRoot)
    {
        var logs = Path.Combine(crossingRoot, "Saved", "Logs");
        return Directory.Exists(logs)
            ? Directory.GetFiles(logs, "CrossingVoid*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static string ReadReleaseValue(string path, string key)
    {
        var value = ReadProperties(path).GetValueOrDefault(key) ?? string.Empty;
        return value.Trim('"');
    }

    private static string VersionFromDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var name = Directory.Exists(path)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
            : Path.GetFileName(Path.GetDirectoryName(path));
        return name ?? string.Empty;
    }

    private static string DetectVersion(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var version = info.ProductVersion ?? info.FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        if (Directory.Exists(path))
        {
            var javaVersion = ReadReleaseValue(Path.Combine(path, "release"), "JAVA_VERSION");
            if (!string.IsNullOrWhiteSpace(javaVersion))
            {
                return javaVersion;
            }
        }

        return VersionFromDirectory(path);
    }

    private static IReadOnlyList<string> Existing(params string?[] paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Cast<string>()
            .ToArray();

    private static DateTimeOffset? LastWrite(string path)
    {
        if (File.Exists(path))
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }

        if (Directory.Exists(path))
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }

        return null;
    }

    [GeneratedRegex(@"Current_Sdk=(r\d+[a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentSdkRegex();

    private sealed record NdkVersion(string Revision, string ReleaseName);
}
