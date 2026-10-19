namespace AxTools.Core.Services;

/// <summary>碎片输出目录里现有的内容（只统计，不修改）。</summary>
public sealed record CrossingVoidFragmentOutputStats(int FileCount, long TotalBytes, int DirectoryCount)
{
    public static CrossingVoidFragmentOutputStats Empty { get; } = new(0, 0, 0);

    public bool IsEmpty => FileCount == 0 && DirectoryCount == 0;
}

/// <summary>
/// 《生成碎片》按大小切片时，生成前清空输出目录。
///
/// **这是删除操作**，所以安全校验全部放在删之前，且宁可拒绝也不误删：
/// 盘根、源目录本身/其内部/其上级、用户目录（含桌面/文档/下载）一律拒绝。
/// 只删输出目录里的内容，目录本身保留 —— 后面 Bandizip 直接往里写。
/// </summary>
public static class CrossingVoidFragmentOutputCleaner
{
    public static CrossingVoidFragmentOutputStats Measure(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return CrossingVoidFragmentOutputStats.Empty;
        }

        // 链接目录不能当输出目录：枚举会跟到链接目标，统计出来的不是"这个目录里的东西"。
        if ((new DirectoryInfo(outputDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return CrossingVoidFragmentOutputStats.Empty;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = 0;
        long bytes = 0;
        var directories = 0;
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", options))
        {
            files++;
            try
            {
                bytes += new FileInfo(path).Length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 统计时文件刚好被别的程序删/占用：跳过它，不要因为一个文件打不开就弹不出确认框。
            }
        }

        foreach (var _ in Directory.EnumerateDirectories(outputDirectory, "*", options))
        {
            directories++;
        }

        return new CrossingVoidFragmentOutputStats(files, bytes, directories);
    }

    /// <summary>校验并清空输出目录的内容；目录本身保留。</summary>
    public static void Clear(
        string outputDirectory,
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var output = Validate(outputDirectory, gameDirectory);
        if (!Directory.Exists(output))
        {
            return;
        }

        // 先落成数组再删：边枚举边删同一目录在 Windows 上虽然通常没事，
        // 但目录项一变枚举器就可能抛"集合已修改"，这里没必要冒这个险。
        foreach (var entry in new DirectoryInfo(output).EnumerateFileSystemInfos().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(entry, cancellationToken);
        }
    }

    /// <summary>
    /// 把路径规范化成"可比较的目录"（去掉末尾分隔符，但盘根 <c>D:\</c> 保留），
    /// 并完成所有"绝对不能清空"的判定。界面上在统计/确认之前就该先调它，
    /// 否则把输出目录误设成盘根时，光统计就会去枚举整个盘。
    /// </summary>
    public static string Validate(string outputDirectory, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("碎片输出目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new InvalidOperationException("生成碎片前必须先确定游戏包目录。");
        }

        string output;
        try
        {
            output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"碎片输出目录格式无效：{outputDirectory}", exception);
        }

        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

        if (string.Equals(output, Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能把磁盘根目录当作碎片输出目录。");
        }

        if (IsSameOrChild(output, game))
        {
            throw new InvalidOperationException("碎片输出目录不能位于游戏包目录内部（清空时会删到源文件）。");
        }

        if (IsSameOrChild(game, output))
        {
            throw new InvalidOperationException("碎片输出目录不能是游戏包目录的上级（清空时会删到源文件）。");
        }

        // 链接目录：删里面的东西会跟到链接目标，等于删了别处的文件。
        if (Directory.Exists(output) &&
            (new DirectoryInfo(output).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("碎片输出目录不能是符号链接或联接点。");
        }

        foreach (var special in GetProtectedDirectories())
        {
            if (IsSameOrChild(special, output))
            {
                throw new InvalidOperationException($"不能把用户目录当作碎片输出目录：{output}");
            }
        }

        return output;
    }

    private static IEnumerable<string> GetProtectedDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static bool IsSameOrChild(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteEntry(FileSystemInfo entry, CancellationToken cancellationToken)
    {
        // 符号链接/联接点只删链接本身，不跟进去删目标（跟进去可能删到别处的文件）。
        if (entry is DirectoryInfo directory &&
            (entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var child in directory.EnumerateFileSystemInfos().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteEntry(child, cancellationToken);
            }
        }

        entry.Attributes = FileAttributes.Normal;
        entry.Delete();
    }
}
