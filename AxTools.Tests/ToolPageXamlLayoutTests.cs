using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace AxTools.Tests;

public sealed class ToolPageXamlLayoutTests
{
    [Fact]
    public void MainNavigation_UsesChineseToolTitlesAndApplicationIcons()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expected = new Dictionary<string, (string Title, string Icon)>
        {
            ["AxToolsNavItem"] = ("Ax工具箱", "AxTools.png"),
            ["FantasyToolsNavItem"] = ("幻杀工具箱", "FantasyTools.png"),
            ["GalExcleToolsNavItem"] = ("剧情工具箱", "GalExcleTools.png"),
            ["CrossingVoidZDToolNavItem"] = ("ZD空界幻境", "CrossingVoidZDTool.png"),
            ["FantasyProjectPcNavItem"] = ("幻杀启动器 PC", "FantasyProjectPc.png"),
            ["CrossingVoidPcNavItem"] = ("零境启动器 PC", "CrossingVoidPc.png"),
            ["CrossingVoidAndroidNavItem"] = ("零境启动器 Android", "CrossingVoidAndroid.png")
        };

        foreach (var item in document.Descendants(presentation + "NavigationViewItem"))
        {
            var name = (string?)item.Attribute(xaml + "Name");
            if (name is null || !expected.TryGetValue(name, out var value)) { continue; }
            Assert.Equal(value.Title, (string?)item.Attribute("Content"));
            var icon = Assert.Single(item.Descendants(presentation + "ImageIcon"));
            Assert.EndsWith(value.Icon, (string?)icon.Attribute("Source"));
            Assert.True(File.Exists(FindProjectFile(Path.Combine("Assets", "Navigation", value.Icon))));
            expected.Remove(name);
        }

