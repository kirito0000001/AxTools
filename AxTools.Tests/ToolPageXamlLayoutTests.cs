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
            ["AxToolsNavItem"] = ("Ax管理器", "AxTools.png"),
            ["CrossingVoidZDToolNavItem"] = ("工具-零境素材", "CrossingVoidZDTool.png"),
            ["GalExcleToolsNavItem"] = ("工具-剧情填表", "GalExcleTools.png"),
            ["FantasyToolsNavItem"] = ("工具-幻杀卡牌", "FantasyTools.png"),
            ["CrossingVoidPcNavItem"] = ("TFAC启动平台", "CrossingVoidPc.png"),
            ["CrossingVoidAndroidNavItem"] = ("平台-零境-安卓", "CrossingVoidAndroid.png"),
            ["FantasyAndroidNavItem"] = ("平台-幻杀-安卓", "FantasyProjectPc.png")
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

        // 2026-09-20：三个游戏包发布页并进「服务器」页的游戏页签，左侧不再有这三个导航项。
        var names = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .ToArray();
        Assert.DoesNotContain("CrossingVoidGameNavItem", names);
        Assert.DoesNotContain("FantasyGameNavItem", names);
        Assert.DoesNotContain("NarutoGameNavItem", names);
    }

    [Fact]
    public void MainWindow_PlacesZdToolBeforeGalExcleToolsBeforeFantasyTools()
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
        var zdIndex = navigationItems.FindIndex(item => item.Name == "CrossingVoidZDToolNavItem");
        var galExcleIndex = navigationItems.FindIndex(item => item.Name == "GalExcleToolsNavItem");
        var fantasyToolsIndex = navigationItems.FindIndex(item => item.Name == "FantasyToolsNavItem");

        Assert.Equal(zdIndex + 1, galExcleIndex);
        Assert.Equal(galExcleIndex + 1, fantasyToolsIndex);
        Assert.Equal("工具-剧情填表", navigationItems[galExcleIndex].Content);
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
        var fantasyToolsIndex = items.FindIndex(item => item.Name == "FantasyToolsNavItem");

        Assert.Equal(zdIndex + 1, galIndex);
        Assert.Equal(galIndex + 1, fantasyToolsIndex);
        Assert.Equal("工具-零境素材", items[zdIndex].Content);
        Assert.Equal("CrossingVoidZDTool", items[zdIndex].Tag);
        Assert.Contains(
            document.Descendants(views + "ToolPage"),
            element => (string?)element.Attribute(xaml + "Name") == "CrossingVoidZDToolPage");

        var routing = File.ReadAllText(FindProjectFile("MainWindow.Navigation.cs"));
        Assert.Contains("\"CrossingVoidZDTool\" => CrossingVoidZDToolPage", routing);
        Assert.Contains("CrossingVoidZDToolPage.Visibility", routing);
        Assert.Contains("\"CrossingVoidZDTool\" => CrossingVoidZDToolNavItem", routing);

        var windowCode = File.ReadAllText(FindProjectFile("MainWindow.xaml.cs"));
        Assert.Contains("CrossingVoidZDToolPage.DataContext = viewModel.Tools[4]", windowCode);
        Assert.Contains("CrossingVoidZDToolPage.ToolActionRequested += ToolPage_ToolActionRequested", windowCode);
        Assert.Contains("CrossingVoidZDToolPage.ToolActionRequested -= ToolPage_ToolActionRequested", windowCode);
    }

    [Fact]
    public void GamePublishing_LivesInServerPageTabsInsteadOfLeftNavigation()
    {
        var windowCode = File.ReadAllText(FindProjectFile("MainWindow.xaml.cs"));
        Assert.Contains(
            "ServerAliyunPage.AttachGamePublishPanel(\"crossingvoid\", CrossingVoidGamePage)",
            windowCode);
        Assert.Contains(
            "ServerAliyunPage.AttachGamePublishPanel(\"narutobp\", NarutoGamePage)",
            windowCode);
        Assert.Contains(
            "ServerAliyunPage.AttachGamePublishPanel(\"fantasyproject\", FantasyGamePage)",
            windowCode);
        // 幻杀的游戏包目录选择按钮原本没接线，跟着这次搬家一起补上。
        Assert.Contains(
            "FantasyGamePage.GamePackageRootSelectionRequested += ToolPage_GamePackageRootSelectionRequested",
            windowCode);

        // 旧页签统一回到服务器分区，避免设置里存着旧值、重开时落不到页面上。
        var routing = File.ReadAllText(FindProjectFile("MainWindow.Navigation.cs"));
        Assert.Contains(
            "\"CrossingVoidGame\" or \"FantasyGame\" or \"NarutoGame\" => \"ServerAliyun\"",
            routing);
        Assert.DoesNotContain("CrossingVoidGamePage.Visibility", routing);
        Assert.DoesNotContain("NarutoGamePage.Visibility", routing);

        // 服务器页按产品键把发布面板挂进对应游戏的页签卡片。
        var serverPage = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ServerPage.xaml.cs")));
        Assert.Contains("_gamePublishPanels.TryGetValue(programKey, out var publishPage)", serverPage);
        Assert.Contains("客户端发布（下载站）", serverPage);
        // 发布卡片挂在固定宿主上，不跟着 15 秒轮询重建（否则输入焦点每 15 秒被踢一次）。
        Assert.Contains("UpdateGamePublishPanel();", serverPage);
        Assert.Contains(
            "GamePublishPanelHost",
            File.ReadAllText(FindProjectFile(Path.Combine("Views", "ServerPage.xaml"))));

        // ToolPage 的嵌入形态：藏掉自己的页头、模式选择器和内层边框。
        var toolPage = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs")));
        Assert.Contains("internal void HostAsGamePublishPanel()", toolPage);
        Assert.Contains("PageTitleText.Visibility = Visibility.Collapsed", toolPage);
        Assert.Contains("ModeSelector.Visibility = Visibility.Collapsed", toolPage);

        // 必须按内容网格自己的 Children 摘除：三个页面和别的页共用一个格子，
        // 用 FrameworkElement.Parent 判断时摘不掉，三张发布页会叠在当前选中页上。
        Assert.Contains("PageHost.Children.Remove(page)", windowCode);
        Assert.Contains("x:Name=\"PageHost\"", File.ReadAllText(FindProjectFile("MainWindow.xaml")));
        Assert.DoesNotContain("page.Parent is Panel", File.ReadAllText(FindProjectFile(Path.Combine("Views", "ServerPage.xaml.cs"))));
    }

    [Fact]
    public void MainWindow_PlacesAndroidLauncherAfterPcLauncher()
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
        Assert.DoesNotContain("FantasyProjectPcNavItem", names);
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
        // 启动器版本号也是三段式 NumberBox（主版本 / 功能更新 / BUG修复）。
        var launcherVersion = launcherSection.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "LauncherVersionField");
        Assert.Equal(
            ["PublishVersionMajor", "PublishVersionFeature", "PublishVersionBugFix"],
            launcherVersion.Descendants(presentation + "NumberBox")
                .Select(element => (string?)element.Attribute("Value"))
                .Select(value => value?.Split(',')[0].Replace("{Binding ", string.Empty)));
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
        // 版本号是三段式 NumberBox（主版本 / 功能更新 / BUG修复），不再是一个手填 TextBox。
        var versionField = gameSection.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GameVersionField");
        Assert.Equal(
            ["GameVersionMajor", "GameVersionFeature", "GameVersionBugFix"],
            versionField.Descendants(presentation + "NumberBox")
                .Select(element => (string?)element.Attribute("Value"))
                .Select(value => value?.Split(',')[0].Replace("{Binding ", string.Empty)));
        // 渠道暂时取消（还没余力开测试服）：界面上没有「游戏通道」，只留平台。
        Assert.DoesNotContain(
            gameSection.Descendants(presentation + "ComboBox"),
            element => ((string?)element.Attribute("SelectedValue"))?.Contains("GamePublishChannel") == true);
        Assert.DoesNotContain(
            gameSection.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "GameChannelField");
        Assert.Contains(
            gameSection.Descendants(presentation + "ComboBox"),
            element => ((string?)element.Attribute("SelectedValue"))?.Contains("GamePublishPlatform") == true);
        // 维护动作收在「维护（额外）」折叠区里。
        var maintenanceArea = gameSection.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GameMaintenanceArea");
        Assert.Contains(
            maintenanceArea.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding GameMaintenanceActions}");
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
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "EditAxToolsReleaseDescription_Click" &&
                ((string?)element.Attribute("Content"))?.Contains("Release 介绍") == true);
    }

    [Fact]
    public void ReleaseDescription_HasExactlyOneEditableEntryInDeveloperReleaseCenter()
    {
        var settings = XDocument.Load(FindProjectFile(Path.Combine("Views", "SettingsPage.xaml")));
        var toolPage = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            settings.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "EditAxToolsReleaseDescription_Click" &&
                ((string?)element.Attribute("Content"))?.Contains("Release 介绍") == true);
        Assert.DoesNotContain(
            toolPage.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Header") is "Release 介绍" or "游戏更新说明" or "默认 Release 介绍");
        Assert.DoesNotContain(
            toolPage.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains("ReleaseDescription") == true);
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
