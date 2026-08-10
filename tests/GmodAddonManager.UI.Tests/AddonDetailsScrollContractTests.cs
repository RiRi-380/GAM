using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Controls;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonDetailsScrollContractTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-details-scroll-contract",
        Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task FloatingPanelRoutesOverlayWheelToScrollableAddonDetails()
    {
        var workshop = Path.Combine(rootPath, "workshop", "content", "4000");
        var appData = Path.Combine(rootPath, "appdata");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(appData);

        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshop,
            CustomAppDataPath = appData,
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });
        await manager.InitializeAsync();

        using var watcher = new GmodProcessWatcher();
        var pending = new PendingChangeManager(manager, appData);
        var previousAssetList = ViewModelLocator.AssetListViewModel;
        var previousAddonGrid = ViewModelLocator.AddonGridViewModel;
        using var main = new MainWindowViewModel(manager, watcher, pending);
        using var addon = new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "123456789",
                Title = "Scrollable addon details",
                FolderPath = string.Empty,
                NeedsTitleUpdate = false
            },
            manager);

        var panel = new AddonDetailsFloatingPanel
        {
            DataContext = main
        };
        var window = new Window
        {
            Content = panel,
            Width = 1000,
            Height = 480
        };

        window.Show();
        try
        {
            main.AddonGridViewModel.SelectedAddon = addon;
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            await Task.Delay(450);
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            var backgroundOverlay = Assert.IsType<Border>(
                panel.FindControl<Border>("BackgroundOverlay"));
            var detailsControl = Assert.IsType<AddonDetailsControl>(
                panel.FindControl<AddonDetailsControl>("DetailsControl"));
            var detailsScrollViewer = Assert.IsType<ScrollViewer>(
                detailsControl.FindControl<ScrollViewer>("AddonDetailsScrollViewer"));

            Assert.True(
                detailsScrollViewer.Extent.Height > detailsScrollViewer.Viewport.Height,
                $"Expected overflowing details: extent={detailsScrollViewer.Extent.Height}, " +
                $"viewport={detailsScrollViewer.Viewport.Height}");
            Assert.Equal(
                ScrollBarVisibility.Auto,
                detailsScrollViewer.VerticalScrollBarVisibility);
            Assert.False(detailsScrollViewer.AllowAutoHide);

            var verticalScrollBar = Assert.Single(
                detailsScrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
                scrollBar =>
                    scrollBar.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(verticalScrollBar.IsEffectivelyVisible);

            var overlayPoint = new Point(300, 278);
            Assert.Same(backgroundOverlay, window.InputHitTest(overlayPoint));
            Assert.Equal(0, detailsScrollViewer.Offset.Y);

            window.MouseWheel(
                overlayPoint,
                new Vector(0, -3),
                RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            Assert.True(
                detailsScrollViewer.Offset.Y > 0,
                "A wheel gesture over the original addon-card area must scroll the open details panel.");
            var overlayWheelOffset = detailsScrollViewer.Offset.Y;

            detailsScrollViewer.Offset = detailsScrollViewer.Offset.WithY(0);
            var detailsPoint = detailsScrollViewer.TranslatePoint(
                new Point(
                    detailsScrollViewer.Bounds.Width / 2,
                    detailsScrollViewer.Bounds.Height / 2),
                window);
            Assert.NotNull(detailsPoint);

            window.MouseWheel(
                detailsPoint.Value,
                new Vector(0, -3),
                RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            Assert.Equal(
                overlayWheelOffset,
                detailsScrollViewer.Offset.Y,
                precision: 6);

            detailsScrollViewer.Offset = detailsScrollViewer.Offset.WithY(0);
            var thumb = Assert.Single(
                verticalScrollBar.GetVisualDescendants().OfType<Thumb>());
            var thumbStart = thumb.TranslatePoint(
                new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2),
                window);
            Assert.NotNull(thumbStart);
            var thumbEnd = thumbStart.Value.WithY(thumbStart.Value.Y + 80);

            window.MouseDown(
                thumbStart.Value,
                MouseButton.Left,
                RawInputModifiers.LeftMouseButton);
            window.MouseMove(thumbEnd, RawInputModifiers.LeftMouseButton);
            window.MouseUp(thumbEnd, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { });

            Assert.True(
                detailsScrollViewer.Offset.Y > 0,
                "The visible vertical scrollbar thumb must remain draggable.");
        }
        finally
        {
            window.Close();
            ViewModelLocator.AssetListViewModel = previousAssetList;
            ViewModelLocator.AddonGridViewModel = previousAddonGrid;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