        Assert.Empty(expected);
    }

    [Fact]
    public void MainWindow_PlacesGalExcleToolsAndZdToolBeforeFantasyProjectPc()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace views = "using:AxTools.Views";

        var navigationItems = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => new
            {
                Name = (string?)element.Attribute(xaml + "Name"),
                Content = (string?)element.Attribute("Content")
            })
            .ToList();
        var fantasyIndex = navigationItems.FindIndex(item => item.Name == "FantasyToolsNavItem");
        var galExcleIndex = navigationItems.FindIndex(item => item.Name == "GalExcleToolsNavItem");
        var zdIndex = navigationItems.FindIndex(item => item.Name == "CrossingVoidZDToolNavItem");
        var fantasyProjectIndex = navigationItems.FindIndex(item => item.Name == "FantasyProjectPcNavItem");

        Assert.Equal(fantasyIndex + 1, galExcleIndex);
        Assert.Equal(galExcleIndex + 1, zdIndex);
        Assert.Equal(zdIndex + 1, fantasyProjectIndex);
        Assert.Equal("剧情工具箱", navigationItems[galExcleIndex].Content);
        Assert.Contains(
            document.Descendants(views + "ToolPage"),
            element => (string?)element.Attribute(xaml + "Name") == "GalExcleToolsPage");
    }

    [Fact]
    public void MainWindow_ExposesCrossingVoidZdToolNavigationPageAndRouting()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace views = "using:AxTools.Views";
        var items = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => new
            {
                Name = (string?)element.Attribute(xaml + "Name"),
                Content = (string?)element.Attribute("Content"),
                Tag = (string?)element.Attribute("Tag")
            })
            .ToList();
        var galIndex = items.FindIndex(item => item.Name == "GalExcleToolsNavItem");
        var zdIndex = items.FindIndex(item => item.Name == "CrossingVoidZDToolNavItem");
        var fantasyProjectIndex = items.FindIndex(item => item.Name == "FantasyProjectPcNavItem");

        Assert.Equal(galIndex + 1, zdIndex);
        Assert.Equal(zdIndex + 1, fantasyProjectIndex);
        Assert.Equal("ZD空界幻境", items[zdIndex].Content);
        Assert.Equal("CrossingVoidZDTool", items[zdIndex].Tag);
        Assert.Contains(
            document.Descendants(views + "ToolPage"),
            element => (string?)element.Attribute(xaml + "Name") == "CrossingVoidZDToolPage");

        var routing = File.ReadAllText(FindProjectFile("MainWindow.Navigation.cs"));
        Assert.Contains("\"CrossingVoidZDTool\" => CrossingVoidZDToolPage", routing);
        Assert.Contains("CrossingVoidZDToolPage.Visibility", routing);
        Assert.Contains("\"CrossingVoidZDTool\" => CrossingVoidZDToolNavItem", routing);

        var windowCode = File.ReadAllText(FindProjectFile("MainWindow.xaml.cs"));
        Assert.Contains("CrossingVoidZDToolPage.DataContext = viewModel.Tools[3]", windowCode);
        Assert.Contains("CrossingVoidZDToolPage.ToolActionRequested += ToolPage_ToolActionRequested", windowCode);
        Assert.Contains("CrossingVoidZDToolPage.ToolActionRequested -= ToolPage_ToolActionRequested", windowCode);
    }

    [Fact]
    public void MainWindow_PlacesAndroidLauncherAfterPcLauncherAndRemovesOldGamePage()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .ToList();

        var pc = names.IndexOf("CrossingVoidPcNavItem");
        var android = names.IndexOf("CrossingVoidAndroidNavItem");
        Assert.Equal(pc + 1, android);
        Assert.DoesNotContain("CrossingVoidGameNavItem", names);
        Assert.DoesNotContain("CrossingVoidPackagePage", names);
    }

    [Fact]
    public void ToolActionButtonTemplate_UsesStableHeightForWrappedActions()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var template = document
            .Descendants(presentation + "DataTemplate")
            .Single(element =>
                (string?)element.Attribute(xaml + "Key") == "ToolActionButtonTemplate");
        var button = template.Elements(presentation + "Button").Single();
        Assert.Null(button.Attribute("MinWidth"));
        Assert.Equal("Stretch", (string?)button.Attribute("HorizontalAlignment"));
        Assert.Equal("40", (string?)button.Attribute("Height"));
    }

    [Fact]
    public void ToolPage_UsesAvailableWidthForResponsiveActionsAndKeepsRightInset()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var root = document.Root!;
        var scrollViewer = root.Element(presentation + "ScrollViewer")!;
        var template = document.Descendants(presentation + "DataTemplate")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "ToolActionButtonTemplate");
        var actionButton = template.Element(presentation + "Button")!;
        var actionLists = document.Descendants(presentation + "ItemsControl")
            .Where(element => ((string?)element.Attribute("ItemsSource"))?.Contains("Actions}") == true)
            .ToArray();

        Assert.Equal("ToolPage_SizeChanged", (string?)root.Attribute("SizeChanged"));
        Assert.Equal("0,0,20,0", (string?)scrollViewer.Attribute("Padding"));
        Assert.Null(actionButton.Attribute("MinWidth"));
        Assert.Equal("Stretch", (string?)actionButton.Attribute("HorizontalAlignment"));
        Assert.NotEmpty(actionLists);
        Assert.All(actionLists, list =>
        {
            Assert.Equal("ActionItemsControl_SizeChanged", (string?)list.Attribute("SizeChanged"));
            Assert.Equal("ActionItemsControl_Loaded", (string?)list.Attribute("Loaded"));
        });
    }

    [Fact]
    public void ToolPage_ProvidesActionHelpButton()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var helpButton = document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "ActionHelpButton");

        Assert.Equal("ShowActionHelp_Click", (string?)helpButton.Attribute("Click"));
        Assert.Contains(
            helpButton.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "功能说明");
    }

    [Fact]
    public void PublishPanel_SeparatesLauncherAndGamePublishingWorkflows()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var launcherSection = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "LauncherPublishingSection");
        var gameSection = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GamePublishingSection");

        Assert.Contains(
            launcherSection.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding LauncherPublishActions}");
        Assert.Contains(
            launcherSection.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains("PublishVersion") == true);
        var launcherVersion = launcherSection.Descendants(presentation + "TextBox")
            .Single(element => ((string?)element.Attribute("Text"))?.Contains("PublishVersion") == true);
        Assert.Equal("例如 1.1.1", (string?)launcherVersion.Attribute("PlaceholderText"));
        Assert.Contains(
            launcherSection.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("LauncherVersionStateText") == true);
        Assert.Contains(
            launcherSection.Descendants(presentation + "ComboBox"),
            element => ((string?)element.Attribute("SelectedValue"))?.Contains("PublishChannel") == true);

        Assert.Equal(
            "{Binding HasGamePackagePublishing, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)gameSection.Attribute("Visibility"));
        Assert.Contains(
            gameSection.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding GamePublishActions}");
        Assert.Contains(
            gameSection.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains("GamePublishVersion") == true);
        Assert.Contains(
            gameSection.Descendants(presentation + "ComboBox"),
            element => ((string?)element.Attribute("SelectedValue"))?.Contains("GamePublishChannel") == true);
    }

    [Fact]
    public void PublishPanel_ShowsRemoteCurrentVersionsAndRefreshButtons()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var launcher = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "LauncherPublishingSection");
        var game = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GamePublishingSection");

        Assert.Contains(
            launcher.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("LauncherPublishedVersionText") == true);
        Assert.Contains(
            launcher.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "RefreshLauncherVersion_Click");
        Assert.Contains(
            game.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("GamePublishedVersionText") == true);
        Assert.Contains(
            game.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "RefreshGameVersion_Click");
    }

    [Fact]
    public void DeveloperReleaseCenter_ShowsRemoteCurrentVersion()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "SettingsPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("CurrentPublishedVersion") == true);
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "RefreshAxToolsPublishedVersion_Click");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Header") == "Release 介绍" &&
                (string?)element.Attribute("IsReadOnly") != "True" &&
                ((string?)element.Attribute("Text"))?.Contains("ReleaseDescription") == true);
    }

    [Fact]
    public void GamePublishing_ShowsInstantReleaseNotesField()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Header") == "Release 介绍" &&
                (string?)element.Attribute("IsReadOnly") != "True" &&
                ((string?)element.Attribute("Text"))?.Contains("GameReleaseDescription") == true);
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Header") is "游戏更新说明" or "默认 Release 介绍");
    }

    [Fact]
    public void LauncherPublishing_ShowsEditableReleaseDescription()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Header") == "Release 介绍" &&
                (string?)element.Attribute("IsReadOnly") != "True" &&
                ((string?)element.Attribute("Text"))?.Contains("LauncherReleaseDescription") == true);
    }

    [Fact]
    public void DevelopmentPanel_ExposesGitHubProjectDownloadCommand()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "DownloadProject_Click");
        Assert.Contains(
            document.Descendants(presentation + "ItemsControl"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains("DevelopmentPanelActions") == true);
        Assert.Contains(
            document.Descendants(presentation + "ItemsControl"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains("ReleasePanelActions") == true);
    }

    [Fact]
    public void SettingsPage_ShowsWorkspaceOutputRootAsReadOnlyWithoutBrowseButton()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "SettingsPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var output = document.Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "WorkspaceOutputRootTextBox");

        Assert.Equal("True", (string?)output.Attribute("IsReadOnly"));
        Assert.Contains("Mode=OneWay", (string?)output.Attribute("Text"));
        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Tag") == "OutputRoot");
    }

    [Fact]
    public void GamePackagePathRow_ProtectsFolderButtonFromScrollbarClipping()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var pathRow = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GamePackagePathRow");
        var browseButton = pathRow.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GamePackageRootBrowseButton");
        var columns = pathRow
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();

        Assert.Equal("10", (string?)pathRow.Attribute("ColumnSpacing"));
        Assert.Equal("0", (string?)pathRow.Attribute("Margin"));
        Assert.Equal(new[] { "*", "40" }, columns);
        Assert.Equal("40", (string?)browseButton.Attribute("Width"));
        Assert.Equal("40", (string?)browseButton.Attribute("Height"));
        Assert.Equal("Stretch", (string?)browseButton.Attribute("HorizontalAlignment"));
        Assert.Equal("GamePackageRootBrowse_Click", (string?)browseButton.Attribute("Click"));
    }

    [Fact]
    public void PublishConfirmation_DistinguishesLauncherAndGameDetails()
    {
        var source = File.ReadAllText(FindProjectFile("MainWindow.ToolActions.cs"));

        Assert.Contains("isGamePublishingAction", source);
        Assert.Contains("\"游戏版本\"", source);
        Assert.Contains("\"启动器版本\"", source);
        Assert.Contains("CreateDialogLine(\"游戏目录\", tool.GamePackageRoot)", source);
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
