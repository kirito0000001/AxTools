using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record ManagedRepositoryDefinition(
    ManagedToolKey ToolKey,
    string RepositoryName,
    string RepositoryUrl,
    string DirectoryName,
    IReadOnlyList<string> ExpectedMarkers)
{
    public string GiteeReleasesUrl =>
        $"https://gitee.com/xiaojie578/{RepositoryName[(RepositoryName.LastIndexOf('/') + 1)..]}/releases";
}

public static class ManagedRepositoryCatalog
{
    private static readonly IReadOnlyDictionary<ManagedToolKey, ManagedRepositoryDefinition> Repositories =
        new Dictionary<ManagedToolKey, ManagedRepositoryDefinition>
        {
            [ManagedToolKey.AxTools] = Definition(ManagedToolKey.AxTools, "AxTools", "AxTools", ["AxTools.csproj", "AxTools.sln"]),
            [ManagedToolKey.FantasyTools] = Definition(ManagedToolKey.FantasyTools, "FantasyTools", "FantasyTools", ["FantasyTools.csproj", Path.Combine("Scripts", "打包工具箱.ps1")]),
            [ManagedToolKey.GalExcleTools] = Definition(ManagedToolKey.GalExcleTools, "TFACStorybox", "GalExcleTools", ["GalExcleTools.csproj", "GalExcleTools.sln"]),
            [ManagedToolKey.CrossingVoidZDTool] = Definition(ManagedToolKey.CrossingVoidZDTool, "CrossingVoidZDTool", "CrossingVoidZDTool", ["CrossingVoidZDTool.csproj"]),
            [ManagedToolKey.FantasyProjectPc] = Definition(ManagedToolKey.FantasyProjectPc, "FantasyProject-PC", "FantasyProject-PC", ["package.json", Path.Combine("src-tauri", "Cargo.toml")]),
            [ManagedToolKey.CrossingVoidPc] = Definition(ManagedToolKey.CrossingVoidPc, "CrossingVoid-Downloader-PC", "CrossingVoidinitiator-PC", ["package.json", Path.Combine("src-tauri", "Cargo.toml")]),
            [ManagedToolKey.CrossingVoidAndroid] = Definition(ManagedToolKey.CrossingVoidAndroid, "CrossingVoid-Downloader-Android", "CrossingVoidinitiator-Android", ["package.json", Path.Combine("android", "gradlew.bat")])
            , [ManagedToolKey.FantasyGame] = Definition(ManagedToolKey.FantasyGame, "FantasyGame", "FantasyGame", [])
            , [ManagedToolKey.FantasyAndroid] = Definition(ManagedToolKey.FantasyAndroid, "FantasyProject-Downloader-Android", "FantasyProject-Downloader-Android", ["package.json"])
            , [ManagedToolKey.CrossingVoidGame] = Definition(ManagedToolKey.CrossingVoidGame, "CrossingVoid", "CrossingVoid", [])
        };

    public static ManagedRepositoryDefinition Get(ManagedToolKey key) =>
        Repositories.TryGetValue(key, out var definition)
            ? definition
            : throw new NotSupportedException($"{key} 没有固定的 GitHub 仓库配置。");

    private static ManagedRepositoryDefinition Definition(
        ManagedToolKey key,
        string repository,
        string directory,
        IReadOnlyList<string> markers) =>
        new(
            key,
            $"kirito0000001/{repository}",
            $"https://github.com/kirito0000001/{repository}.git",
            directory,
            markers);
}
