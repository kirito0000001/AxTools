using System.Text.Json;
using System.Diagnostics;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class SystemEnvironmentInventoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxTools.EnvironmentInventory",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateInventory_DescribesUnrealWindowsAndAndroidVersionsWithConsumers()
    {
        var paths = CreateEnvironmentTree();
        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings
            {
                JavaHome = Path.Combine(_root, "Java", "jdk-23"),
                AndroidSdkRoot = paths.AndroidSdkRoot
            },
            paths,
            []);

        var unreal = Assert.Single(inventory, item => item.Key == "unreal-engine-5.6.1");
        Assert.Equal("5.6.1", unreal.CurrentVersion);
        Assert.Equal(EnvironmentHealthState.Recommended, unreal.Health);
        Assert.Contains(unreal.Consumers!, consumer => consumer.Name == "CrossingVoid");
        Assert.Contains(unreal.Consumers!, consumer =>
            consumer.Name == "ZD空界幻境 Unreal 同步" &&
            consumer.Requirement.Contains("只读", StringComparison.Ordinal));
        Assert.Contains(unreal.EvidencePaths!, path => path.EndsWith("CrossingVoid.uproject"));

        var msvc = Assert.Single(inventory, item => item.Key == "msvc-v143");
        Assert.Equal("14.44.35207", msvc.CurrentVersion);
        Assert.Contains("14.44", msvc.RecommendedVersion);
        Assert.Contains(msvc.Consumers!, consumer => consumer.Name == "CrossingVoid");

        var windowsSdk = Assert.Single(inventory, item => item.Key == "windows-sdk");
        Assert.Contains("10.0.22621.0", windowsSdk.CurrentVersion);
        Assert.Equal("10.0.22621.0", windowsSdk.RecommendedVersion);

        var ndk = Assert.Single(inventory, item => item.Key == "android-ndk");
        Assert.Contains("r25b", ndk.CurrentVersion);
        Assert.Contains("r28c", ndk.CurrentVersion);
        Assert.Contains("r25b", ndk.RecommendedVersion);
        Assert.Contains("r27c", ndk.RecommendedVersion);
        Assert.Equal(EnvironmentHealthState.Compatible, ndk.Health);
        Assert.Contains(ndk.Consumers!, consumer =>
            consumer.Name == "CrossingVoid" && consumer.UsageStatus == "已确认使用");
        Assert.Contains(ndk.EvidencePaths!, path => path.EndsWith("CrossingVoid.log"));

        var jbr = Assert.Single(inventory, item => item.Key == "android-studio-jbr");
        Assert.Equal("21.0.3", jbr.CurrentVersion);
        Assert.Contains(jbr.Consumers!, consumer => consumer.Name == "CrossingVoid");
    }

    [Fact]
    public void CreateInventory_ShowsGlobalJavaConflictWithoutClaimingAndroidUsesJdk23()
    {
        var paths = CreateEnvironmentTree() with
        {
            GlobalJavaHome = Path.Combine(_root, "Java", "jdk-1.8")
        };
        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings
            {
                JavaHome = Path.Combine(_root, "Java", "jdk-23"),
                AndroidSdkRoot = paths.AndroidSdkRoot
            },
            paths,
            []);

        var java = Assert.Single(inventory, item => item.Key == "managed-java");
        Assert.Equal(EnvironmentHealthState.Warning, java.Health);
        Assert.Contains("全局 JAVA_HOME", java.Message);
        Assert.Contains("jdk-1.8", java.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(java.Consumers!);
    }

    [Fact]
    public void CreateInventory_UsesExecutableProductVersionForManagedCommands()
    {
        var paths = CreateEnvironmentTree();
        var executable = typeof(SystemEnvironmentInventoryServiceTests).Assembly.Location;
        var expected = FileVersionInfo.GetVersionInfo(executable).ProductVersion;

        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings
            {
                JavaHome = Path.Combine(_root, "Java", "jdk-23"),
                AndroidSdkRoot = paths.AndroidSdkRoot
            },
            paths,
            [new ToolchainStatusItem("dotnet", ".NET SDK", executable, true, "ok")]);

        var dotnet = Assert.Single(inventory, item => item.Key == "dotnet");
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, dotnet.CurrentVersion);
        Assert.Contains(dotnet.Consumers!, consumer =>
            consumer.Name == "ZD空界幻境" &&
            consumer.EvidencePath.EndsWith("CrossingVoidZDTool.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateInventory_HandlesMissingManagedJavaWithoutThrowing()
    {
        var paths = CreateEnvironmentTree() with
        {
            GlobalJavaHome = Path.Combine(_root, "Java", "jdk-1.8")
        };

        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings { AndroidSdkRoot = paths.AndroidSdkRoot },
            paths,
            []);

        var java = Assert.Single(inventory, item => item.Key == "managed-java");
        Assert.Equal(EnvironmentHealthState.Missing, java.Health);
        Assert.False(java.IsAvailable);
    }

    [Fact]
    public void CreateInventory_ListsInstalledDotNetSdksAndOmitsDuplicateJavaAndAndroidRoots()
    {
        var paths = CreateEnvironmentTree();
        Directory.CreateDirectory(Path.Combine(paths.DotNetRoot, "sdk", "8.0.423"));
        Directory.CreateDirectory(Path.Combine(paths.DotNetRoot, "sdk", "10.0.302"));

        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings
            {
                JavaHome = Path.Combine(_root, "Java", "jdk-23"),
                AndroidSdkRoot = paths.AndroidSdkRoot
            },
            paths,
            [
                new ToolchainStatusItem("dotnet", ".NET SDK", Path.Combine(paths.DotNetRoot, "dotnet.exe"), true, "ok"),
                new ToolchainStatusItem("java", "JDK", Path.Combine(_root, "Java", "jdk-23"), true, "ok"),
                new ToolchainStatusItem("android-sdk", "Android SDK", paths.AndroidSdkRoot, true, "ok")
            ]);

        var dotnet = Assert.Single(inventory, item => item.Key == "dotnet");
        Assert.Contains("8.0.423", dotnet.CurrentVersion);
        Assert.Contains("10.0.302", dotnet.CurrentVersion);
        Assert.Contains("8.0", dotnet.RecommendedVersion);
        Assert.DoesNotContain(inventory, item => item.Key == "java");
        Assert.DoesNotContain(inventory, item => item.Key == "android-sdk");
    }

    [Fact]
    public void CreateInventory_SeparatesAndroidJdk21FromGeneralManagedJdk()
    {
        var paths = CreateEnvironmentTree();
        var generalJava = Path.Combine(_root, "Java", "jdk-23");
        var androidJava = Path.Combine(_root, "Android", "jdk-21");
        WriteFile(Path.Combine(generalJava, "release"), "JAVA_VERSION=\"23.0.1\"\n");
        WriteFile(Path.Combine(androidJava, "release"), "JAVA_VERSION=\"21.0.8\"\n");
        Directory.CreateDirectory(Path.Combine(androidJava, "bin"));

        var inventory = new SystemEnvironmentInventoryService().CreateInventory(
            new ToolchainSettings
            {
                JavaHome = generalJava,
                AndroidJavaHome = androidJava,
                AndroidSdkRoot = paths.AndroidSdkRoot
            },
            paths,
            []);

        var android = Assert.Single(inventory, item => item.Key == "managed-android-java");
        Assert.Equal("21.0.8", android.CurrentVersion);
        Assert.Contains(android.Consumers!, consumer =>
            consumer.Name == "零境启动器 Android" && consumer.Requirement.Contains("21"));
        var general = Assert.Single(inventory, item => item.Key == "managed-java");
        Assert.DoesNotContain(general.Consumers!, consumer => consumer.Name == "零境启动器 Android");
    }

    private EnvironmentInventoryPaths CreateEnvironmentTree()
    {
        var engine = Path.Combine(_root, "UnrealEngine-release");
        WriteJson(Path.Combine(engine, "Engine", "Build", "Build.version"), new
        {
            MajorVersion = 5,
            MinorVersion = 6,
            PatchVersion = 1
        });
        WriteJson(Path.Combine(engine, "Engine", "Config", "Windows", "Windows_SDK.json"), new
        {
            MainVersion = "10.0.22621.0",
            PreferredVisualCppVersions = new[] { "14.38.33130-14.38.99999" },
            MinimumVisualCppVersion = "14.38.33130"
        });
        WriteJson(Path.Combine(engine, "Engine", "Config", "Android", "Android_SDK.json"), new
        {
            MainVersion = "r27c",
            MinVersion = "r25b",
            MaxVersion = "r29",
            platforms = "android-34",
            build_tools = "35.0.1",
            cmake = "3.22.1",
            ndk = "27.2.12479018"
        });

        var crossing = Path.Combine(_root, "CrossingVoid");
        WriteJson(Path.Combine(crossing, "CrossingVoid.uproject"), new
        {
            EngineAssociation = "test-engine"
        });
        WriteFile(
            Path.Combine(crossing, "Config", "DefaultEngine.ini"),
            "[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]\nMinSDKVersion=26\nTargetSDKVersion=26\n");
        WriteFile(
            Path.Combine(crossing, "Saved", "Logs", "CrossingVoid.log"),
            "Turnkey Platform: Android: (Status=Valid, Current_Sdk=r25b, Allowed_AutoSdk=r27c)\n");

        var vs2022 = Path.Combine(_root, "VS2022", "BuildTools");
        Directory.CreateDirectory(Path.Combine(vs2022, "VC", "Tools", "MSVC", "14.44.35207"));
        var vs2026 = Path.Combine(_root, "VS2026", "Community");
        Directory.CreateDirectory(Path.Combine(vs2026, "VC", "Tools", "MSVC", "14.51.36231"));
        var windowsKits = Path.Combine(_root, "Windows Kits", "10");
        Directory.CreateDirectory(Path.Combine(windowsKits, "Lib", "10.0.22621.0"));
        Directory.CreateDirectory(Path.Combine(windowsKits, "Lib", "10.0.26100.0"));

        var androidSdk = Path.Combine(_root, "Android", "Sdk");
        WriteFile(Path.Combine(androidSdk, "ndk", "25.1.8937393", "source.properties"),
            "Pkg.Revision = 25.1.8937393\n");
        WriteFile(Path.Combine(androidSdk, "ndk", "28.2.13676358", "source.properties"),
            "Pkg.Revision = 28.2.13676358\nPkg.ReleaseName = r28c\n");
        Directory.CreateDirectory(Path.Combine(androidSdk, "platforms", "android-34"));
        Directory.CreateDirectory(Path.Combine(androidSdk, "platforms", "android-36"));
        Directory.CreateDirectory(Path.Combine(androidSdk, "build-tools", "35.0.0"));
        Directory.CreateDirectory(Path.Combine(androidSdk, "cmake", "3.22.1"));

        var studio = Path.Combine(_root, "Android Studio");
        WriteFile(Path.Combine(studio, "jbr", "release"), "JAVA_VERSION=\"21.0.3\"\n");
        Directory.CreateDirectory(Path.Combine(_root, "Java", "jdk-23", "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "Java", "jdk-1.8", "bin"));

        return new EnvironmentInventoryPaths(
            UnrealEngineRoot: engine,
            CrossingVoidRoot: crossing,
            FantasyProjectRoot: Path.Combine(_root, "FantasyProject"),
            VisualStudio2022Root: vs2022,
            VisualStudio2022Version: "17.14.37",
            VisualStudio2026Root: vs2026,
            VisualStudio2026Version: "18.8.2",
            WindowsKitsRoot: windowsKits,
            AndroidStudioRoot: studio,
            AndroidSdkRoot: androidSdk,
            DotNetRoot: Path.Combine(_root, "dotnet"),
            GlobalJavaHome: string.Empty);
    }

    private static void WriteJson(string path, object value) =>
        WriteFile(path, JsonSerializer.Serialize(value));

    private static void WriteFile(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
