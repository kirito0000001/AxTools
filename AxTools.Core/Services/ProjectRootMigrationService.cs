using System.Security.Cryptography;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class ProjectRootMigrationService : IProjectRootMigrationService
{
    public const long RequiredFreeSpaceMarginBytes = 256L * 1024 * 1024;

    private readonly Func<string, long> _getAvailableFreeSpace;

    public ProjectRootMigrationService(Func<string, long>? getAvailableFreeSpace = null)
    {
        _getAvailableFreeSpace = getAvailableFreeSpace ?? GetAvailableFreeSpace;
    }

    public async Task<ProjectRootMigrationResult> CopyAndVerifyAsync(
        string oldRoot,
        string newRoot,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(oldRoot);
        var targetRoot = Path.GetFullPath(newRoot);
        if (IsPathInsideDirectory(targetRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "新整体项目目录不能位于旧整体项目目录内部。");
        }

        if (IsPathInsideDirectory(sourceRoot, targetRoot))
        {
            throw new InvalidOperationException(
                "新整体项目目录不能包含旧整体项目目录。");
        }

        if (Directory.Exists(targetRoot) &&
            Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            throw new InvalidOperationException(
                "新整体项目目录已存在且不是空目录，请选择空目录或新的父目录。");
        }

        if (!Directory.Exists(sourceRoot))
        {
            Directory.CreateDirectory(targetRoot);
            progress?.Report(new ProgressUpdate(
                "旧目录不存在，已创建新整体项目目录。",
                94,
                targetRoot));
            return new ProjectRootMigrationResult(0, 0, 0);
        }

        progress?.Report(new ProgressUpdate(
            "正在扫描整体项目目录...",
            2,
            sourceRoot,
            true));
        var directories = Directory.GetDirectories(
            sourceRoot,
            "*",
            SearchOption.AllDirectories);
        var files = Directory.GetFiles(
            sourceRoot,
            "*",
            SearchOption.AllDirectories);
        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        var requiredBytes = checked(totalBytes + RequiredFreeSpaceMarginBytes);
        var availableBytes = _getAvailableFreeSpace(targetRoot);
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"目标磁盘可用空间不足，需要至少 {requiredBytes} 字节，当前可用 {availableBytes} 字节。");
        }

        var targetExisted = Directory.Exists(targetRoot);
        try
        {
            Directory.CreateDirectory(targetRoot);
            foreach (var sourceDirectory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(MapPath(sourceRoot, targetRoot, sourceDirectory));
            }

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = files[index];
                var targetFile = MapPath(sourceRoot, targetRoot, sourceFile);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(sourceFile, targetFile, overwrite: true);
                progress?.Report(new ProgressUpdate(
                    "正在复制整体项目文件...",
                    10 + (index + 1d) / Math.Max(files.Length, 1) * 55,
                    Path.GetRelativePath(sourceRoot, sourceFile)));
            }

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyFileAsync(
                    files[index],
                    MapPath(sourceRoot, targetRoot, files[index]),
                    cancellationToken);

                progress?.Report(new ProgressUpdate(
                    "正在校验整体项目文件...",
                    65 + (index + 1d) / Math.Max(files.Length, 1) * 29,
                    Path.GetRelativePath(sourceRoot, files[index])));
            }

            return new ProjectRootMigrationResult(
                files.Length,
                directories.Length,
                totalBytes);
        }
        catch
        {
            ResetTargetDirectory(targetRoot, targetExisted);
            throw;
        }
    }

    private static async Task VerifyFileAsync(
        string sourceFile,
        string targetFile,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourceFile);
        var targetInfo = new FileInfo(targetFile);
        if (!targetInfo.Exists || sourceInfo.Length != targetInfo.Length)
        {
            throw new IOException($"文件长度校验失败：{sourceFile}");
        }

        await using var sourceStream = File.OpenRead(sourceFile);
        await using var targetStream = File.OpenRead(targetFile);
        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
        var targetHash = await SHA256.HashDataAsync(targetStream, cancellationToken);
        if (!sourceHash.AsSpan().SequenceEqual(targetHash))
        {
            throw new IOException($"文件 SHA-256 校验失败：{sourceFile}");
        }
    }

    private static string MapPath(
        string sourceRoot,
        string targetRoot,
        string sourcePath) =>
        Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, sourcePath));

    public static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsPathInsideDirectory(
        string candidate,
        string directory)
    {
        var normalizedCandidate = NormalizePath(candidate);
        var normalizedDirectory = NormalizePath(directory);
        if (string.Equals(
            normalizedCandidate,
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(
            normalizedDirectory,
            normalizedCandidate);
        return !Path.IsPathRooted(relativePath) &&
            !relativePath.Equals("..", StringComparison.Ordinal) &&
            !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    public bool TryDeleteDirectory(string path, out string? error)
    {
        error = null;
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return true;
        }

        try
        {
            Directory.Delete(fullPath, recursive: true);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    private static void ResetTargetDirectory(string targetRoot, bool recreate)
    {
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, recursive: true);
        }

        if (recreate)
        {
            Directory.CreateDirectory(targetRoot);
        }
    }

    private static long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ??
            throw new IOException($"无法确定目标路径所在磁盘：{path}");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
