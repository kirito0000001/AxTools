using System.Diagnostics;

namespace AxTools.Core.Services;

/// <summary>
/// 打开 Rider 并载入项目。只负责“打开项目”，不负责编译或运行。
///
/// 查找顺序：JetBrains Toolbox 安装目录 → Program Files → 用户目录，
/// 任意一处找到 rider64.exe 就用它。
/// </summary>
public sealed class RiderLauncher
{
    public string? FindRiderExecutable()
    {
        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var hit = Directory
                    .EnumerateFiles(root, "rider64.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(hit))
                {
                    return hit;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 某个目录读不了就跳过，继续找下一个。
            }
        }

        return null;
    }

    public bool TryOpenProject(string projectPath, out string message)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            message = "项目路径不存在，先去「设置」里选目录。";
            return false;
        }

        var executable = FindRiderExecutable();
        if (executable is null)
        {
            message = "没找到 Rider（Toolbox / Program Files 里都没有 rider64.exe）。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = $"\"{projectPath}\"",
                UseShellExecute = true
            });
            message = "已在 Rider 中打开项目。";
            return true;
        }
        catch (Exception exception)
        {
            message = $"启动 Rider 失败：{exception.Message}";
            return false;
        }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "JetBrains", "Toolbox", "apps", "Rider");
        yield return Path.Combine(localAppData, "Programs", "Rider");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "JetBrains");
    }
}
