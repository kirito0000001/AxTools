using System.Diagnostics;
using System.Text;

namespace AxTools.Core.Services;

/// <summary>
/// 《生成碎片》真正的打包实现：起 Bandizip 控制台（bz.exe）。
///
/// 参数口径抄的是已经跑通的那条命令
/// （`Scripts\Adapters\CrossingVoidGame\Invoke-CrossingVoidGamePackage.ps1` 里的
/// `c -fmt:zip -l:5 -r -y &lt;包&gt; &lt;源...&gt;`）。区别只有两点：
/// ① 不是打包整个暂存目录，而是按清单点名的相对路径打包；
/// ② 压缩档位由调用方给（影片 0 = 仅存储，其余 5 = 标准档）。
/// </summary>
public sealed class BandizipCrossingVoidArchiver : ICrossingVoidArchiver
{
    /// <summary>Bandizip 的压缩级别就是 0~9：0 = 仅存储，5 = 标准档（脚本原来用的那档）。</summary>
    private const int StoreLevel = 0;

    private const int StandardLevel = 5;

    /// <summary>
    /// Windows 命令行上限 32767 字符；留出余量免得撞上限直接启动失败。
    /// 现在一次最多也就百来个文件，正常情况下碰不到。
    /// </summary>
    private const int MaxCommandLineLength = 30000;

    private readonly string executable;

    public BandizipCrossingVoidArchiver(string executable)
    {
        this.executable = ResolveExecutable(executable);
    }

    /// <summary>档位 → Bandizip 的 <c>-l:</c> 数值。</summary>
    public static int ResolveLevel(CrossingVoidArchiveCompression compression) =>
        compression == CrossingVoidArchiveCompression.Store ? StoreLevel : StandardLevel;

    /// <summary>
    /// 拼一次压缩用的命令行（不含 exe 本身）。抽成静态方法是为了能直接钉参数，
    /// 不用真的装一个 Bandizip 就能测。
    /// </summary>
    public static List<string> BuildCompressArguments(
        string archivePath,
        IReadOnlyList<string> relativePaths,
        CrossingVoidArchiveCompression compression)
    {
        var arguments = new List<string>
        {
            "c",
            "-fmt:zip",
            $"-l:{ResolveLevel(compression)}",
            "-r",
            "-y",
            archivePath,
        };
        arguments.AddRange(relativePaths);

        var length = arguments.Sum(argument => argument.Length + 3);
        if (length > MaxCommandLineLength)
        {
            throw new InvalidOperationException(
                $"一次要打包 {relativePaths.Count} 个路径，命令行太长（约 {length} 字符）。需要拆成多个压缩包。");
        }

        return arguments;
    }

    /// <summary>
    /// 找 bz.exe：先用调用方给的，再看标准安装路径。找不到就明确报错，
    /// 不要悄悄退回某个系统 zip（会产出玩家导不进去的包）。
    /// </summary>
    public static string ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        const string standardPath = @"C:\Program Files\Bandizip\bz.exe";
        if (File.Exists(standardPath))
        {
            return standardPath;
        }

        throw new FileNotFoundException(
            "没有找到 Bandizip 控制台程序 bz.exe。请在设置里指定，或安装 Bandizip 到默认路径。",
            configured ?? standardPath);
    }

    public void CreateArchive(
        string archivePath,
        string sourceRoot,
        IReadOnlyList<string> relativePaths,
        CrossingVoidArchiveCompression compression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (relativePaths.Count == 0)
        {
            throw new ArgumentException("没有要打包的文件。", nameof(relativePaths));
        }

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"游戏包目录不存在：{sourceRoot}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);

        // 压缩包放在输出目录里，所以先删掉同名旧包：Bandizip 默认是追加/更新语义，
        // 留着旧包会让同一个文件在包里出现两份。
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        var convertPaths = relativePaths
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
            .ToList();

        // 工作目录钉在游戏包目录，传进去的就是相对路径 —— 这样压缩包内部保持原目录树，
        // 解出来正好能盖回游戏安装目录。
        Run(sourceRoot, BuildCompressArguments(archivePath, convertPaths, compression));

        if (!File.Exists(archivePath))
        {
            throw new InvalidOperationException($"Bandizip 没有产出压缩包：{archivePath}");
        }

        // 补一次完整性校验，和脚本里的做法一致（bz t）。
        // 这里失败就整个中止：宁可现在报错，也不要让用户拿着坏包去发。
        Run(sourceRoot, ["t", archivePath]);
    }

    private void Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 Bandizip：{executable}");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Bandizip 失败，退出码 {process.ExitCode}：{output.ToString().Trim()}");
        }
    }
}
