using System.Text;

namespace AxTools.Core.Services;

public sealed record LogFileOptions(
    bool Enabled,
    string ProjectRootPath);

public sealed class SessionLogFileWriter
{
    public const int RetainedFileCount = 30;
    private const string FileSearchPattern = "LogAxTools-*.log";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly object _syncRoot = new();
    private string? _sessionFileName;

    public void Write(string projectRootPath, DateTime timestamp, string text)
    {
        var normalizedRoot = Path.GetFullPath(projectRootPath);
        lock (_syncRoot)
        {
            _sessionFileName ??= $"LogAxTools-{timestamp:yyyyMMdd-HHmmss}.log";
            var logDirectory = Path.Combine(normalizedRoot, "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, _sessionFileName);
            File.AppendAllText(
                logPath,
                text + Environment.NewLine,
                Utf8WithoutBom);
            PruneOldFiles(logDirectory);
        }
    }

    private static void PruneOldFiles(string logDirectory)
    {
        var ownFiles = new DirectoryInfo(logDirectory)
            .EnumerateFiles(FileSearchPattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in ownFiles.Skip(RetainedFileCount))
        {
            file.Delete();
        }
    }
}
