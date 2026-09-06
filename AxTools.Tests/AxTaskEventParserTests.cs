using AxTools.Core.Models;
using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class AxTaskEventParserTests
{
    private readonly AxTaskEventParser _parser = new();

    [Fact]
    public void Parse_ValidProgressEvent_PreservesChineseAndClampsPercent()
    {
        var parsed = _parser.Parse(
            "::axtools {\"type\":\"progress\",\"stage\":\"build\",\"percent\":125,\"message\":\"正在编译工具箱\",\"detail\":\"第 2 步\"}");

        Assert.True(parsed.IsProtocolLine);
        Assert.Null(parsed.ProtocolError);
        Assert.NotNull(parsed.Event);
        Assert.Equal(AxTaskEventType.Progress, parsed.Event.Type);
        Assert.Equal("build", parsed.Event.Stage);
        Assert.Equal(100, parsed.Event.Percent);
        Assert.Equal("正在编译工具箱", parsed.Event.Message);
        Assert.Equal("第 2 步", parsed.Event.Detail);
    }

    [Theory]
    [InlineData("stage", AxTaskEventType.Stage)]
    [InlineData("artifact", AxTaskEventType.Artifact)]
    [InlineData("warning", AxTaskEventType.Warning)]
    [InlineData("result", AxTaskEventType.Result)]
    [InlineData("cancellation", AxTaskEventType.Cancellation)]
    public void Parse_KnownTypes_ReturnsStructuredEvent(
        string type,
        AxTaskEventType expected)
    {
        var parsed = _parser.Parse($"::axtools {{\"type\":\"{type}\",\"message\":\"ok\"}}");

        Assert.Equal(expected, parsed.Event?.Type);
        Assert.Null(parsed.ProtocolError);
    }

    [Fact]
    public void Parse_NormalText_RemovesAnsiAndDoesNotCreateEvent()
    {
        var parsed = _parser.Parse("\u001b[31m普通 PowerShell 输出\u001b[0m");

        Assert.False(parsed.IsProtocolLine);
        Assert.Null(parsed.Event);
        Assert.Null(parsed.ProtocolError);
        Assert.Equal("普通 PowerShell 输出", parsed.Text);
    }

    [Theory]
    [InlineData("::axtools not-json", "事件 JSON 无效")]
    [InlineData("::axtools {\"message\":\"missing\"}", "缺少 type")]
    [InlineData("::axtools {\"type\":\"unknown\"}", "未知事件类型")]
    public void Parse_InvalidProtocol_ReturnsErrorWithoutInventingSuccess(
        string line,
        string expectedError)
    {
        var parsed = _parser.Parse(line);

        Assert.True(parsed.IsProtocolLine);
        Assert.Null(parsed.Event);
        Assert.Contains(expectedError, parsed.ProtocolError);
        Assert.Equal(line, parsed.Text);
    }

    [Fact]
    public void Parse_ResultAndCancellation_ReadsStableFields()
    {
        var result = _parser.Parse(
            "::axtools {\"type\":\"result\",\"status\":\"success\",\"exitCode\":0}");
        var cancellation = _parser.Parse(
            "::axtools {\"type\":\"cancellation\",\"mode\":\"locked\",\"message\":\"正在提交\"}");

        Assert.Equal(AxTaskResultStatus.Succeeded, result.Event?.ResultStatus);
        Assert.Equal(0, result.Event?.ExitCode);
        Assert.Equal(AxTaskCancellationMode.Locked, cancellation.Event?.CancellationMode);
        Assert.Equal("正在提交", cancellation.Event?.Message);
    }
}
