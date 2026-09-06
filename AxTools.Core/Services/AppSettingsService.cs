using System.Text.Json;
using System.Text.Json.Serialization;
using AxTools.Core.Catalog;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class AppSettingsService
{
    private readonly IAtomicTextFileService _writer;
    private readonly string _bootstrapPath;
    private readonly Func<string> _defaultProjectRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AppSettingsService(
        IAtomicTextFileService writer,
        string bootstrapPath,
        Func<string> defaultProjectRoot)
    {
        _writer = writer;
        _bootstrapPath = bootstrapPath;
        _defaultProjectRoot = defaultProjectRoot;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var projectRoot = await ResolveProjectRootAsync(cancellationToken);
        Directory.CreateDirectory(projectRoot);
        var settingsPath = SettingsPathProvider.GetSettingsPath(projectRoot);
        AppSettings settings;

        try
        {
            settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(
                    await File.ReadAllTextAsync(settingsPath, cancellationToken),
                    _jsonOptions) ?? throw new JsonException("设置文件内容为空。")
                : new AppSettings();
        }
        catch (JsonException)
        {
            var corruptPath = Path.Combine(
                projectRoot,
                $"AxTools.settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(settingsPath, corruptPath, overwrite: false);
            settings = new AppSettings();
        }

        settings.ProjectRootPath = projectRoot;
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, 9);
        settings.CrossingVoidPackage ??= new CrossingVoidPackageSettings();
        settings.ManagedTools ??= AppSettings.CreateManagedTools();
        settings.Toolchains ??= new ToolchainSettings();
        settings.UpdateConnectionTimeoutSeconds = Math.Clamp(
            settings.UpdateConnectionTimeoutSeconds,
            10,
            600);
        foreach (var descriptor in ManagedToolCatalog.All)
        {
            settings.ManagedTools.TryAdd(descriptor.StableKey, new ManagedToolPaths());
        }
        var gamePaths = GetOrAddPaths(settings.ManagedTools, "CrossingVoidGame");
        var legacyPcPaths = GetOrAddPaths(settings.ManagedTools, "CrossingVoidinitiator-PC");
        var legacyAndroidPaths = GetOrAddPaths(settings.ManagedTools, "CrossingVoidinitiator-Android");
        if (string.IsNullOrWhiteSpace(gamePaths.GamePublishVersion))
        {
            gamePaths.GamePublishVersion = string.IsNullOrWhiteSpace(legacyPcPaths.GamePublishVersion)
                ? legacyAndroidPaths.GamePublishVersion
                : legacyPcPaths.GamePublishVersion;
        }
        if (string.IsNullOrWhiteSpace(gamePaths.GamePackageRoot))
        {
            gamePaths.GamePackageRoot = legacyPcPaths.GamePackageRoot;
        }
        WorkspaceArtifactPathPolicy.Apply(projectRoot, settings.ManagedTools);
        settings.CrossingVoidPackage.OutputDirectory =
            settings.ManagedTools["CrossingVoidinitiator-PC"].OutputRoot;
        foreach (var paths in settings.ManagedTools.Values)
        {
            paths.PublishVersion = paths.PublishVersion?.Trim() ?? string.Empty;
            paths.PublishChannel = paths.PublishChannel is "stable" or "beta"
                ? paths.PublishChannel
                : "stable";
            paths.PublishReleaseNotes = paths.PublishReleaseNotes?.Trim() ?? string.Empty;
            if (paths.GamePublishChannel is not ("stable" or "beta"))
            {
                paths.GamePublishChannel = "stable";
            }
            if (paths.GamePublishPlatform is not ("Windows" or "Android"))
            {
                paths.GamePublishPlatform = "Windows";
            }
        }
        var pcPaths = settings.ManagedTools["CrossingVoidinitiator-PC"];
        var androidPaths = settings.ManagedTools["CrossingVoidinitiator-Android"];
        var axToolsPaths = settings.ManagedTools["AxTools"];
        if (string.IsNullOrWhiteSpace(axToolsPaths.PublishReleaseNotes) &&
            !string.IsNullOrWhiteSpace(settings.AxToolsReleaseNotes))
        {
            axToolsPaths.PublishReleaseNotes = settings.AxToolsReleaseNotes.Trim();
        }
        settings.AxToolsReleaseNotes = axToolsPaths.PublishReleaseNotes;
        var sharedGameVersion = string.Equals(
            pcPaths.GamePublishVersion?.Trim(),
            androidPaths.GamePublishVersion?.Trim(),
            StringComparison.OrdinalIgnoreCase)
                ? pcPaths.GamePublishVersion?.Trim() ?? string.Empty
                : string.IsNullOrWhiteSpace(pcPaths.GamePublishVersion)
                    ? androidPaths.GamePublishVersion?.Trim() ?? string.Empty
                    : string.IsNullOrWhiteSpace(androidPaths.GamePublishVersion)
                        ? pcPaths.GamePublishVersion.Trim()
                        : string.Empty;
        var sharedGameChannel = string.Equals(
            pcPaths.GamePublishChannel,
            androidPaths.GamePublishChannel,
            StringComparison.OrdinalIgnoreCase) &&
            pcPaths.GamePublishChannel == "beta"
                ? "beta"
                : "stable";
        pcPaths.GamePublishVersion = sharedGameVersion;
        androidPaths.GamePublishVersion = sharedGameVersion;
        pcPaths.GamePublishChannel = sharedGameChannel;
        androidPaths.GamePublishChannel = sharedGameChannel;
        var sharedGameReleaseNotes = string.Equals(
            pcPaths.GamePublishReleaseNotes?.Trim(),
            androidPaths.GamePublishReleaseNotes?.Trim(),
            StringComparison.Ordinal)
                ? pcPaths.GamePublishReleaseNotes?.Trim() ?? string.Empty
                : string.IsNullOrWhiteSpace(pcPaths.GamePublishReleaseNotes)
                    ? androidPaths.GamePublishReleaseNotes?.Trim() ?? string.Empty
                    : string.IsNullOrWhiteSpace(androidPaths.GamePublishReleaseNotes)
                        ? pcPaths.GamePublishReleaseNotes.Trim()
                        : string.Empty;
        pcPaths.GamePublishReleaseNotes = sharedGameReleaseNotes;
        androidPaths.GamePublishReleaseNotes = sharedGameReleaseNotes;
        SynchronizePcLauncherVersion(pcPaths);
        if (string.IsNullOrWhiteSpace(pcPaths.GamePackageRoot) &&
            !string.IsNullOrWhiteSpace(settings.CrossingVoidPackage.GameDirectory))
        {
            pcPaths.GamePackageRoot = settings.CrossingVoidPackage.GameDirectory;
        }

        await SaveAsync(settings, cancellationToken);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, 9);
        var axToolsPaths = settings.ManagedTools["AxTools"];
        if (string.IsNullOrWhiteSpace(axToolsPaths.PublishReleaseNotes) &&
            !string.IsNullOrWhiteSpace(settings.AxToolsReleaseNotes))
        {
            axToolsPaths.PublishReleaseNotes = settings.AxToolsReleaseNotes.Trim();
        }
        settings.AxToolsReleaseNotes = axToolsPaths.PublishReleaseNotes;
        WorkspaceArtifactPathPolicy.Apply(
            settings.ProjectRootPath,
            settings.ManagedTools);
        settings.CrossingVoidPackage.OutputDirectory =
            settings.ManagedTools["CrossingVoidinitiator-PC"].OutputRoot;
        var settingsJson = JsonSerializer.Serialize(settings, _jsonOptions);
        await _writer.WriteTextAsync(
            SettingsPathProvider.GetSettingsPath(settings.ProjectRootPath),
            settingsJson,
            cancellationToken);

        var bootstrapJson = JsonSerializer.Serialize(
            new BootstrapSettings { ProjectRootPath = settings.ProjectRootPath },
            _jsonOptions);
        await _writer.WriteTextAsync(_bootstrapPath, bootstrapJson, cancellationToken);
    }

    private static void SynchronizePcLauncherVersion(ManagedToolPaths paths)
    {
        if (string.IsNullOrWhiteSpace(paths.SourceRoot))
        {
            return;
        }

        var versionPath = Path.Combine(
            paths.SourceRoot,
            "Saved",
            "Launcher",
            "developer-version.json");
        if (!File.Exists(versionPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(versionPath));
            if (document.RootElement.TryGetProperty("version", out var versionElement))
            {
                var version = versionElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    paths.PublishVersion = version;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static ManagedToolPaths GetOrAddPaths(
        Dictionary<string, ManagedToolPaths> paths,
        string key)
    {
        if (!paths.TryGetValue(key, out var value) || value is null)
        {
            value = new ManagedToolPaths();
            paths[key] = value;
        }
        return value;
    }

    private async Task<string> ResolveProjectRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_bootstrapPath))
        {
            return _defaultProjectRoot();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_bootstrapPath, cancellationToken);
            var bootstrap = JsonSerializer.Deserialize<BootstrapSettings>(json, _jsonOptions);
            return string.IsNullOrWhiteSpace(bootstrap?.ProjectRootPath)
                ? _defaultProjectRoot()
                : Path.GetFullPath(bootstrap.ProjectRootPath);
        }
        catch (JsonException)
        {
            return _defaultProjectRoot();
        }
    }
}
