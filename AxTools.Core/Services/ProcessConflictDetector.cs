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
