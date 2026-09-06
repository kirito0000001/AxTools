using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class LogService
{
    public const int MaxSessionEntries = 300;
    public const int MaxClipboardEntryLength = 12_000;
    private const int MaxClipboardStackLines = 8;
    private const int MaxDisplayStackLines = 32;
    private const string Category = "LogAxTools";

    private static readonly Regex TokenAssignmentPattern = new(
        @"\b(?<name>AXTOOLS_GITEE_TOKEN|FANTASYTOOLS_GITEE_TOKEN|GITEE_TOKEN|GITEE_ACCESS_TOKEN|GITHUB_TOKEN|GH_TOKEN)\s*[:=]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationPattern = new(
        @"\bAuthorization\s*:\s*(?:Bearer|token)\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<DateTime> _clock;
    private readonly Func<LogKind, bool> _shouldWrite;
    private readonly Func<LogFileOptions>? _fileOptionsProvider;
    private readonly SessionLogFileWriter _fileWriter;

    public LogService()
        : this(() => DateTime.Now, _ => true)
    {
    }

    public LogService(
        Func<DateTime> clock,
        Func<LogKind, bool> shouldWrite,
        Func<LogFileOptions>? fileOptionsProvider = null,
        SessionLogFileWriter? fileWriter = null)
    {
        _clock = clock;
        _shouldWrite = shouldWrite;
        _fileOptionsProvider = fileOptionsProvider;
        _fileWriter = fileWriter ?? new SessionLogFileWriter();
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public event EventHandler<LogEntry>? EntryWritten;

    public event EventHandler<Exception>? FileWriteFailed;

    public void Write(LogKind kind, string message, Exception? exception = null)
    {
        if (!_shouldWrite(kind))
        {
            return;
        }

        var timestamp = _clock();
        var normalizedMessage = Sanitize(NormalizeMessage(message));
        var diagnosticMessage = Sanitize(NormalizeDiagnosticMessage(message));
        var firstLine = $"[{timestamp:HH:mm:ss}] {Category}: {kind}: {diagnosticMessage}";
        var displayStackLines = kind == LogKind.Error ? int.MaxValue : MaxDisplayStackLines;
        var clipboardStackLines = kind == LogKind.Error ? int.MaxValue : MaxClipboardStackLines;
        var displayText = exception is null
            ? firstLine
            : firstLine + Environment.NewLine + FormatExceptionSafely(exception, displayStackLines);
        var copyText = exception is null
            ? firstLine
            : firstLine + Environment.NewLine + FormatExceptionSafely(exception, clipboardStackLines);
        if (kind != LogKind.Error)
        {
            copyText = TrimForClipboard(copyText);
        }

        var entry = new LogEntry(
            timestamp,
            kind,
            normalizedMessage,
            displayText,
            copyText);
        Entries.Add(entry);
        while (Entries.Count > MaxSessionEntries)
        {
            Entries.RemoveAt(0);
        }

        EntryWritten?.Invoke(this, entry);
        WriteToFile(timestamp, copyText);
    }

    private void WriteToFile(DateTime timestamp, string copyText)
    {
        var options = _fileOptionsProvider?.Invoke();
        if (options is not { Enabled: true } ||
            string.IsNullOrWhiteSpace(options.ProjectRootPath))
        {
            return;
        }

        try
        {
            _fileWriter.Write(options.ProjectRootPath, timestamp, copyText);
        }
        catch (Exception exception)
        {
            FileWriteFailed?.Invoke(this, exception);
        }
    }

    private static string FormatExceptionSafely(Exception exception, int maxStackLines)
    {
        try
        {
            return FormatExceptionChain(exception, maxStackLines);
        }
        catch (Exception formattingException)
        {
            return FormatExceptionFallback(exception, formattingException);
        }
    }

    private static string FormatExceptionFallback(Exception exception, Exception formattingException)
    {
        var exceptionType = SafeExceptionTypeName(exception);
        var formatterType = SafeExceptionTypeName(formattingException);
        var originalMessage = FormatExceptionMessage(exception);
        return Sanitize(string.Join(
            Environment.NewLine,
            "    Exception formatting failed; original diagnostic retained.",
            $"    Exception={exceptionType}",
            $"    Message={originalMessage}",
            $"    FormatterException={formatterType}"));
    }

    private static string FormatExceptionChain(Exception exception, int maxStackLines)
    {
        var builder = new StringBuilder();
        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            if (depth > 0)
            {
                builder.AppendLine();
                builder.Append("    InnerException[").Append(depth).AppendLine("]:");
            }

            var message = FormatExceptionMessage(current);
            builder
                .Append("    Exception=")
                .Append(SafeExceptionTypeName(current))
                .Append(" HRESULT=0x")
                .Append(SafeHResult(current))
                .Append(" Message=");
            AppendExceptionMessage(builder, message);
            if (current is COMException comException)
            {
                builder
                    .Append("    COMErrorCode=0x")
                    .AppendLine(comException.ErrorCode.ToString("X8"));
            }

            AppendExceptionData(builder, current);
            AppendStackTrace(builder, current.StackTrace, maxStackLines);
            current = current.InnerException;
            depth++;
        }

        return Sanitize(builder.ToString().TrimEnd());
    }

    private static void AppendStackTrace(
        StringBuilder builder,
        string? stackTrace,
        int maxStackLines)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return;
        }

        var lines = stackTrace
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        builder.AppendLine("    StackTrace:");
        foreach (var line in lines.Take(maxStackLines))
        {
            builder.Append("        ").AppendLine(line.Trim());
        }

        if (lines.Length > maxStackLines)
        {
            builder
                .Append("        ... stack trace trimmed for clipboard (")
                .Append(lines.Length - maxStackLines)
                .AppendLine(" more lines in UI)");
        }
    }

    private static void AppendExceptionData(StringBuilder builder, Exception exception)
    {
        try
        {
            if (exception.Data.Count == 0)
            {
                return;
            }

            builder.AppendLine("    Data:");
            foreach (DictionaryEntry item in exception.Data)
            {
                builder
                    .Append("        ")
                    .Append(Sanitize(item.Key?.ToString() ?? "<null>"))
                    .Append('=')
                    .AppendLine(Sanitize(item.Value?.ToString() ?? "<null>"));
            }
        }
        catch (Exception dataException)
        {
            builder
                .Append("    DataReadError=")
                .AppendLine(SafeExceptionTypeName(dataException));
        }
    }

    private static string FormatExceptionMessage(Exception exception)
    {
        try
        {
            var message = exception.Message;
            return string.IsNullOrWhiteSpace(message)
                ? "<empty message>"
                : Sanitize(message.Trim());
        }
        catch (Exception formattingException)
        {
            return $"<message unavailable: {SafeExceptionTypeName(formattingException)}>";
        }
    }

    private static void AppendExceptionMessage(StringBuilder builder, string value)
    {
        var lines = value.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        builder.AppendLine(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            builder.Append("        ").AppendLine(line);
        }
    }

    private static string SafeHResult(Exception exception)
    {
        try
        {
            return exception.HResult.ToString("X8");
        }
        catch
        {
            return "????????";
        }
    }

    private static string TrimForClipboard(string value)
    {
        if (value.Length <= MaxClipboardEntryLength)
        {
            return value;
        }

        var marker = $"{Environment.NewLine}... log trimmed for clipboard ({value.Length - MaxClipboardEntryLength} more characters in UI)";
        var prefixLength = Math.Max(0, MaxClipboardEntryLength - marker.Length);
        marker = $"{Environment.NewLine}... log trimmed for clipboard ({value.Length - prefixLength} more characters in UI)";
        prefixLength = Math.Max(0, MaxClipboardEntryLength - marker.Length);
        return value[..prefixLength] + marker;
    }

    private static string NormalizeMessage(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? "<empty message>"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string NormalizeDiagnosticMessage(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? "<empty message>"
            : message.Trim();

    private static string SafeExceptionTypeName(Exception exception)
    {
        try
        {
            return exception.GetType().FullName ?? exception.GetType().Name;
        }
        catch
        {
            return "<unknown exception type>";
        }
    }

    private static string Sanitize(string value)
    {
        var withoutAssignments = TokenAssignmentPattern.Replace(
            value,
            match => $"{match.Groups["name"].Value}=<redacted>");
        return AuthorizationPattern.Replace(
            withoutAssignments,
            "Authorization: <redacted>");
    }
}
