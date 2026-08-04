using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class ResponsiveLayoutContractTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    [Theory]
    [InlineData(1400, ResponsiveLayoutKind.Wide, false, 340, 250, 400)]
    [InlineData(960, ResponsiveLayoutKind.Compact, true, 340, 250, 400)]
    [InlineData(683, ResponsiveLayoutKind.Narrow, true, 320, 250, 400)]
    public void PolicyKeepsWideLayoutAndUsesReachableOverlayPanesWhenSnapped(
        double viewportWidth,
        ResponsiveLayoutKind expectedKind,
        bool expectedOverlay,
        double expectedAssetWidth,
        double expectedFilterWidth,
        double expectedDetailsWidth)
    {
        var layout = ResponsiveLayoutPolicy.Resolve(viewportWidth);

        Assert.Equal(expectedKind, layout.Kind);
        Assert.Equal(expectedOverlay, layout.UseOverlayPanes);
        Assert.Equal(expectedAssetWidth, layout.AssetPaneWidth);
        Assert.Equal(expectedFilterWidth, layout.FilterPaneWidth);
        Assert.Equal(expectedDetailsWidth, layout.DetailsPaneWidth);
    }

    [Fact]
    public void MainWindowSupportsSnapDimensionsAndKeepsAssetDrawerReachable()
    {
        var document = LoadXaml("Views", "MainWindow.axaml");
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "MainWindow.axaml.cs");
        var window = document.Root!;
        var assetSplitView = FindNamedElement(document, "SplitView", "AssetSplitView");
        var assetPaneContent =
            FindNamedElement(document, "AssetListView", "AssetPaneContent");
        var assetToggle = FindNamedElement(document, "Button", "AssetPaneToggleButton");
        var search = FindNamedElement(document, "TextBox", "SearchTextBox");

        Assert.Equal("640", (string?)window.Attribute("MinWidth"));
        Assert.Equal("480", (string?)window.Attribute("MinHeight"));
        Assert.Equal("Left", (string?)assetSplitView.Attribute("PanePlacement"));
        Assert.Equal("Inline", (string?)assetSplitView.Attribute("DisplayMode"));
        Assert.Equal(
            "OnAssetPaneOpening",
            (string?)assetSplitView.Attribute("PaneOpening"));
        Assert.Equal(
            "OnAssetPaneClosed",
            (string?)assetSplitView.Attribute("PaneClosed"));
        Assert.NotNull(assetPaneContent);
        Assert.Equal("OnAssetPaneToggleClick", (string?)assetToggle.Attribute("Click"));
        Assert.Equal("False", (string?)assetToggle.Attribute("IsVisible"));
        Assert.Null(search.Attribute("Width"));
        Assert.Equal("120", (string?)search.Attribute("MinWidth"));
        Assert.Equal("300", (string?)search.Attribute("MaxWidth"));

        Assert.Contains(
            "AssetPaneToggleButton.IsVisible = layout.UseOverlayPanes;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssetSplitView.IsPaneOpen = !AssetSplitView.IsPaneOpen;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssetPaneContent.IsVisible = false;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyboardNavigationMode.None",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddonGridControl.ApplyResponsiveLayout(layout);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddonDetailsPanel.ApplyResponsiveLayout(layout);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyResponsiveLayout(Bounds.Width);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddonGridKeepsSortAndFiltersInsideAToggleableRightDrawer()
    {
        var document = LoadXaml("Views", "AddonGridView.axaml");
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AddonGridView.axaml.cs");
        var filterSplitView = FindNamedElement(document, "SplitView", "FilterSplitView");
        var filterPaneContent = FindNamedElement(document, "Border", "FilterPaneContent");
        var filterToggle = FindNamedElement(document, "Button", "FilterPaneToggleButton");
        var filterClose = FindNamedElement(document, "Button", "FilterPaneCloseButton");

        Assert.Equal("Right", (string?)filterSplitView.Attribute("PanePlacement"));
        Assert.Equal("Inline", (string?)filterSplitView.Attribute("DisplayMode"));
        Assert.Equal(
            "OnFilterPaneOpening",
            (string?)filterSplitView.Attribute("PaneOpening"));
        Assert.Equal(
            "OnFilterPaneClosed",
            (string?)filterSplitView.Attribute("PaneClosed"));
        Assert.NotNull(filterPaneContent);
        Assert.Equal("OnFilterPaneToggleClick", (string?)filterToggle.Attribute("Click"));
        Assert.Equal("False", (string?)filterToggle.Attribute("IsVisible"));
        Assert.Equal("OnFilterPaneToggleClick", (string?)filterClose.Attribute("Click"));
        Assert.Equal("False", (string?)filterClose.Attribute("IsVisible"));
        Assert.Contains(
            filterSplitView.Descendants(AvaloniaNamespace + "ComboBox"),
            element =>
                (string?)element.Attribute("ItemsSource") == "{Binding SortModeOptions}");
        Assert.Contains(
            filterSplitView.Descendants(AvaloniaNamespace + "ItemsControl"),
            element =>
                (string?)element.Attribute("ItemsSource") == "{Binding AddonTypeFilters}");
        Assert.Contains(
            filterSplitView.Descendants(AvaloniaNamespace + "ItemsControl"),
            element =>
                (string?)element.Attribute("ItemsSource") == "{Binding AddonTagFilters}");

        Assert.Contains(
            "FilterPaneToggleButton.IsVisible = layout.UseOverlayPanes;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterPaneCloseButton.IsVisible = layout.UseOverlayPanes;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterSplitView.IsPaneOpen = !FilterSplitView.IsPaneOpen;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterPaneContent.IsVisible = false;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyboardNavigationMode.None",
            source,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ClosedAssetOverlayPreservesCloseAnimationThenLeavesAutomationTree()
    {
        using var window = new MainWindow
        {
            WindowState = WindowState.Normal,
            Width = 1400,
            Height = 640
        };

        window.Show();
        try
        {
            var splitView = Assert.IsType<SplitView>(
                window.FindControl<SplitView>("AssetSplitView"));
            var paneContent = Assert.IsType<AssetListView>(
                window.FindControl<AssetListView>("AssetPaneContent"));
            var toggle = Assert.IsType<Button>(
                window.FindControl<Button>("AssetPaneToggleButton"));

            window.Width = 643;
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            Assert.Equal(SplitViewDisplayMode.Overlay, splitView.DisplayMode);
            Assert.False(splitView.IsPaneOpen);
            Assert.True(paneContent.IsVisible);
            Assert.False(paneContent.IsEnabled);
            Assert.False(paneContent.IsHitTestVisible);
            Assert.Equal(
                KeyboardNavigationMode.None,
                KeyboardNavigation.GetTabNavigation(paneContent));

            await WaitForPaneVisibilityDelayAsync();

            Assert.False(paneContent.IsVisible);
            Assert.Null(splitView.Pane);
            Assert.False(AutomationTreeContains(window, "AssetPaneContent"));

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(splitView.IsPaneOpen);
            Assert.Same(paneContent, splitView.Pane);
            Assert.True(paneContent.IsVisible);
            Assert.True(paneContent.IsEnabled);
            Assert.True(paneContent.IsHitTestVisible);
            Assert.NotEqual(
                KeyboardNavigationMode.None,
                KeyboardNavigation.GetTabNavigation(paneContent));
            Assert.True(AutomationTreeContains(window, "AssetPaneContent"));

            splitView.IsPaneOpen = false;
            await WaitForPaneVisibilityDelayAsync();
            Assert.Null(splitView.Pane);

            var addonGrid = Assert.IsType<AddonGridView>(
                window.FindControl<AddonGridView>("AddonGridControl"));
            var filterToggle = Assert.IsType<Button>(
                addonGrid.FindControl<Button>("FilterPaneToggleButton"));
            filterToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPaneVisibilityDelayAsync();

            Assert.False(splitView.IsPaneOpen);
            Assert.Null(splitView.Pane);
            Assert.False(AutomationTreeContains(window, "AssetPaneContent"));
            Assert.True(AutomationTreeContains(window, "FilterPaneContent"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosedFilterOverlayPreservesCloseAnimationThenLeavesAutomationTree()
    {
        using var view = new AddonGridView();
        var window = new Window
        {
            Content = view,
            Width = 643,
            Height = 640
        };

        window.Show();
        try
        {
            view.ApplyResponsiveLayout(ResponsiveLayoutPolicy.Resolve(643));

            var splitView = Assert.IsType<SplitView>(
                view.FindControl<SplitView>("FilterSplitView"));
            var paneContent = Assert.IsType<Border>(
                view.FindControl<Border>("FilterPaneContent"));
            var toggle = Assert.IsType<Button>(
                view.FindControl<Button>("FilterPaneToggleButton"));

            Assert.Equal(SplitViewDisplayMode.Overlay, splitView.DisplayMode);
            Assert.False(splitView.IsPaneOpen);
            Assert.True(paneContent.IsVisible);
            Assert.False(paneContent.IsEnabled);
            Assert.False(paneContent.IsHitTestVisible);
            Assert.Equal(
                KeyboardNavigationMode.None,
                KeyboardNavigation.GetTabNavigation(paneContent));

            await WaitForPaneVisibilityDelayAsync();

            Assert.False(paneContent.IsVisible);
            Assert.Null(splitView.Pane);
            Assert.False(AutomationTreeContains(window, "FilterPaneContent"));

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(splitView.IsPaneOpen);
            Assert.Same(paneContent, splitView.Pane);
            Assert.True(paneContent.IsVisible);
            Assert.True(paneContent.IsEnabled);
            Assert.True(paneContent.IsHitTestVisible);
            Assert.NotEqual(
                KeyboardNavigationMode.None,
                KeyboardNavigation.GetTabNavigation(paneContent));
            Assert.True(AutomationTreeContains(window, "FilterPaneContent"));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void DetailsPanelUsesThePolicyWidthForLayoutAndItsHiddenTranslation()
    {
        var document = LoadXaml("Controls", "AddonDetailsFloatingPanel.axaml");
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Controls", "AddonDetailsFloatingPanel.axaml.cs");
        var panel = FindNamedElement(document, "Border", "FloatingPanel");

        Assert.Equal("400", (string?)panel.Attribute("MaxWidth"));
        Assert.Contains("_panelWidth = layout.DetailsPaneWidth;", source, StringComparison.Ordinal);
        Assert.Contains(
            "CreateHorizontalTranslation(_panelWidth)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("translateX(400px)", source, StringComparison.Ordinal);
    }

    private static XElement FindNamedElement(
        XDocument document,
        string elementName,
        string name) =>
        document
            .Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .Single(element => (string?)element.Attribute("Name") == name);

    private static async Task WaitForPaneVisibilityDelayAsync()
    {
        await Task.Delay(250);
        await Dispatcher.UIThread.InvokeAsync(static () => { });
    }

    private static bool AutomationTreeContains(Control root, string automationId)
    {
        var pending = new Stack<AutomationPeer>();
        pending.Push(ControlAutomationPeer.CreatePeerForElement(root));

        while (pending.TryPop(out var peer))
        {
            if (string.Equals(
                    peer.GetAutomationId(),
                    automationId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var child in peer.GetChildren())
            {
                pending.Push(child);
            }
        }

        return false;
    }

    private static XDocument LoadXaml(string directory, string fileName) =>
        XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", directory, fileName));

    private static string ReadRepositoryFile(
        string segment1,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment1, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
