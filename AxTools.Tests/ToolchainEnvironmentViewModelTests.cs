using AxTools.Core.Models;
using AxTools.Core.ViewModels;
using Xunit;

namespace AxTools.Tests;

public sealed class ToolchainEnvironmentViewModelTests
{
    [Fact]
    public void ReplaceItems_UpdatesStatusAndItemsWithoutReplacingSettings()
    {
        var settings = new ToolchainSettings();
        var viewModel = new ToolchainEnvironmentViewModel(settings);

        viewModel.ReplaceItems(
            [new ToolchainStatusItem("dotnet", ".NET", @"C:\dotnet", true, "ok")],
            "检测到 .NET 版本变化");

        Assert.Same(settings, viewModel.Settings);
        Assert.Equal("dotnet", Assert.Single(viewModel.Items).Key);
        Assert.Equal("检测到 .NET 版本变化", viewModel.ChangeSummary);
        Assert.Contains("1 个环境组件", viewModel.ScanStatus);
    }
}
