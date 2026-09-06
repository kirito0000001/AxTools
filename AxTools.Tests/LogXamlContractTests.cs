using System.Xml.Linq;
using Xunit;

namespace AxTools.Tests;

public sealed class LogXamlContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SettingsPage_UsesFiveOrderedLogCheckBoxesAndControlZUndo()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "SettingsPage.xaml")));
        var logExpander = document
            .Descendants(Presentation + "Expander")
            .Single(element => (string?)element.Attribute("Header") == "Log 输出");
        var bindings = logExpander
            .Descendants(Presentation + "CheckBox")
            .Select(element => (string?)element.Attribute("IsChecked"))
            .ToArray();

        Assert.Equal(
            [
                "{Binding LogEnabled, Mode=TwoWay}",
                "{Binding LogSaveToFileEnabled, Mode=TwoWay}",
                "{Binding LogUserOperations, Mode=TwoWay}",
                "{Binding LogWarnings, Mode=TwoWay}",
                "{Binding LogErrors, Mode=TwoWay}"
            ],
            bindings);
        Assert.All(
            logExpander.Descendants(Presentation + "CheckBox").Skip(1),
            element => Assert.Equal(
                "{Binding AreLogOptionsEnabled}",
                (string?)element.Attribute("IsEnabled")));

        var accelerator = document
            .Descendants(Presentation + "KeyboardAccelerator")
            .Single(element => (string?)element.Attribute("Key") == "Z");
        Assert.Equal("Control", (string?)accelerator.Attribute("Modifiers"));
        Assert.Equal(
            "UndoLogSettingKeyboardAccelerator_Invoked",
            (string?)accelerator.Attribute("Invoked"));
    }

    [Fact]
    public void SettingsPage_UsesImmediatePersistenceWithoutSaveControls()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "SettingsPage.xaml")));

        Assert.DoesNotContain(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == "保存设置");
        Assert.DoesNotContain(
            document.Descendants(Presentation + "InfoBar"),
            element => (string?)element.Attribute(Xaml + "Name") == "SaveResultInfoBar");
    }

    [Fact]
    public void MainWindow_UsesFixedLogPanelWithRequiredCommands()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        var panel = document
            .Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "LogPanelBorder");

        Assert.Equal("{StaticResource PanelBorderStyle}", (string?)panel.Attribute("Style"));
        Assert.Empty(panel.Descendants(Presentation + "Expander"));
        var scrollViewer = panel
            .Descendants(Presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "LogScrollViewer");
        Assert.Equal("150", (string?)scrollViewer.Attribute("MaxHeight"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal(
            "LogScrollViewer_PointerWheelChanged",
            (string?)scrollViewer.Attribute("PointerWheelChanged"));

        var buttonNames = panel
            .Descendants(Presentation + "Button")
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToArray();
        Assert.Equal(
            ["ScrollLogToBottomButton", "CopyAllLogButton", "ClearLogButton"],
            buttonNames);
        Assert.Contains(
            panel.Descendants(Presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Logs}");
    }

    [Fact]
    public void MainWindow_LogEntriesStretchAcrossTheAvailableWidth()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        var itemsControl = document
            .Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "LogItemsControl");
        var entryBorder = itemsControl
            .Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "LogEntryBorder");
        var entryText = entryBorder
            .Descendants(Presentation + "TextBlock")
            .Single();

        Assert.Equal("Stretch", (string?)itemsControl.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Stretch", (string?)entryBorder.Attribute("HorizontalAlignment"));
        Assert.Equal("LogEntryBorder_Tapped", (string?)entryBorder.Attribute("Tapped"));
        Assert.Equal("Wrap", (string?)entryText.Attribute("TextWrapping"));
        Assert.Equal("Stretch", (string?)entryText.Attribute("HorizontalAlignment"));
        Assert.Null(entryText.Attribute("TextTrimming"));
        Assert.Null(entryText.Attribute("MaxLines"));
        Assert.Empty(itemsControl.Descendants(Presentation + "Button"));
    }

    [Fact]
    public void ToolboxStyles_DefinesExactLightAndDarkLogResources()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Styles", "ToolboxStyles.xaml")));
        var expectedLight = new Dictionary<string, string>
        {
            ["LogDefaultForegroundBrush"] = "#FF1F1F1F",
            ["LogDefaultBackgroundBrush"] = "#FFF2F2F2",
            ["LogDefaultBorderBrush"] = "#FF7A7A7A",
            ["LogUserForegroundBrush"] = "#FF176B3A",
            ["LogUserBackgroundBrush"] = "#FFE6F4EA",
            ["LogUserBorderBrush"] = "#FF4D9A68",
            ["LogWarningForegroundBrush"] = "#FF8A5200",
            ["LogWarningBackgroundBrush"] = "#FFFFF3CD",
            ["LogWarningBorderBrush"] = "#FFB77900",
            ["LogErrorForegroundBrush"] = "#FFA61B1B",
            ["LogErrorBackgroundBrush"] = "#FFFDE2E2",
            ["LogErrorBorderBrush"] = "#FFB42318"
        };
        var expectedDark = new Dictionary<string, string>
        {
            ["LogDefaultForegroundBrush"] = "#FFE1E1E1",
            ["LogDefaultBackgroundBrush"] = "#1AFFFFFF",
            ["LogDefaultBorderBrush"] = "#41FFFFFF",
            ["LogUserForegroundBrush"] = "#FFE1E1E1",
            ["LogUserBackgroundBrush"] = "#1AFFFFFF",
            ["LogUserBorderBrush"] = "#41FFFFFF",
            ["LogWarningForegroundBrush"] = "#FFFFD700",
            ["LogWarningBackgroundBrush"] = "#2AA06E00",
            ["LogWarningBorderBrush"] = "#78DCAA28",
            ["LogErrorForegroundBrush"] = "#FFFF4500",
            ["LogErrorBackgroundBrush"] = "#34821818",
            ["LogErrorBorderBrush"] = "#96E65046"
        };

        AssertThemeBrushes(document, "Light", expectedLight);
        AssertThemeBrushes(document, "Dark", expectedDark);

        var styleKeys = document
            .Root!
            .Elements(Presentation + "Style")
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            styleKeys,
            new HashSet<string>(
            [
                "LogDefaultTextStyle", "LogUserTextStyle", "LogWarningTextStyle", "LogErrorTextStyle",
                "LogDefaultBlockStyle", "LogUserBlockStyle", "LogWarningBlockStyle", "LogErrorBlockStyle"
            ], StringComparer.Ordinal));
    }

    private static void AssertThemeBrushes(
        XDocument document,
        string themeKey,
        IReadOnlyDictionary<string, string> expected)
    {
        var theme = document
            .Descendants(Presentation + "ResourceDictionary")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == themeKey);
        var actual = theme
            .Elements(Presentation + "SolidColorBrush")
            .Where(element => ((string?)element.Attribute(Xaml + "Key"))?.StartsWith("Log", StringComparison.Ordinal) == true)
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => (string)element.Attribute("Color")!,
                StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static string FindProjectFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"找不到 {relativePath}。");
    }
}
