using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class GlobalProgressViewModelTests
{
    [Fact]
    public void Report_ClampsPercentAndNormalizesDetail()
    {
        var viewModel = new GlobalProgressViewModel();
        viewModel.Start("构建 FantasyTools", "准备环境");

        viewModel.Report(new ProgressUpdate("正在编译...", 145, "line1\r\nline2"));

        Assert.Equal(100, viewModel.Percent);
        Assert.Equal("100%", viewModel.PercentText);
        Assert.Equal("line1  line2", viewModel.Detail);
        Assert.True(viewModel.IsVisible);
    }

    [Fact]
    public void Complete_SetsFinalState()
    {
        var viewModel = new GlobalProgressViewModel();
        viewModel.Start("构建", "准备");

        viewModel.Complete("构建完成", "AxTools.exe");

        Assert.Equal(100, viewModel.Percent);
        Assert.Equal("构建完成", viewModel.Title);
        Assert.Equal("AxTools.exe", viewModel.Detail);
        Assert.False(viewModel.CanCancel);
    }

    /// <summary>
    /// 阶段总时长未知时进度条是滚动条，此时显示 "0%" 会让人以为卡死了。
    /// </summary>
    [Fact]
    public void IndeterminateStage_ReplacesMeaninglessZeroPercent()
    {
        var viewModel = new GlobalProgressViewModel();
        viewModel.Start("导入新构建", "正在准备...");

        viewModel.IsIndeterminate = true;

        Assert.Equal("—", viewModel.PercentText);

        viewModel.Report(new ProgressUpdate("正在同步产物文件 40/100", 40));

        Assert.Equal("40%", viewModel.PercentText);
    }
}
