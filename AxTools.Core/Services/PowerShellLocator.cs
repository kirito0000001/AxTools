namespace AxTools.Core.Services;

public sealed class PowerShellLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly string _programFiles;
    private readonly string _localAppData;
    private readonly string _pathEnvironment;

    public PowerShellLocator()
        : this(
            File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
    {
    }

    public PowerShellLocator(
        Func<string, bool> fileExists,
        string programFiles,
        string localAppData,
        string pathEnvironment)
    {
        _fileExists = fileExists;
        _programFiles = programFiles;
        _localAppData = localAppData;
        _pathEnvironment = pathEnvironment;
    }

    public string FindPowerShell()
    {
        foreach (var candidate in GetCandidates())
        {
            if (_fileExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "未找到 PowerShell 7 (pwsh.exe)。请安装 PowerShell 7 后重新运行任务。");
    }

    private IEnumerable<string> GetCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_programFiles))
        {
            yield return Path.Combine(_programFiles, "PowerShell", "7", "pwsh.exe");
        }

        if (!string.IsNullOrWhiteSpace(_localAppData))
        {
            yield return Path.Combine(
                _localAppData,
                "Microsoft",
                "WindowsApps",
                "pwsh.exe");
        }

        foreach (var directory in _pathEnvironment.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, "pwsh.exe");
        }
    }
}
