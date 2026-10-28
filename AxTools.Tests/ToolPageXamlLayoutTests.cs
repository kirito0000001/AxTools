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
            ["AxToolsNavItem"] = ("AX管理器", "AxTools.png"),
            ["CrossingVoidZDToolNavItem"] = ("工具-零境素材", "CrossingVoidZDTool.png"),
            ["GalExcleToolsNavItem"] = ("工具-剧情填表", "GalExcleTools.png"),
            ["FantasyToolsNavItem"] = ("工具-幻杀卡牌", "FantasyTools.png"),
            ["CrossingVoidPcNavItem"] = ("TFAC启动平台", "CrossingVoidPc.png")
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

        // 2026-10-27：左栏固定 8 项，三个游戏页回到左栏；服务器与两个安卓页不再有入口。
        var names = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .ToArray();
        Assert.Contains("CrossingVoidGameNavItem", names);
        Assert.Contains("FantasyGameNavItem", names);
        Assert.Contains("NarutoGameNavItem", names);
        Assert.DoesNotContain("ServerAliyunNavItem", names);
        Assert.DoesNotContain("CrossingVoidAndroidNavItem", names);
        Assert.DoesNotContain("FantasyAndroidNavItem", names);
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
    public void GamePublishing_IsBackInLeftNavigationAsStandalonePages()
    {
        // 2026-10-27：游戏发布页从「服务器」页签搬回左栏，重新作为独立页面。
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace views = "using:AxTools.Views";

        var gameItems = new Dictionary<string, (string Title, string Tag)>
        {
            ["CrossingVoidGameNavItem"] = ("游戏-零境交错", "CrossingVoidGame"),
            ["FantasyGameNavItem"] = ("游戏-幻杀", "FantasyGame"),
            ["NarutoGameNavItem"] = ("游戏-火影BP", "NarutoGame")
        };
        foreach (var item in document.Descendants(presentation + "NavigationViewItem"))
        {
            var name = (string?)item.Attribute(xaml + "Name");
            if (name is null || !gameItems.TryGetValue(name, out var value))
            {
                continue;
            }

            Assert.Equal(value.Title, (string?)item.Attribute("Content"));
            Assert.Equal(value.Tag, (string?)item.Attribute("Tag"));
            gameItems.Remove(name);
        }

        Assert.Empty(gameItems);

        // 三个游戏页仍是 PageHost 的常驻子页，不再被摘出去挂到服务器页。
        var pageNames = document
            .Descendants(views + "ToolPage")
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .ToArray();
        Assert.Contains("CrossingVoidGamePage", pageNames);
        Assert.Contains("FantasyGamePage", pageNames);
        Assert.Contains("NarutoGamePage", pageNames);

        var windowCode = File.ReadAllText(FindProjectFile("MainWindow.xaml.cs"));
        Assert.DoesNotContain("AttachGamePublishPanel", windowCode);
        Assert.DoesNotContain("PageHost.Children.Remove(page)", windowCode);

        // 路由要把三个标签指到各自页面，并在首次进入时切到发布页签。
        var routing = File.ReadAllText(FindProjectFile("MainWindow.Navigation.cs"));
        Assert.Contains("\"CrossingVoidGame\" => CrossingVoidGamePage", routing);
        Assert.Contains("\"FantasyGame\" => FantasyGamePage", routing);
        Assert.Contains("\"NarutoGame\" => NarutoGamePage", routing);
        Assert.Contains("gamePage.EnsurePublishMode();", routing);

        var toolPage = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs")));
        Assert.Contains("internal void EnsurePublishMode()", toolPage);
    }

    [Fact]
    public void MainWindow_LeftNavigationHasExactlyEightItemsInOrder()
    {
        var document = XDocument.Load(FindProjectFile("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document
            .Descendants(presentation + "NavigationViewItem")
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .ToArray();

        // 顺序即需求：管理器 → 三个工具 → TFAC → 三个游戏；设置固定在页脚。
        Assert.Equal(
            [
                "AxToolsNavItem",
                "CrossingVoidZDToolNavItem",
                "GalExcleToolsNavItem",
                "FantasyToolsNavItem",
                "CrossingVoidPcNavItem",
                "CrossingVoidGameNavItem",
                "FantasyGameNavItem",
                "NarutoGameNavItem",
                "SettingsNavItem"
            ],
            names);
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
        // 只有工具动作列表需要按可用宽度算列宽；游戏卡片用的是固定列宽的动作网格。
        var actionLists = document.Descendants(presentation + "ItemsControl")
            .Where(element =>
            {
                var source = (string?)element.Attribute("ItemsSource");
                return source?.Contains("Actions}") == true &&
                    source?.Contains("GameProjectActions") != true &&
                    source != "{Binding Actions}";
            })
            .ToArray();

        Assert.Equal("ToolPage_SizeChanged", (string?)root.Attribute("SizeChanged"));
        // 右侧内边距改挂在内容 Grid 的 Margin 上：ScrollViewer.Padding 不参与内容测量，
        // 右对齐的浏览按钮会顶到滚动条下面被挡住（2026-10-27 实测）。
        Assert.Null(scrollViewer.Attribute("Padding"));
        var contentGrid = scrollViewer.Element(presentation + "Grid")!;
        Assert.Equal("{StaticResource AxPagePadding}", (string?)contentGrid.Attribute("Margin"));
        Assert.Equal("0,0,20,0", ReadTokenValue("AxPagePadding"));
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
    public void ToolPage_DropsTheActionHelpButton()
    {
        // 2026-10-27：「功能说明」属于 AX 自己的元功能，直接删掉，不再占页面位置。
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(xaml + "Name") == "ActionHelpButton");
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "功能说明");
        Assert.DoesNotContain(
            "ShowActionHelp_Click",
            File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs"))));
    }

    [Fact]
    public void GameTools_HideTheToolDescriptionAndReportMissingItemsAtTop()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 游戏页不再显示工具描述行。
        var description = document.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PageDescriptionText");
        Assert.Equal(
            "{Binding IsGameTool, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
            (string?)description.Attribute("Visibility"));

        // 配置状态通告栏按校验结果变色，不再固定绿色。
        var infoBar = document.Descendants(presentation + "InfoBar").Single();
        Assert.Equal(
            "{Binding ConfigurationSeverity, Converter={StaticResource OperationNoticeSeverityConverter}}",
            (string?)infoBar.Attribute("Severity"));

        // 缺什么写在顶部通知栏里，开发版面板这一轮不动；
        // 通知栏内容跟着页签走：开发版报“开发环境已就绪”，打包页查游戏包目录。
        var viewModel = File.ReadAllText(FindProjectFile(Path.Combine("AxTools.Core", "ViewModels", "ToolPageViewModel.cs")));
        Assert.Contains("EvaluateGameConfiguration", viewModel);
        Assert.Contains("开发环境已就绪", viewModel);
        Assert.Contains("发布环境已就绪", viewModel);
        Assert.Contains("SelectSection", viewModel);

        var codeBehind = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs")));
        Assert.Contains("sectionViewModel.SelectSection(section)", codeBehind);
        Assert.Contains("ToolPageSection.Publish", codeBehind);
        Assert.DoesNotContain("GameWorkbenchPanel", File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml"))));
    }

    [Fact]
    public void GameTools_HaveSettingsPageWithScratchSection()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 页签叫「设置」；「功能散页」是它内部的临时分区，只在游戏页出现。
        var tab = document.Descendants(presentation + "SelectorBarItem")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SettingsSelectorItem");
        Assert.Equal("设置", (string?)tab.Attribute("Text"));
        Assert.Equal(
            "{Binding IsGameTool, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)tab.Attribute("Visibility"));

        var panel = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SettingsPanel");

        Assert.Contains(
            panel.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "功能散页");

        // 设置项归到「环境配置」一组下面，且用比小节更大的分组标题样式。
        var groupTitle = panel.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "环境配置");
        Assert.Equal("{StaticResource GroupTitleStyle}", (string?)groupTitle.Attribute("Style"));

        // 正规设置第一项：是否拥有安卓启动器。
        var toggle = panel.Descendants(presentation + "ToggleSwitch").Single();
        Assert.Equal("{Binding HasAndroidLauncher, Mode=TwoWay}", (string?)toggle.Attribute("IsOn"));

        // 两个项目路径：可手填，也可点右侧按钮选目录（Tag 供文件夹选择器识别）。
        var textBindings = panel.Descendants(presentation + "TextBox")
            .Select(element => (string?)element.Attribute("Text"))
            .ToArray();
        Assert.Contains("{Binding ClientProjectPath, Mode=TwoWay}", textBindings);
        Assert.Contains("{Binding AndroidLauncherPath, Mode=TwoWay}", textBindings);

        var browseTags = panel.Descendants(presentation + "Button")
            .Where(element => (string?)element.Attribute("Click") == "ProjectPathBrowse_Click")
            .Select(element => (string?)element.Attribute("Tag"))
            .ToArray();
        Assert.Equal(new string?[] { "client", "android", "server-root" }, browseTags);

        // 暂存清单绑定到 GameScratchItems。
        Assert.Contains(
            panel.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding GameScratchItems}");

        // 页签切换要同步通知栏并显示对应面板。
        var codeBehind = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs")));
        Assert.Contains("ToolPageSection.Settings", codeBehind);
        Assert.Contains("SettingsPanel.Visibility", codeBehind);
    }

    [Fact]
    public void GameTools_DevelopmentPageUsesProjectCardWithTallerButtons()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // 卡片列表由 GameProjectCards 驱动：本体一张，开了安卓启动器再加一张。
        var cardsList = document.Descendants(presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding GameProjectCards}");
        Assert.Equal(
            "{Binding IsGameTool, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)cardsList.Attribute("Visibility"));

        // 卡片内标题绑定 Title，动作列表绑定 Actions。
        Assert.Contains(
            cardsList.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Title}");
        var actionList = cardsList.Descendants(presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Actions}");
        var actionButton = actionList.Descendants(presentation + "Button").Single();
        Assert.Equal("{StaticResource CardActionButtonStyle}", (string?)actionButton.Attribute("Style"));

        // 按钮高度由样式统一给 48（1.2 倍），页面里不写乘算、不写高度。
        var styles = XDocument.Load(FindProjectFile(Path.Combine("Styles", "ToolboxStyles.xaml")));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = styles.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "CardActionButtonStyle");
        var heightSetter = style.Descendants(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Height");
        Assert.Equal("48", (string?)heightSetter.Attribute("Value"));
        Assert.Null(actionButton.Attribute("Height"));

        // 图标+文字整体居中，不做左对齐。
        var alignSetter = style.Descendants(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "HorizontalContentAlignment");
        Assert.Equal("Center", (string?)alignSetter.Attribute("Value"));
    }

    [Fact]
    public void ServerPanel_HoldsServerStateAndActions()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var panel = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "ServerPanel");

        // 服务器单独一页：实例端口 + 仓库状态 + 功能按钮。
        Assert.Contains(
            panel.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "服务端状态");
        Assert.Contains(
            panel.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding ServerInstances}");
        Assert.Contains(
            panel.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding ServerUpdateLines}");
        // 刷新时排版不变：实例为空给占位提示，不隐藏整块。
        Assert.Contains(
            panel.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "尚未读取到实例信息。");
        // 功能按钮：七个服务端操作，点击交给服务器页那套实现执行。
        Assert.Contains(
            panel.Descendants(presentation + "ItemsControl"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding ServerActions}");
        Assert.Contains(
            panel.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "ServerAction_Click");

        // 手动刷新按钮已删：进入分区自动读，之后 15 秒一次。
        Assert.DoesNotContain(
            panel.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "RefreshServerState_Click");

        // 发布页仍保留旧内容，但整块是收起的，等重新设计。
        var publishPanel = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PublishPanel");
        var legacy = publishPanel.Descendants(presentation + "StackPanel")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "LegacyPublishSection");
        Assert.Equal("Collapsed", (string?)legacy.Attribute("Visibility"));
        Assert.Contains(
            legacy.Descendants(presentation + "StackPanel"),
            element => (string?)element.Attribute(xaml + "Name") == "LauncherPublishingSection");
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
        // 游戏版本由打包产物的 Version.json 决定，界面只显示只读结果，不再允许手填。
        var versionField = gameSection.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GameVersionField");
        Assert.DoesNotContain(versionField.Descendants(presentation + "NumberBox"), _ => true);
        Assert.Contains(
            versionField.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("GamePackageVersionText") == true);
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
    public void FragmentCreation_IsItsOwnTabAndNotInsidePublishPanel()
    {
        var document = XDocument.Load(FindProjectFile(Path.Combine("Views", "ToolPage.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var tabs = document.Descendants(presentation + "SelectorBarItem").ToList();
        var publishIndex = tabs.FindIndex(item => (string?)item.Attribute(xaml + "Name") == "PublishSelectorItem");
        var serverIndex = tabs.FindIndex(item => (string?)item.Attribute(xaml + "Name") == "ServerSelectorItem");
        var fragmentIndex = tabs.FindIndex(item => (string?)item.Attribute(xaml + "Name") == "FragmentSelectorItem");
        var storageIndex = tabs.FindIndex(item => (string?)item.Attribute(xaml + "Name") == "StorageSelectorItem");

        Assert.True(publishIndex >= 0);
        Assert.Equal(publishIndex + 1, serverIndex);
        Assert.Equal(serverIndex + 1, fragmentIndex);
        Assert.Equal(fragmentIndex + 1, storageIndex);

        var fragmentTab = tabs[fragmentIndex];
        Assert.Equal("碎片制作", (string?)fragmentTab.Attribute("Text"));
        Assert.Equal(
            "{Binding HasFragmentGeneration, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)fragmentTab.Attribute("Visibility"));

        var publishPanel = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PublishPanel");
        var fragmentPanel = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "FragmentPanel");
        var fragmentSection = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "FragmentGenerationSection");

        Assert.Contains(fragmentSection, fragmentPanel.Descendants());
        Assert.DoesNotContain(fragmentSection, publishPanel.Descendants());
        // 页签本身已经叫「碎片制作」，内层不再重复一个同名标题；只保留说明。
        Assert.Null((string?)fragmentSection.Attribute("Visibility"));
        Assert.Contains(
            fragmentPanel.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("FragmentGenerationDescription") == true);

        var gameSelector = fragmentPanel.Descendants(presentation + "ComboBox")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "FragmentGameSelector");
        Assert.Equal("{Binding FragmentGameOptions}", (string?)gameSelector.Attribute("ItemsSource"));
        Assert.Equal(
            "{Binding SelectedFragmentGame, Mode=TwoWay}",
            (string?)gameSelector.Attribute("SelectedItem"));
        Assert.Equal(
            "{Binding HasFragmentGameSelection, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)gameSelector.Attribute("Visibility"));

        var codeBehind = File.ReadAllText(FindProjectFile(Path.Combine("Views", "ToolPage.xaml.cs")));
        Assert.Contains("FragmentPanel.Visibility = selectedItem == FragmentSelectorItem", codeBehind);
        Assert.Contains("fragmentViewModel.RefreshFragmentBindings();", codeBehind);
        Assert.Contains("FragmentSelectorItem.Visibility = Visibility.Collapsed", codeBehind);
        Assert.Contains("FragmentPanel.Visibility = Visibility.Collapsed", codeBehind);
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

        // 2026-10-27：正式版分区整个移除，不再有对应的页签与动作列表。
        Assert.DoesNotContain(
            document.Descendants(presentation + "SelectorBarItem"),
            element => (string?)element.Attribute("Text") == "正式版");
        Assert.DoesNotContain(
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

        Assert.Equal("{StaticResource AxSpace10}", (string?)pathRow.Attribute("ColumnSpacing"));
        Assert.Equal("10", ReadTokenValue("AxSpace10"));
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

    /// <summary>
    /// 读取 DesignTokens.xaml 里的令牌数值。布局测试因此不再把间距/内边距写死在断言里，
    /// 而是把"引用令牌"和"令牌取值"分开锁：换皮时只动令牌，结构契约仍然生效。
    /// </summary>
    private static string ReadTokenValue(string key)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(FindProjectFile(Path.Combine("Styles", "DesignTokens.xaml")));
        var token = document.Root!
            .Elements()
            .SingleOrDefault(element => (string?)element.Attribute(xaml + "Key") == key);
        Assert.True(token is not null, $"DesignTokens.xaml 里缺少令牌 {key}。");
        return token!.Value.Trim();
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
