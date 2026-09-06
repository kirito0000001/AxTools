using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class ToolchainEnvironmentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxTools.Toolchains",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DetectAndApply_FindsJdk23AndroidSdkAndKnownCommandPaths()
    {
        var jdk = CreateFile(Path.Combine(_root, "Java", "jdk-23", "bin", "java.exe"));
        var sdk = CreateFile(Path.Combine(_root, "Android", "Sdk", "platform-tools", "adb.exe"));
        var pwsh = CreateFile(Path.Combine(_root, "PowerShell", "pwsh.exe"));
        var settings = new ToolchainSettings();
        var service = new ToolchainEnvironmentService(
            commandResolver: name => name == "pwsh.exe" ? pwsh : null,
            candidateRoots: new ToolchainCandidateRoots(
                JavaRoot: Path.Combine(_root, "Java"),
                AndroidSdkRoot: Path.GetDirectoryName(Path.GetDirectoryName(sdk))!));

        var result = service.DetectAndApply(settings);

        Assert.True(result.Changed);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(jdk)), settings.JavaHome);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(sdk)), settings.AndroidSdkRoot);
        Assert.Equal(pwsh, settings.PowerShellPath);
        Assert.Contains(result.Items, item => item.Key == "java" && item.IsAvailable);
        Assert.Contains(result.Items, item => item.Key == "android-sdk" && item.IsAvailable);
    }

    [Fact]
    public void DetectAndApply_PreservesExistingValidCustomJavaAndAndroidPaths()
    {
        var java = CreateFile(Path.Combine(_root, "CustomJava", "bin", "java.exe"));
        var adb = CreateFile(Path.Combine(_root, "CustomSdk", "platform-tools", "adb.exe"));
        var settings = new ToolchainSettings
        {
            JavaHome = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            AndroidSdkRoot = Path.GetDirectoryName(Path.GetDirectoryName(adb))!
        };

        var result = new ToolchainEnvironmentService(
            commandResolver: _ => null,
            candidateRoots: new ToolchainCandidateRoots("", ""))
            .DetectAndApply(settings);

        Assert.Contains(result.Items, item => item.Key == "java" && item.IsAvailable);
        Assert.Contains(result.Items, item => item.Key == "android-sdk" && item.IsAvailable);
    }

    [Fact]
    public void DetectAndApply_FindsDedicatedAndroidJdk21WithoutReplacingGeneralJdk23()
    {
        var generalJava = CreateFile(Path.Combine(_root, "Java", "jdk-23", "bin", "java.exe"));
        var androidJava = CreateFile(Path.Combine(_root, "Android", "openjdk", "jdk-21.0.8", "bin", "java.exe"));
        var settings = new ToolchainSettings();
        var service = new ToolchainEnvironmentService(
            commandResolver: _ => null,
            candidateRoots: new ToolchainCandidateRoots(
                JavaRoot: Path.Combine(_root, "Java"),
                AndroidSdkRoot: string.Empty,
                AndroidJavaRoot: Path.Combine(_root, "Android", "openjdk")));

        var result = service.DetectAndApply(settings);

        Assert.True(result.Changed);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(generalJava)), settings.JavaHome);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(androidJava)), settings.AndroidJavaHome);
        Assert.Contains(result.Items, item => item.Key == "android-java" && item.IsAvailable);
    }

    [Fact]
    public void DetectAndApply_SelectsInstalledMsvcVersionWhenVisualStudioDefaultIsMissing()
    {
        var visualStudioRoot = Path.Combine(_root, "Microsoft Visual Studio", "18", "Insiders");
        CreateFile(Path.Combine(
            visualStudioRoot,
            "VC",
            "Auxiliary",
            "Build",
            "vcvarsall.bat"));
        CreateFile(Path.Combine(
            visualStudioRoot,
            "VC",
            "Auxiliary",
            "Build",
            "Microsoft.VCToolsVersion.default.txt"));
        File.WriteAllText(
            Path.Combine(
                visualStudioRoot,
                "VC",
                "Auxiliary",
                "Build",
                "Microsoft.VCToolsVersion.default.txt"),
            "14.51.36231");
        CreateFile(Path.Combine(
            visualStudioRoot,
            "VC",
            "Tools",
            "MSVC",
            "14.44.35207",
            "bin",
            "Hostx64",
            "x64",
            "link.exe"));
        var settings = new ToolchainSettings();

        var result = new ToolchainEnvironmentService(
            commandResolver: _ => null,
            candidateRoots: new ToolchainCandidateRoots(
                JavaRoot: string.Empty,
                AndroidSdkRoot: string.Empty,
                VisualStudioRoots: [visualStudioRoot]))
            .DetectAndApply(settings);

        Assert.True(result.Changed);
        Assert.Equal(visualStudioRoot, settings.VisualStudioRoot);
        Assert.Equal("14.44.35207", settings.MsvcVersion);
        Assert.Contains(
            result.Items,
            item => item.Key == "msvc" &&
                item.IsAvailable &&
                item.CurrentVersion == "MSVC 14.44.35207");
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
