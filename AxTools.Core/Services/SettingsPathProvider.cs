namespace AxTools.Core.Services;

public sealed class SettingsPathProvider
{
    private readonly string _roamingAppDataRoot;
    private readonly Func<string, bool> _driveExists;

    public SettingsPathProvider()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Directory.Exists)
    {
    }

    public SettingsPathProvider(string roamingAppDataRoot, Func<string, bool> driveExists)
    {
        _roamingAppDataRoot = roamingAppDataRoot;
        _driveExists = driveExists;
    }

    public string GetDefaultProjectRoot()
    {
        const string driveRoot = @"D:\";
        if (!_driveExists(driveRoot))
        {
            throw new DriveNotFoundException("D 盘不存在或不可访问，请选择 Ax工具箱项目位置。");
        }

        return Path.Combine(driveRoot, "Ax工具箱项目");
    }

    public string GetBootstrapPath() =>
        Path.Combine(_roamingAppDataRoot, "AxTools", "bootstrap.json");

    public static string GetSettingsPath(string projectRoot) =>
        Path.Combine(projectRoot, "AxTools.settings.json");
}
