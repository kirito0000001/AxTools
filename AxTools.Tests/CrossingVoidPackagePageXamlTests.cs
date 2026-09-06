using System.Xml.Linq;
using Xunit;

namespace AxTools.Tests;

public sealed class CrossingVoidPackagePageXamlTests
{
    [Fact]
    public void MainWindow_RemovesLegacyCrossingVoidGameNavigation()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(document.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "CrossingVoidGameNavItem");
        Assert.DoesNotContain(document.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "CrossingVoidPackagePage");
    }

    [Fact]
    public void PackagePage_ContainsRequiredInputsSummariesAndSinglePrimaryAction()
    {
        var document = XDocument.Load(FindProjectFile("Views", "CrossingVoidPackagePage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("GameDirectoryTextBox", names);
        Assert.Contains("OutputDirectoryTextBox", names);
        Assert.Contains("BandizipTextBox", names);
        Assert.Contains("GameVersionTextBox", names);
        Assert.Contains("InspectionInfoBar", names);
        Assert.Contains("IncludedSummaryText", names);
        Assert.Contains("ExcludedSummaryText", names);
        Assert.Contains("EstimatedChunksText", names);
        var generateButton = document.Descendants().Single(
            element => (string?)element.Attribute(xaml + "Name") == "GeneratePackageButton");
        Assert.Equal("{StaticResource AccentButtonStyle}", (string?)generateButton.Attribute("Style"));
        Assert.Equal(3, document.Descendants().Count(element =>
            (string?)element.Attribute("ToolTipService.ToolTip") is not null));
    }

    private static string FindProjectFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine([root, .. segments]);
    }
}
