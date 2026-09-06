using System.Buffers.Binary;
using Xunit;

namespace AxTools.Tests;

public sealed class IconAssetContractTests
{
    [Fact]
    public void AxTools_UsesSuppliedImageAcrossWindowAndExecutableAssets()
    {
        var xaml = File.ReadAllText(FindProjectFile("MainWindow.xaml"));
        var windowCode = File.ReadAllText(FindProjectFile("MainWindow.xaml.cs"));
        var project = File.ReadAllText(FindProjectFile("AxTools.csproj"));

        Assert.Contains("ms-appx:///Assets/AxToolsIcon.png", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<SymbolIcon Symbol=\"Repair\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon", windowCode, StringComparison.Ordinal);
        Assert.Contains("Assets", windowCode, StringComparison.Ordinal);
        Assert.Contains("AxTools.ico", windowCode, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Assets\\AxTools.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("Content Include=\"Assets\\AxTools.ico\"", project, StringComparison.Ordinal);
        Assert.Contains("Content Include=\"Assets\\AxToolsIcon.png\"", project, StringComparison.Ordinal);

        var expectedPngSizes = new Dictionary<string, (int Width, int Height)>
        {
            ["Assets/AxToolsIcon.png"] = (256, 256),
            ["Assets/LockScreenLogo.scale-200.png"] = (48, 48),
            ["Assets/SplashScreen.scale-200.png"] = (1240, 600),
            ["Assets/Square150x150Logo.scale-200.png"] = (300, 300),
            ["Assets/Square44x44Logo.scale-200.png"] = (88, 88),
            ["Assets/Square44x44Logo.targetsize-24_altform-unplated.png"] = (24, 24),
            ["Assets/StoreLogo.png"] = (50, 50),
            ["Assets/Wide310x150Logo.scale-200.png"] = (620, 300)
        };

        foreach (var (relativePath, expectedSize) in expectedPngSizes)
        {
            var actualSize = ReadPngSize(FindProjectFile(relativePath));
            Assert.Equal(expectedSize, actualSize);
        }

        var iconBytes = File.ReadAllBytes(FindProjectFile("Assets/AxTools.ico"));
        Assert.True(iconBytes.Length > 6);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(0, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(2, 2)));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(4, 2)));
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        Assert.True(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static string FindProjectFile(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, normalized);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"找不到 {relativePath}。");
    }
}
