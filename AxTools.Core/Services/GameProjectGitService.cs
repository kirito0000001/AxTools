using System.Diagnostics;
using System.Text;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record GameProjectGitResult(bool Success, string Message);

/// <summary>
/// 游戏项目的 git 操作（安装 = clone，检查上游 = pull）。
///
/// 输出逐行转成进度消息，交给全局进度条；不解析 git 的百分比
/// （clone 的 --progress 输出是 \r 覆盖的，逐行读不划算），统一按不确定进度显示。
/// </summary>
public sealed class GameProjectGitService
{
    private readonly string _gitExecutable;

    public GameProjectGitService(string gitExecutable = "git")
    {
        _gitExecutable = gitExecutable;
    }

    /// <summary>把仓库克隆到目标目录。目标目录必须不存在或为空。</summary>
    public Task<GameProjectGitResult> CloneAsync(
        string repositoryUrl,
        string targetPath,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        RunAsync(
            ["clone", "--progress", repositoryUrl, targetPath],
            workingDirectory: null,
            "正在克隆仓库",
            progress,
            cancellationToken);

    /// <summary>在项目目录里执行 git pull（默认合并）。</summary>
    public Task<GameProjectGitResult> PullAsync(
        string projectPath,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        RunAsync(
            ["-C", projectPath, "pull"],
            workingDirectory: projectPath,
            "正在拉取上游更新",
            progress,
            cancellationToken);

    private async Task<GameProjectGitResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string stageTitle,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_gitExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        progress?.Report(new ProgressUpdate(stageTitle, 0, IsIndeterminate: true));

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errorText = new StringBuilder();
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new GameProjectGitResult(false, $"无法启动 git：{exception.Message}");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程已经退出，忽略。
            }
        });

        var stdoutTask = PumpAsync(process.StandardOutput, progress, errorText: null);
        var stderrTask = PumpAsync(process.StandardError, progress, errorText);
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(CancellationToken.None));

        if (cancellationToken.IsCancellationRequested)
        {
            return new GameProjectGitResult(false, "操作已取消。");
        }

        if (process.ExitCode != 0)
        {
            var detail = errorText.ToString().Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"git 退出码 {process.ExitCode}";
            }

            return new GameProjectGitResult(false, detail);
        }

        return new GameProjectGitResult(true, "git 操作完成。");
    }

    private static async Task PumpAsync(
        StreamReader reader,
        IProgress<ProgressUpdate>? progress,
        StringBuilder? errorText)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            errorText?.AppendLine(line);
            // git 的进度行带 \r，ReadLine 拿到的可能是一整段；截断到一行展示。
            var display = line.Replace('\r', ' ').Trim();
            if (display.Length > 160)
            {
                display = display[..160];
            }

            progress?.Report(new ProgressUpdate(display, 0, IsIndeterminate: true));
        }
    }
}
