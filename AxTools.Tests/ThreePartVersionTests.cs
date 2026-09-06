using System.Text.Json;
using AxTools.Core.Adapters;
using AxTools.Core.Catalog;
using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class ThreePartVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AxTools-ThreePart-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V0.5.12", 0, 5, 12)]
    [InlineData("1.2.0-beta.1", 1, 2, 0)]
    public void ParseOrDefault_ReadsThreeNumericParts(string value, int major, int feature, int bugFix)
    {
        var parsed = ThreePartVersion.ParseOrDefault(value);

        Assert.Equal(major, parsed.Major);
        Assert.Equal(feature, parsed.Feature);
        Assert.Equal(bugFix, parsed.BugFix);
    }

    [Fact]
    public void ToolPageVersionParts_UpdateSettingsAndCrossingVoidProjectImmediately()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "src-tauri"));
        File.WriteAllText(Path.Combine(_root, "src-tauri", "Cargo.toml"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "Scripts"));
        File.WriteAllText(Path.Combine(_root, "Scripts", "Build-LauncherUpdaterPackage.ps1"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "Scripts", "Publish-LauncherGiteePackage.ps1"), string.Empty);
        var paths = new ManagedToolPaths { SourceRoot = _root, PublishVersion = "1.1.1" };
        var descriptor = ManagedToolCatalog.All.Single(tool => tool.Key == ManagedToolKey.CrossingVoidPc);
        var viewModel = new ToolPageViewModel(descriptor, paths, new CrossingVoidPcAdapter(Path.Combine(_root, "AxScripts")));

        viewModel.PublishVersionFeature = 2;
        viewModel.PublishVersionBugFix = 0;

        Assert.Equal("1.2.0", paths.PublishVersion);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "Saved", "Launcher", "developer-version.json")));
        Assert.Equal("1.2.0", document.RootElement.GetProperty("version").GetString());
        Assert.DoesNotContain(viewModel.PublishActions, action => action.Action == ManagedToolAction.SetVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
