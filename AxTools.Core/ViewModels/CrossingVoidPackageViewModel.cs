using AxTools.Core.Models;
using AxTools.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxTools.Core.ViewModels;

public sealed class CrossingVoidPackageViewModel : ObservableObject
{
    private readonly CrossingVoidPackageSettings _settings;
    private readonly CrossingVoidPackageInspector _inspector;
    private readonly string _scriptsRoot;
    private readonly bool _isRunnerAvailable;
    private CrossingVoidPackageInspection _inspection = new(
        false, 0, 0, 0, 0, 0, string.Empty, ["请选择游戏包目录。"]) ;
    private bool _isRunning;
    private string _lastResultText = "尚未生成分片包。";

    public CrossingVoidPackageViewModel(
        CrossingVoidPackageSettings settings,
        CrossingVoidPackageInspector inspector,
        string scriptsRoot,
        bool isRunnerAvailable)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _scriptsRoot = Path.GetFullPath(scriptsRoot);
        _isRunnerAvailable = isRunnerAvailable;
        if (string.IsNullOrWhiteSpace(_settings.BandizipExecutable))
        {
            const string standardBandizip = @"C:\Program Files\Bandizip\bz.exe";
            if (File.Exists(standardBandizip))
            {
                _settings.BandizipExecutable = standardBandizip;
            }
        }

        if (!string.IsNullOrWhiteSpace(GameDirectory) && string.IsNullOrWhiteSpace(OutputDirectory))
        {
            SetDefaultOutputDirectory(GameDirectory);
        }

        RefreshInspection();
    }

    public string GameDirectory
    {
        get => _settings.GameDirectory;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(
                _settings.GameDirectory,
                normalized,
                _settings,
                static (model, newValue) => model.GameDirectory = newValue))
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory))
                {
                    SetDefaultOutputDirectory(normalized);
                }
                MarkInspectionStale();
            }
        }
    }

    public string OutputDirectory
    {
        get => _settings.OutputDirectory;
        set
        {
            if (SetProperty(
                _settings.OutputDirectory,
                value?.Trim() ?? string.Empty,
                _settings,
                static (model, newValue) => model.OutputDirectory = newValue))
            {
                MarkInspectionStale();
            }
        }
    }

    public string BandizipExecutable
    {
        get => _settings.BandizipExecutable;
        set => SetProperty(
            _settings.BandizipExecutable,
            value?.Trim() ?? string.Empty,
            _settings,
            static (model, newValue) => model.BandizipExecutable = newValue);
    }

    public string GameVersion
    {
        get => _settings.GameVersion;
        set
        {
            if (SetProperty(
                _settings.GameVersion,
                value?.Trim() ?? string.Empty,
                _settings,
                static (model, newValue) => model.GameVersion = newValue))
            {
                MarkInspectionStale();
            }
        }
    }

    public CrossingVoidPackageInspection Inspection
    {
        get => _inspection;
        private set
        {
            if (SetProperty(ref _inspection, value))
            {
                OnPropertyChanged(nameof(ResolvedVersion));
                OnPropertyChanged(nameof(InspectionStatus));
                OnPropertyChanged(nameof(IncludedSummary));
                OnPropertyChanged(nameof(ExcludedSummary));
                OnPropertyChanged(nameof(EstimatedChunkSummary));
                OnPropertyChanged(nameof(CanGenerate));
            }
        }
    }

    public string ResolvedVersion => Inspection.ResolvedVersion;

    public string InspectionStatus => Inspection.IsValid
        ? "预检通过，可以生成分片包。"
        : string.Join(Environment.NewLine, Inspection.Errors);

    public string IncludedSummary =>
        $"将打包 {Inspection.IncludedFileCount:N0} 个文件，共 {FormatBytes(Inspection.IncludedBytes)}";

    public string ExcludedSummary =>
        $"已排除 {Inspection.ExcludedFileCount:N0} 个文件，共 {FormatBytes(Inspection.ExcludedBytes)}";

    public string EstimatedChunkSummary =>
        $"预计约 {Inspection.EstimatedChunkCount:N0} 个分片，每片最大 500 MiB";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanGenerate));
            }
        }
    }

    public bool CanGenerate => _isRunnerAvailable && !IsRunning && Inspection.IsValid;

    public string LastResultText
    {
        get => _lastResultText;
        private set => SetProperty(ref _lastResultText, value);
    }

    public void RefreshInspection()
    {
        Inspection = _inspector.Inspect(GameDirectory, OutputDirectory, GameVersion);
        if (!string.IsNullOrWhiteSpace(Inspection.ResolvedVersion) &&
            !string.Equals(GameVersion, Inspection.ResolvedVersion, StringComparison.Ordinal))
        {
            _settings.GameVersion = Inspection.ResolvedVersion;
            OnPropertyChanged(nameof(GameVersion));
        }
    }

    public AxTaskDefinition CreateTaskDefinition(bool overwrite)
    {
        if (!Inspection.IsValid)
        {
            throw new InvalidOperationException(InspectionStatus);
        }

        var arguments = new List<string>
        {
            "-GameDirectory", Path.GetFullPath(GameDirectory),
            "-OutputDirectory", Path.GetFullPath(OutputDirectory),
            "-BandizipExecutable", BandizipExecutable,
            "-GameVersion", ResolvedVersion
        };
        if (overwrite)
        {
            arguments.Add("-Overwrite");
        }

        return new AxTaskDefinition(
            $"crossingvoid-game-package-{Guid.NewGuid():N}",
            $"零境交错 · 生成 {ResolvedVersion} 游戏分片包",
            Path.Combine(
                _scriptsRoot,
                "Adapters",
                "CrossingVoidGame",
                "Invoke-CrossingVoidGamePackage.ps1"),
            arguments,
            IsHeavy: true);
    }

    public void BeginGeneration()
    {
        IsRunning = true;
        LastResultText = "正在生成游戏分片包...";
    }

    public void CompleteGeneration(AxTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IsRunning = false;
        LastResultText = result.Status switch
        {
            AxTaskStatus.Succeeded => $"生成成功 · {result.Duration.TotalSeconds:F1}s",
            AxTaskStatus.Stopped => $"已停止 · {result.Duration.TotalSeconds:F1}s",
            _ => $"生成失败 · 退出码 {result.ExitCode}"
        };
    }

    private void SetDefaultOutputDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            OutputDirectory = Path.Combine(parent, "CrossingVoid-GitChunks");
        }
    }

    private void MarkInspectionStale()
    {
        Inspection = new CrossingVoidPackageInspection(
            false, 0, 0, 0, 0, 0, string.Empty, ["目录或版本已变化，请重新预检。"]);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
