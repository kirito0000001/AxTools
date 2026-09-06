using AxTools.Core.Models;
using AxTools.Core.Services;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class CrossingVoidPackageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AxToolsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GameDirectory_DefaultsOutputToSiblingAndRefreshesInspection()
    {
        var game = Path.Combine(_root, "CrossingVoid-Windows");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "CrossingVoid.exe"), "game");
        var settings = new CrossingVoidPackageSettings { GameVersion = "V0.5.14" };
        var viewModel = CreateViewModel(settings);

        viewModel.GameDirectory = game;
        viewModel.RefreshInspection();

        Assert.Equal(Path.Combine(_root, "CrossingVoid-GitChunks"), viewModel.OutputDirectory);
        Assert.True(viewModel.Inspection.IsValid);
        Assert.Equal("V0.5.14", viewModel.ResolvedVersion);
        Assert.True(viewModel.CanGenerate);
        Assert.Equal(game, settings.GameDirectory);
    }

    [Fact]
    public void CreateTaskDefinition_UsesSeparateArgumentsAndOverwriteSwitch()
    {
        var game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "CrossingVoid.exe"), "game");
        var output = Path.Combine(_root, "Output");
        var bz = Path.Combine(_root, "bz.exe");
        File.WriteAllText(bz, string.Empty);
        var settings = new CrossingVoidPackageSettings
        {
            GameDirectory = game,
            OutputDirectory = output,
            BandizipExecutable = bz,
            GameVersion = "V0.5.14"
        };
        var viewModel = CreateViewModel(settings);
        viewModel.RefreshInspection();

        var definition = viewModel.CreateTaskDefinition(overwrite: true);

        Assert.EndsWith(
            Path.Combine("Scripts", "Adapters", "CrossingVoidGame", "Invoke-CrossingVoidGamePackage.ps1"),
            definition.ScriptPath);
        Assert.Equal(
            ["-GameDirectory", game, "-OutputDirectory", output, "-BandizipExecutable", bz, "-GameVersion", "V0.5.14", "-Overwrite"],
            definition.Arguments);
        Assert.True(definition.IsHeavy);
    }

    [Fact]
    public void BeginAndComplete_TogglesAvailabilityAndSetsReadableStatus()
    {
        var game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "CrossingVoid.exe"), "game");
        var settings = new CrossingVoidPackageSettings
        {
            GameDirectory = game,
            OutputDirectory = Path.Combine(_root, "Output"),
            GameVersion = "V0.5.14"
        };
        var viewModel = CreateViewModel(settings);
        viewModel.RefreshInspection();

        viewModel.BeginGeneration();
        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.CanGenerate);
        viewModel.CompleteGeneration(new AxTaskResult(
            "id", AxTaskStatus.Succeeded, 0,
            DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now,
            "ok", [], []));

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.CanGenerate);
        Assert.Contains("成功", viewModel.LastResultText);
    }

    private CrossingVoidPackageViewModel CreateViewModel(CrossingVoidPackageSettings settings) =>
        new(
            settings,
            new CrossingVoidPackageInspector(),
            Path.Combine(_root, "Scripts"),
            isRunnerAvailable: true);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
