using System.ComponentModel;
using System.Diagnostics;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ProcessConflictDetector
{
    private readonly Func<IReadOnlyList<ProcessSnapshot>> _snapshotProvider;

    public ProcessConflictDetector()
        : this(CaptureRunningProcesses)
    {
    }

    public ProcessConflictDetector(
        Func<IReadOnlyList<ProcessSnapshot>> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    public IReadOnlyList<ProcessSnapshot> FindConflicts(
        IEnumerable<string> targetExecutablePaths)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetPath in targetExecutablePaths)
        {
            if (TryNormalizePath(targetPath, out var normalized))
            {
                targets.Add(normalized);
            }
        }

        if (targets.Count == 0)
        {
            return Array.Empty<ProcessSnapshot>();
        }

        return _snapshotProvider()
            .Where(snapshot =>
                TryNormalizePath(snapshot.ExecutablePath, out var normalized) &&
                targets.Contains(normalized))
            .ToArray();
    }

    public IReadOnlyList<ProcessSnapshot> FindConflictsForAction(
        ManagedToolAction action,
        string developmentExecutable,
        string releaseExecutable,
        IEnumerable<string>? protectedExecutablePaths = null)
    {
        if (action is not (
            ManagedToolAction.Build or
            ManagedToolAction.BuildAndRun or
            ManagedToolAction.ForceBuildAndRun or
            ManagedToolAction.RunDevelopment or
            ManagedToolAction.BuildFrontend or
            ManagedToolAction.PackageStable or
            ManagedToolAction.PackageBeta or
            ManagedToolAction.BuildLauncherPackage or
            ManagedToolAction.ReplaceRelease or
            ManagedToolAction.PublishDryRun or
            ManagedToolAction.Publish))
        {
            return Array.Empty<ProcessSnapshot>();
        }

        var targetPaths = new List<string>
        {
            developmentExecutable,
            releaseExecutable
        };
        if (protectedExecutablePaths is not null)
        {
            targetPaths.AddRange(protectedExecutablePaths);
        }

        return FindConflicts(targetPaths);
    }

    /// <summary>
    /// 强制结束这些进程（连同它们的子进程），返回**没能结束掉**的那些。
    /// 用于「目标程序正在运行」对话框里的"强制关闭并继续"——有时候开发版进程卡住了，
    /// 不关掉就没法重新编译启动。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> ForceClose(
        IReadOnlyList<ProcessSnapshot> conflicts) =>
        ForceClose(
            conflicts,
            snapshot =>
            {
                try
                {
                    using var process = Process.GetProcessById(snapshot.ProcessId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    return !process.HasExited;
                }
                catch (ArgumentException)
                {
                    return false; // 已经不在了
                }
                catch (InvalidOperationException)
                {
                    return false; // 查的时候刚好退出了
                }
                catch (Win32Exception)
                {
                    return true; // 权限不足之类：算没杀掉，交给调用方报告
                }
                catch (NotSupportedException)
                {
                    return true;
                }
            });

    /// <summary>测试用重载：survivedProcess 返回 true 表示"没杀掉"。</summary>
    public static IReadOnlyList<ProcessSnapshot> ForceClose(
        IReadOnlyList<ProcessSnapshot> conflicts,
        Func<ProcessSnapshot, bool> survivedProcess) =>
        conflicts.Where(survivedProcess).ToArray();

    public static IReadOnlyList<ProcessSnapshot> CaptureRunningProcesses()
    {
        var snapshots = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        snapshots.Add(new ProcessSnapshot(
                            process.Id,
                            process.ProcessName,
                            executablePath));
                    }
                }
                catch (Win32Exception)
                {
                    // Protected processes are not safe to identify by path.
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the snapshot was collected.
                }
                catch (NotSupportedException)
                {
                    // MainModule is unavailable on this process/runtime.
                }
            }
        }

        return snapshots;
    }

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            normalized = Path.GetFullPath(trimmed);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
