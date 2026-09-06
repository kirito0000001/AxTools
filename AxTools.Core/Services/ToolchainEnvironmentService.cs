using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ToolchainEnvironmentService
{
    private readonly Func<string, string?> _commandResolver;
    private readonly ToolchainCandidateRoots _candidateRoots;

    public ToolchainEnvironmentService(
        Func<string, string?>? commandResolver = null,
        ToolchainCandidateRoots? candidateRoots = null)
    {
        _commandResolver = commandResolver ?? ResolveCommand;
        _candidateRoots = candidateRoots ?? new ToolchainCandidateRoots(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android",
                "Sdk"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Android",
                "openjdk"));
    }

    public ToolchainDetectionResult DetectAndApply(ToolchainSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = false;

        changed |= RepairFile(settings.PowerShellPath, _commandResolver("pwsh.exe"), value => settings.PowerShellPath = value);
        changed |= RepairFile(settings.DotNetPath, _commandResolver("dotnet.exe"), value => settings.DotNetPath = value);
        changed |= RepairFile(settings.NodePath, _commandResolver("node.exe"), value => settings.NodePath = value);
        changed |= RepairFile(settings.NpmPath, _commandResolver("npm.cmd"), value => settings.NpmPath = value);
        changed |= RepairFile(settings.CargoPath, _commandResolver("cargo.exe"), value => settings.CargoPath = value);
        changed |= RepairFile(
            settings.BandizipPath,
            FirstExisting(
                _commandResolver("bz.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bandizip", "bz.exe")),
            value => settings.BandizipPath = value);

        var javaHome = IsJavaHome(settings.JavaHome)
            ? Path.GetFullPath(settings.JavaHome)
            : FindJdk23(_candidateRoots.JavaRoot);
        changed |= RepairDirectory(settings.JavaHome, javaHome, value => settings.JavaHome = value);

        var androidJavaHome = IsJavaHome(settings.AndroidJavaHome)
            ? Path.GetFullPath(settings.AndroidJavaHome)
            : FindJdk21(_candidateRoots.AndroidJavaRoot) ??
                FirstExistingJavaHome(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Android",
                    "Android Studio",
                    "jbr"));
        changed |= RepairDirectory(
            settings.AndroidJavaHome,
            androidJavaHome,
            value => settings.AndroidJavaHome = value);

        var androidSdk = IsAndroidSdk(settings.AndroidSdkRoot)
            ? Path.GetFullPath(settings.AndroidSdkRoot)
            : IsAndroidSdk(_candidateRoots.AndroidSdkRoot)
                ? Path.GetFullPath(_candidateRoots.AndroidSdkRoot)
                : null;
        changed |= RepairDirectory(settings.AndroidSdkRoot, androidSdk, value => settings.AndroidSdkRoot = value);

        var nativeToolchain = IsVisualStudioToolchain(
            settings.VisualStudioRoot,
            settings.MsvcVersion)
            ? new VisualStudioToolchain(settings.VisualStudioRoot, settings.MsvcVersion)
            : FindVisualStudioToolchain();
        if (nativeToolchain is not null)
        {
            changed |= Replace(
                settings.VisualStudioRoot,
                nativeToolchain.Root,
                value => settings.VisualStudioRoot = value);
            changed |= ReplaceValue(
                settings.MsvcVersion,
                nativeToolchain.MsvcVersion,
                value => settings.MsvcVersion = value);
        }

        var nativeToolchainAvailable = IsVisualStudioToolchain(
            settings.VisualStudioRoot,
            settings.MsvcVersion);

        return new ToolchainDetectionResult(
            changed,
            [
                Status("powershell", "PowerShell 7", settings.PowerShellPath, File.Exists),
                Status("dotnet", ".NET SDK", settings.DotNetPath, File.Exists),
                Status("node", "Node.js", settings.NodePath, File.Exists),
                Status("npm", "npm", settings.NpmPath, File.Exists),
                Status("cargo", "Rust / Cargo", settings.CargoPath, File.Exists),
                Status("java", "JDK", settings.JavaHome, IsJavaHome),
                Status("android-java", "Android JDK 21", settings.AndroidJavaHome, IsJavaHome),
                Status("android-sdk", "Android SDK / adb", settings.AndroidSdkRoot, IsAndroidSdk),
                Status("bandizip", "Bandizip", settings.BandizipPath, File.Exists),
                new ToolchainStatusItem(
                    "msvc",
                    "Visual Studio C++ / Windows SDK",
                    settings.VisualStudioRoot,
                    nativeToolchainAvailable,
                    nativeToolchainAvailable
                        ? "已检测可用的 x64 linker；编译 Rust 时按需加载 Windows SDK。"
                        : "未检测到完整的 x64 MSVC 工具链。",
                    Description: "供 Tauri/Rust Windows 原生链接使用，不修改系统全局环境。",
                    CurrentVersion: nativeToolchainAvailable
                        ? $"MSVC {settings.MsvcVersion}"
                        : string.Empty,
                    RecommendedVersion: "MSVC 14.44（当前项目兼容配置）",
                    Health: nativeToolchainAvailable
                        ? EnvironmentHealthState.Recommended
                        : EnvironmentHealthState.Missing,
                    ManagementMode: EnvironmentManagementMode.OfficialManager,
                    ManagementHint: "安装或维护请使用 Visual Studio Installer。")
            ]);
    }

    private VisualStudioToolchain? FindVisualStudioToolchain()
    {
        var roots = _candidateRoots.VisualStudioRoots ?? GetDefaultVisualStudioRoots();
        return roots
            .Where(Directory.Exists)
            .SelectMany(root => FindMsvcVersions(root)
                .Select(version => new VisualStudioToolchain(Path.GetFullPath(root), version)))
            .OrderByDescending(
                toolchain => GetVisualStudioMajorVersion(toolchain.Root))
            .ThenByDescending(
                toolchain => ParseVersion(toolchain.MsvcVersion))
            .FirstOrDefault();
    }

    private static IEnumerable<string> FindMsvcVersions(string visualStudioRoot)
    {
        var vcvarsPath = Path.Combine(
            visualStudioRoot,
            "VC",
            "Auxiliary",
            "Build",
            "vcvarsall.bat");
        var toolsRoot = Path.Combine(visualStudioRoot, "VC", "Tools", "MSVC");
        if (!File.Exists(vcvarsPath) || !Directory.Exists(toolsRoot))
        {
            return [];
        }

        return Directory.GetDirectories(toolsRoot)
            .Select(Path.GetFileName)
            .Where(version =>
                !string.IsNullOrWhiteSpace(version) &&
                IsVisualStudioToolchain(visualStudioRoot, version!))
            .Select(version => version!);
    }

    private static bool IsVisualStudioToolchain(string root, string version) =>
        !string.IsNullOrWhiteSpace(root) &&
        !string.IsNullOrWhiteSpace(version) &&
        File.Exists(Path.Combine(root, "VC", "Auxiliary", "Build", "vcvarsall.bat")) &&
        File.Exists(Path.Combine(
            root,
            "VC",
            "Tools",
            "MSVC",
            version,
            "bin",
            "Hostx64",
            "x64",
            "link.exe"));

    private static IReadOnlyList<string> GetDefaultVisualStudioRoots()
    {
        var candidates = new List<string>();
        foreach (var programFiles in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = Path.Combine(programFiles, "Microsoft Visual Studio");
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var versionRoot in Directory.GetDirectories(root))
            {
                candidates.AddRange(Directory.GetDirectories(versionRoot));
            }
        }

        return candidates;
    }

    private static int GetVisualStudioMajorVersion(string root) =>
        int.TryParse(Directory.GetParent(root)?.Name, out var version) ? version : 0;

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static ToolchainStatusItem Status(
        string key,
        string name,
        string path,
        Func<string, bool> validator)
    {
        var available = !string.IsNullOrWhiteSpace(path) && validator(path);
        return new ToolchainStatusItem(
            key,
            name,
            path,
            available,
            available ? "已检测并固定使用此路径。" : "未检测到，请手动配置路径。");
    }

    private static string? FindJdk23(string javaRoot)
    {
        if (!Directory.Exists(javaRoot))
        {
            return null;
        }

        return Directory.GetDirectories(javaRoot, "jdk-23*")
            .Where(IsJavaHome)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FindJdk21(string javaRoot)
    {
        if (IsJavaHome(javaRoot))
        {
            return Path.GetFullPath(javaRoot);
        }

        if (!Directory.Exists(javaRoot))
        {
            return null;
        }

        return Directory.GetDirectories(javaRoot, "jdk-21*")
            .Where(IsJavaHome)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FirstExistingJavaHome(string path) =>
        IsJavaHome(path) ? Path.GetFullPath(path) : null;

    private static bool IsJavaHome(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "bin", "java.exe"));

    private static bool IsAndroidSdk(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "platform-tools", "adb.exe"));

    private static bool RepairFile(string current, string? detected, Action<string> assign) =>
        File.Exists(current) || string.IsNullOrWhiteSpace(detected)
            ? false
            : Replace(current, detected, assign);

    private static bool RepairDirectory(string current, string? detected, Action<string> assign) =>
        string.IsNullOrWhiteSpace(detected) || string.Equals(current, detected, StringComparison.OrdinalIgnoreCase)
            ? false
            : Replace(current, detected, assign);

    private static bool Replace(string current, string detected, Action<string> assign)
    {
        if (string.Equals(current, detected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        assign(Path.GetFullPath(detected));
        return true;
    }

    private static bool ReplaceValue(string current, string detected, Action<string> assign)
    {
        if (string.Equals(current, detected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        assign(detected);
        return true;
    }

    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? ResolveCommand(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, command))
            .FirstOrDefault(File.Exists);
    }

    private sealed record VisualStudioToolchain(string Root, string MsvcVersion);
}
