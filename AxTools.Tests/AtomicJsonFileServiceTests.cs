using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AtomicJsonFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteTextAsync_ReplacesExistingContentAndRemovesTemporaryFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "old");
        var service = new AtomicJsonFileService();

        await service.WriteTextAsync(path, "new", CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
