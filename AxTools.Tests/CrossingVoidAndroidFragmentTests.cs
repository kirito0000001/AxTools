using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 安卓碎片的守卫：OBB 条目名必须逐字符等于清单 path、第 1 片必须是安装包、
/// 所有 zip 条目 Store、旁车字节必须和线上那份完全一致（否则启动器校验 sha256 会失败）。
/// </summary>
public sealed class CrossingVoidAndroidFragmentTests
{
    private const string ApkName = "CrossingVoid-Android-Shipping-arm64.apk";
    private const string ObbName = "main.1.test.obb";
    private const string ArchivePrefix = "零境V0.6安卓";
    private const string PublishedSidecarSha256 =
        "22cc73d4a7cbe9156ab847800476c308d501464bea310cc6440a1813dd834d48";

    [Fact]
    public void LocatesTheMainApkAndObbAndIgnoresAfsDebugApks()
    {
        var root = CreateAndroidPackage(
            "android-locate",
            ("CrossingVoid/Content/Paks/a.ucas", "ucas"),
            ("Engine/Config/StagedBuild.ini", "ini"));
        try
        {
            File.WriteAllText(Path.Combine(root, "AFS_" + ApkName), "debug");
            Directory.CreateDirectory(Path.Combine(root, "CrossingVoid_Symbols_v1"));
            File.WriteAllText(Path.Combine(root, "CrossingVoid_Symbols_v1", "symbol.pdb"), "pdb");

            var package = CrossingVoidAndroidPackage.Locate(root);

            Assert.Equal(ObbName, package.ObbFileName);
            Assert.Equal(ApkName, package.ApkFileName);
            Assert.Equal(2, package.ObbEntries.Count);
            Assert.Equal(
                new[] { "CrossingVoid/Content/Paks/a.ucas", "Engine/Config/StagedBuild.ini" }
                    .OrderBy(path => path, StringComparer.Ordinal),
                package.ObbEntries.Select(entry => entry.Path));
            Assert.Equal(7, package.ObbBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsMultipleMainObbs()
    {
        var root = CreateAndroidPackage(
            "android-two-obb",
            ("CrossingVoid/Content/Paks/a.ucas", "ucas"));
        try
        {
            File.WriteAllText(Path.Combine(root, "main.2.test.obb"), "not a zip");
            Assert.Throws<InvalidOperationException>(() => CrossingVoidAndroidPackage.Locate(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsMultipleMainApks()
    {
        var root = CreateAndroidPackage(
            "android-two-apk",
            ("CrossingVoid/Content/Paks/a.ucas", "ucas"));
        try
        {
            File.WriteAllText(Path.Combine(root, "CrossingVoid-Android-Shipping-x86.apk"), "x86");
            Assert.Throws<InvalidOperationException>(() => CrossingVoidAndroidPackage.Locate(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildsAnInstallerSliceAndKeepsTheExactObbEntryNames()
    {
        var root = CreateAndroidPackage(
            "android-plan",
            ("CrossingVoid/Content/Paks/pakchunk0-Android.ucas", "ucas"),
            ("CrossingVoid/Content/Movies/Login_1.mp4", "mp4"),
            ("Engine/Config/StagedBuild.ini", "ini"));
        try
        {
            var plan = CrossingVoidAndroidFragmentGenerator.BuildPlan(root, "0.6.0", ArchivePrefix);

            Assert.Equal(2, plan.Slices.Count);
            Assert.Equal(1, plan.BaseSliceCount);
            Assert.Equal(6, plan.TotalCount);

            var installer = plan.Installer;
            Assert.Equal($"{ArchivePrefix}安装包.zip", installer.ArchiveName);
            Assert.Equal(3, installer.Entries.Count);
            Assert.Equal(
                new[] { ApkName, CrossingVoidVersionMarker.FileName, CrossingVoidAndroidObbSidecar.FileName },
                installer.Entries.Select(entry => entry.Path));
            Assert.Equal(
                new FileInfo(Path.Combine(root, ApkName)).Length + 76 + plan.ObbSidecarBytes.Length,
                installer.SourceBytes);

            var baseSlice = plan.Slices[1];
            Assert.Equal($"{ArchivePrefix}基础碎片001.zip", baseSlice.ArchiveName);
            Assert.Equal(
                new[]
                {
                    "CrossingVoid/Content/Movies/Login_1.mp4",
                    "CrossingVoid/Content/Paks/pakchunk0-Android.ucas",
                    "Engine/Config/StagedBuild.ini",
                },
                baseSlice.Entries.Select(entry => entry.Path));
            Assert.Equal(3, baseSlice.Entries.Count);

            using var sidecar = JsonDocument.Parse(plan.ObbSidecarBytes);
            var sidecarRoot = sidecar.RootElement;
            Assert.Equal(ObbName, sidecarRoot.GetProperty("obbFileName").GetString());
            Assert.Equal("0.6.0", sidecarRoot.GetProperty("version").GetString());
            Assert.Equal(
                CrossingVoidAndroidObbSidecar.ProductKey,
                sidecarRoot.GetProperty("productKey").GetString());
            Assert.Equal(3, sidecarRoot.GetProperty("entries").GetArrayLength());
            Assert.Equal(
                new[]
                {
                    "CrossingVoid/Content/Movies/Login_1.mp4",
                    "CrossingVoid/Content/Paks/pakchunk0-Android.ucas",
                    "Engine/Config/StagedBuild.ini",
                },
                sidecarRoot.GetProperty("entries").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SplitsBaseEntriesBySizeButNeverMergesTheInstaller()
    {
        var root = CreateAndroidPackage(
            "android-split",
            ("CrossingVoid/Content/Paks/a.ucas", "aaaa"),
            ("CrossingVoid/Content/Paks/b.ucas", "bbbb"),
            ("CrossingVoid/Content/Paks/c.ucas", "cccc"));
        try
        {
            var plan = CrossingVoidAndroidFragmentPlanner.Build(
                root,
                "0.6.0",
                ArchivePrefix,
                maxSliceBytes: 5);

            Assert.Equal(4, plan.Slices.Count);
            Assert.Equal(3, plan.BaseSliceCount);
            Assert.Equal(
                new[]
                {
                    $"{ArchivePrefix}基础碎片001.zip",
                    $"{ArchivePrefix}基础碎片002.zip",
                    $"{ArchivePrefix}基础碎片003.zip",
                },
                plan.Slices.Skip(1).Select(slice => slice.ArchiveName));
            Assert.All(plan.Slices.Skip(1), slice => Assert.Single(slice.Entries));
            Assert.Equal(3, plan.Slices[0].Entries.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WritesStoreZipsWithTheExactEntryNamesAndManifest()
    {
        var root = CreateAndroidPackage(
            "android-write",
            ("CrossingVoid/Content/Paks/pakchunk0-Android.ucas", "ucas"),
            ("CrossingVoid/Content/Movies/Login_1.mp4", "mp4"),
            ("Engine/Config/StagedBuild.ini", "ini"));
        var output = NewTempDirectory("android-write-out");
        try
        {
            var plan = CrossingVoidAndroidFragmentGenerator.BuildPlan(root, "0.6.0", ArchivePrefix);
            var result = CrossingVoidAndroidFragmentGenerator.Execute(plan, output);

            Assert.Equal(2, result.Archives.Count);
            var installerPath = result.Archives[0].ArchivePath;
            var basePath = result.Archives[1].ArchivePath;
            Assert.Equal($"{ArchivePrefix}安装包.zip", Path.GetFileName(installerPath));
            Assert.Equal($"{ArchivePrefix}基础碎片001.zip", Path.GetFileName(basePath));

            using (var installer = ZipFile.OpenRead(installerPath))
            {
                Assert.Equal(3, installer.Entries.Count);
                Assert.Equal(
                    new[] { ApkName, CrossingVoidVersionMarker.FileName, CrossingVoidAndroidObbSidecar.FileName },
                    installer.Entries.Select(entry => entry.FullName));
                Assert.Equal("apk", ReadText(installer, ApkName));
                Assert.Equal(
                    CrossingVoidVersionMarker.BuildBytes(
                        "0.6.0",
                        CrossingVoidVersionMarker.AndroidPlatform,
                        CrossingVoidVersionMarker.StableChannel),
                    ReadBytes(installer, CrossingVoidVersionMarker.FileName));
                Assert.Equal(plan.ObbSidecarBytes, ReadBytes(installer, CrossingVoidAndroidObbSidecar.FileName));
                Assert.All(installer.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
                Assert.DoesNotContain(installer.Entries, entry => entry.FullName.StartsWith("OBB/", StringComparison.Ordinal));
            }

            using (var baseZip = ZipFile.OpenRead(basePath))
            {
                Assert.Equal(3, baseZip.Entries.Count);
                Assert.Equal("mp4", ReadText(baseZip, "CrossingVoid/Content/Movies/Login_1.mp4"));
                Assert.Equal("ucas", ReadText(baseZip, "CrossingVoid/Content/Paks/pakchunk0-Android.ucas"));
                Assert.Equal("ini", ReadText(baseZip, "Engine/Config/StagedBuild.ini"));
                Assert.All(baseZip.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
                Assert.DoesNotContain(baseZip.Entries, entry => entry.FullName.StartsWith("OBB/", StringComparison.Ordinal));
            }

            var manifestPath = CrossingVoidFragmentManifestWriter.Write(output, "0.6.0", result.Archives);
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var fragments = manifest.RootElement.GetProperty("fragments");
            Assert.Equal(2, fragments.GetArrayLength());
            Assert.Equal(
                $"{ArchivePrefix}安装包.zip",
                fragments[0].GetProperty("fileName").GetString());
            Assert.Equal(3, fragments[0].GetProperty("fileCount").GetInt32());
            Assert.Equal(
                result.Archives.Sum(archive => archive.ArchiveBytes),
                manifest.RootElement.GetProperty("totalBytes").GetInt64());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void ReproducesThePublishedAndroidSidecarBytes()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "CrossingVoid.obb.0.6.0.json");
        Assert.True(File.Exists(fixturePath), $"缺少测试数据：{fixturePath}");

        using var fixture = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var root = fixture.RootElement;
        var entries = root.GetProperty("entries")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        var obbFileName = root.GetProperty("obbFileName").GetString()!;
        var version = root.GetProperty("version").GetString()!;
        var productKey = root.GetProperty("productKey").GetString()!;

        Assert.Equal(155, entries.Length);
        var bytes = CrossingVoidAndroidObbSidecar.Build(obbFileName, version, entries, productKey);

        Assert.Equal(8796, bytes.Length);
        Assert.Equal(
            PublishedSidecarSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.True(text.Contains("\r\n", StringComparison.Ordinal));
        Assert.False(text.EndsWith('\n'));

        // 顺序无关：调用方从 OBB 拿到的顺序可能不同，旁车必须稳定。
        var reversed = CrossingVoidAndroidObbSidecar.Build(
            obbFileName,
            version,
            entries.Reverse().ToArray(),
            productKey);
        Assert.Equal(bytes, reversed);
    }

    [Fact]
    public void BuildsTheTestChannelProductKey()
    {
        Assert.Equal(
            "crossingvoid-android-game-test",
            CrossingVoidAndroidObbSidecar.BuildProductKey("crossingvoid-android", "test"));
        Assert.Equal(
            CrossingVoidAndroidObbSidecar.ProductKey,
            CrossingVoidAndroidObbSidecar.BuildProductKey("crossingvoid-android", "stable"));

        var bytes = CrossingVoidAndroidObbSidecar.Build(
            ObbName,
            "0.6.0",
            ["CrossingVoid/Content/Paks/a.ucas"],
            "crossingvoid-android-game-test");
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(
            "crossingvoid-android-game-test",
            document.RootElement.GetProperty("productKey").GetString());
    }

    [Fact]
    public void RemovesThePartialArchiveWhenCancelledMidSlice()
    {
        var root = CreateAndroidPackage(
            "android-cancel",
            ("CrossingVoid/Content/Paks/a.ucas", "aaaa"),
            ("CrossingVoid/Content/Paks/b.ucas", "bbbb"),
            ("CrossingVoid/Content/Paks/c.ucas", "cccc"));
        var output = NewTempDirectory("android-cancel-out");
        try
        {
            var plan = CrossingVoidAndroidFragmentGenerator.BuildPlan(root, "0.6.0", ArchivePrefix);
            using var cancellation = new CancellationTokenSource();
            var cancelled = false;

            Assert.Throws<OperationCanceledException>(() =>
                CrossingVoidAndroidFragmentGenerator.Execute(
                    plan,
                    output,
                    progress =>
                    {
                        if (!cancelled &&
                            progress.Message.Contains("基础碎片001", StringComparison.Ordinal) &&
                            progress.Message.Contains("（2 /", StringComparison.Ordinal))
                        {
                            cancelled = true;
                            cancellation.Cancel();
                        }
                    },
                    cancellation.Token));

            Assert.True(cancelled);
            Assert.True(File.Exists(Path.Combine(output, $"{ArchivePrefix}安装包.zip")));
            Assert.False(File.Exists(Path.Combine(output, $"{ArchivePrefix}基础碎片001.zip")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    private static string CreateAndroidPackage(
        string name,
        params (string Path, string Content)[] obbEntries)
    {
        var root = NewTempDirectory(name);
        CreateObb(Path.Combine(root, ObbName), obbEntries);
        File.WriteAllText(Path.Combine(root, ApkName), "apk");
        File.WriteAllBytes(
            Path.Combine(root, CrossingVoidVersionMarker.FileName),
            CrossingVoidVersionMarker.BuildBytes(
                "0.6.0",
                CrossingVoidVersionMarker.AndroidPlatform,
                CrossingVoidVersionMarker.StableChannel));
        return root;
    }

    private static void CreateObb(string path, params (string Path, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using var output = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            output.Write(bytes, 0, bytes.Length);
        }
    }

    private static byte[] ReadBytes(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"zip 里找不到条目：{path}");
        using var input = entry.Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ReadText(ZipArchive archive, string path) =>
        Encoding.UTF8.GetString(ReadBytes(archive, path));

    private static string NewTempDirectory(string name) =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-{name}-{Guid.NewGuid():N}")).FullName;
}
