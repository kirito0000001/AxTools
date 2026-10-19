using System.Security.Cryptography;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

/// <summary>
/// 版本标记的守卫：字节必须和发布脚本写出来的完全一致（否则 sha256 对不上清单，导入永远差一个文件）。
/// 期望哈希取自线上 0.6.0/Windows/stable 那份真实文件。
/// </summary>
public sealed class CrossingVoidVersionMarkerTests
{
    private const string ExpectedSha256 =
        "56b8ef6416c337f2d6cf7cd296cf4e3e69919559e5e122c65a625ce4bc1aa51d";

    [Fact]
    public void BuildsTheSameBytesAsThePublishScript()
    {
        var bytes = CrossingVoidVersionMarker.BuildBytes("0.6.0", "Windows", "stable");

        Assert.Equal(76, bytes.Length);
        Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(
            "{\r\n  \"version\": \"0.6.0\",\r\n  \"platform\": \"Windows\",\r\n  \"channel\": \"stable\"\r\n}",
            System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ReadsTheVersionBack()
    {
        var root = NewTempDirectory("marker-read");
        try
        {
            var path = Path.Combine(root, CrossingVoidVersionMarker.FileName);
            File.WriteAllBytes(path, CrossingVoidVersionMarker.BuildBytes("0.6.0", "Windows", "stable"));

            Assert.True(CrossingVoidVersionMarker.TryReadVersion(path, out var version));
            Assert.Equal("0.6.0", version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesAnExistingMarkerWithoutTouchingTheNetwork()
    {
        var root = NewTempDirectory("marker-reuse");
        try
        {
            var path = Path.Combine(root, CrossingVoidVersionMarker.FileName);
            File.WriteAllBytes(path, CrossingVoidVersionMarker.BuildBytes("0.6.0", "Windows", "stable"));

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var result = await CrossingVoidVersionMarker.EnsureAsync(
                root,
                "0.6.0",
                "Windows",
                "stable",
                client,
                downloadBase: "http://127.0.0.1:9");

            Assert.Equal(CrossingVoidVersionMarkerAction.Reused, result.Action);
            Assert.Null(result.Warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FallsBackToLocalBytesWhenTheDownloadSiteIsUnreachable()
    {
        var root = NewTempDirectory("marker-generate");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var result = await CrossingVoidVersionMarker.EnsureAsync(
                root,
                "0.6.0",
                "Windows",
                "stable",
                client,
                downloadBase: "http://127.0.0.1:9");

            Assert.Equal(CrossingVoidVersionMarkerAction.Generated, result.Action);
            Assert.NotNull(result.Warning);
            var bytes = File.ReadAllBytes(result.Path);
            Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("1.0.0-beta.1")]
    public void AcceptsReasonableVersions(string version) =>
        Assert.True(CrossingVoidVersionMarker.IsValidVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("../0.6.0")]
    [InlineData("0.6.0 beta")]
    public void RejectsVersionsThatCannotBeFileNames(string version) =>
        Assert.False(CrossingVoidVersionMarker.IsValidVersion(version));

    private static string NewTempDirectory(string name) =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"axtools-{name}-{Guid.NewGuid():N}")).FullName;
}
