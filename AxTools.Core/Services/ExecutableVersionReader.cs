using System.Diagnostics;

namespace AxTools.Core.Services;

public static class ExecutableVersionReader
{
    public static string Read(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) { return "未配置"; }
        try
        {
            var path = Path.GetFullPath(executablePath);
            if (!File.Exists(path)) { return "文件不存在"; }
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.ProductVersion?.Split('+', 2)[0];
            if (string.IsNullOrWhiteSpace(version)) { version = info.FileVersion; }
            return string.IsNullOrWhiteSpace(version) ? "未知" : version;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "路径无效";
        }
    }
}
