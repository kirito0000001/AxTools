using System.Runtime.InteropServices;
using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class LogServiceTests : IDisposable
{
    private static readonly DateTime FixedTime = new(2026, 8, 10, 12, 34, 56);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "AxTools.NewLogService",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(LogKind.Info)]
    [InlineData(LogKind.User)]
    [InlineData(LogKind.Warning)]
    [InlineData(LogKind.Error)]
    public void Write_UsesStableCategoryAndStrictKindFormat(LogKind kind)
    {
        var service = CreateService();

        service.Write(kind, "环境扫描完成。");

        var entry = Assert.Single(service.Entries);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(
            $"[12:34:56] LogAxTools: {kind}: 环境扫描完成。",
            entry.DisplayText);
        Assert.Equal(entry.DisplayText, entry.CopyText);
    }

    [Fact]
    public void Write_WhenPolicyRejectsKind_DoesNotCollectEntry()
    {
        var service = new LogService(
            () => FixedTime,
            kind => kind != LogKind.Warning);

        service.Write(LogKind.Warning, "这条提示已关闭。");

        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Write_KeepsOnlyNewestThreeHundredEntries()
    {
        var service = CreateService();

        for (var index = 0; index < 305; index++)
        {
            service.Write(LogKind.Info, $"消息 {index}");
        }

        Assert.Equal(300, service.Entries.Count);
        Assert.Equal("消息 5", service.Entries[0].Message);
        Assert.Equal("消息 304", service.Entries[^1].Message);
    }

    [Fact]
    public void Write_RedactsSecretsBeforeStoringOrCopying()
    {
        var service = CreateService();

        service.Write(
            LogKind.Info,
            "AXTOOLS_GITEE_TOKEN=secret-value Authorization: Bearer github-secret");

        var entry = Assert.Single(service.Entries);
        Assert.DoesNotContain("secret-value", entry.DisplayText);
        Assert.DoesNotContain("github-secret", entry.DisplayText);
        Assert.Contains("<redacted>", entry.CopyText);
    }

    [Fact]
    public void Write_FormatsExceptionChainAndPreservesCompleteErrorStack()
    {
        var stackLines = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 14).Select(index => $"   at Example.Type.Method{index}()"));
        var inner = new COMException(string.Empty, unchecked((int)0x8001010E));
        var outer = new StackedException("外层失败", inner, stackLines);
        outer.Data["SourceFile"] = "MainWindow.xaml";
        var service = CreateService();

        service.Write(LogKind.Error, "项目数据加载失败。", outer);

        var entry = Assert.Single(service.Entries);
        Assert.Contains($"Exception={typeof(StackedException).FullName}", entry.CopyText);
        Assert.Contains("Exception=System.Runtime.InteropServices.COMException", entry.CopyText);
        Assert.Contains("HRESULT=0x8001010E", entry.CopyText);
        Assert.Contains("COMErrorCode=0x8001010E", entry.CopyText);
        Assert.Contains("SourceFile=MainWindow.xaml", entry.CopyText);
        Assert.Contains("Message=<empty message>", entry.CopyText);
        Assert.Contains("Method14", entry.CopyText);
        Assert.DoesNotContain("stack trace trimmed for clipboard", entry.CopyText);
    }

    [Fact]
    public void Write_TrimsSingleCopyTextWithoutTrimmingDisplayText()
    {
        var service = CreateService();

        service.Write(LogKind.Info, new string('A', 13_000));

        var entry = Assert.Single(service.Entries);
        Assert.True(entry.DisplayText.Length > 12_000);
        Assert.True(entry.CopyText.Length <= LogService.MaxClipboardEntryLength);
        Assert.Contains("log trimmed for clipboard", entry.CopyText);
    }

    [Fact]
    public void Write_ErrorPreservesMultilineMessageAndCompleteCopyText()
    {
        var service = CreateService();
        var detail = new string('X', 13_000);

        service.Write(LogKind.Error, $"构建失败。{Environment.NewLine}{detail}");

        var entry = Assert.Single(service.Entries);
        Assert.Contains($"构建失败。{Environment.NewLine}{detail}", entry.DisplayText);
        Assert.Equal(entry.DisplayText, entry.CopyText);
        Assert.DoesNotContain("log trimmed for clipboard", entry.CopyText);
    }

    [Fact]
    public void Write_WhenExceptionFormattingFails_KeepsFallbackDiagnostic()
    {
        var service = CreateService();

        var error = Record.Exception(() =>
            service.Write(LogKind.Error, "格式化异常失败。", new ThrowingStackTraceException()));

        Assert.Null(error);
        var entry = Assert.Single(service.Entries);
        Assert.Contains("Exception formatting failed", entry.DisplayText);
        Assert.Contains(typeof(ThrowingStackTraceException).FullName!, entry.DisplayText);
        Assert.Contains("格式化异常失败。", entry.CopyText);
    }

    [Fact]
    public void FileLogging_DoesNotCreateDirectoryUntilFirstAcceptedWrite()
    {
        _ = CreateFileService(enabled: true);

        Assert.False(Directory.Exists(Path.Combine(_testRoot, "Logs")));
    }

    [Fact]
    public void FileLogging_WhenDisabled_CreatesNoDirectory()
    {
        var service = CreateFileService(enabled: false);

        service.Write(LogKind.Info, "不会保存。");

        Assert.False(Directory.Exists(Path.Combine(_testRoot, "Logs")));
    }

    [Fact]
    public void FileLogging_WritesOneUtf8SessionFileForMultipleEntries()
    {
        var service = CreateFileService(enabled: true);

        service.Write(LogKind.Info, "第一条中文日志");
        service.Write(LogKind.User, "第二条中文日志");

        var logDirectory = Path.Combine(_testRoot, "Logs");
        var logPath = Assert.Single(Directory.GetFiles(logDirectory, "LogAxTools-*.log"));
        Assert.Equal("LogAxTools-20260810-123456.log", Path.GetFileName(logPath));
        var bytes = File.ReadAllBytes(logPath);
        Assert.False(bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble));
        var text = File.ReadAllText(logPath);
        Assert.Contains("第一条中文日志", text);
        Assert.Contains("第二条中文日志", text);
    }

    [Fact]
    public void FileLogging_RetainsThirtyOwnFilesAndPreservesUnrelatedLogs()
    {
        var logDirectory = Path.Combine(_testRoot, "Logs");
        Directory.CreateDirectory(logDirectory);
        for (var index = 0; index < 31; index++)
        {
            var path = Path.Combine(logDirectory, $"LogAxTools-202607{index + 1:00}-010101.log");
            File.WriteAllText(path, index.ToString());
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 1).AddMinutes(index));
        }

        var unrelated = Path.Combine(logDirectory, "LogFantasyTools-keep.log");
        File.WriteAllText(unrelated, "keep");
        var service = CreateFileService(enabled: true);

        service.Write(LogKind.Info, "触发保留规则。");

        Assert.Equal(30, Directory.GetFiles(logDirectory, "LogAxTools-*.log").Length);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void FileLogging_WhenWriteFails_ReportsFailureWithoutThrowing()
    {
        Directory.CreateDirectory(_testRoot);
        var invalidRoot = Path.Combine(_testRoot, "project-root-is-a-file");
        File.WriteAllText(invalidRoot, "file");
        var service = new LogService(
            () => FixedTime,
            _ => true,
            () => new LogFileOptions(true, invalidRoot));
        Exception? reported = null;
        service.FileWriteFailed += (_, exception) => reported = exception;

        var exception = Record.Exception(() =>
            service.Write(LogKind.Error, "文件日志失败不影响业务。"));

        Assert.Null(exception);
        Assert.NotNull(reported);
        Assert.Single(service.Entries);
    }

    private static LogService CreateService() =>
        new(() => FixedTime, _ => true);

    private LogService CreateFileService(bool enabled) =>
        new(
            () => FixedTime,
            _ => true,
            () => new LogFileOptions(enabled, _testRoot));

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class StackedException(
        string message,
        Exception innerException,
        string stackTrace) : InvalidOperationException(message, innerException)
    {
        public override string StackTrace => stackTrace;
    }

    private sealed class ThrowingStackTraceException : Exception
    {
        public override string StackTrace => throw new InvalidOperationException("stack trace getter failed");
    }
}
