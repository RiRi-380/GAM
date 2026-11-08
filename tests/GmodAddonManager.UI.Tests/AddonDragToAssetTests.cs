using Avalonia;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonDragToAssetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"gam-addon-drag-{Guid.NewGuid():N}");

    [AvaloniaFact]
    public Task DragSelectedAddonsThroughGroupsAndBreadcrumbsAndUndoOnlyNewMembers() =>
        WithWindowAsync(async (window, main, manager) =>
        {
            var grid = main.AddonGridViewModel;
            var list = main.AssetListViewModel;
            var source = Assert.IsType<AssetItemViewModel>(list.SelectedAsset);
            var sourceModel = manager.GetConfiguration().Assets.Single(asset => asset.Id == "source");
            var destination = manager.GetConfiguration().Assets.Single(asset => asset.Id == "destination");

            Click(window, AddonPoint(window, "101"));
            Click(window, AddonPoint(window, "102"));
            Assert.Equal(2, grid.SelectedAddonsCount);

            var start = AddonPoint(window, "101");
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Assert.Equal(2, grid.SelectedAddonsCount);
            var groupHover = Stopwatch.StartNew();
            Move(window, EntryPoint(window, "outer"));
            Assert.Null(list.CurrentGroupId);
            Assert.Contains("2", window.FindControl<TextBlock>("AddonDragText")!.Text);
            await WaitUntilAsync(() => list.CurrentGroupId == "outer");
            Assert.True(groupHover.Elapsed >= TimeSpan.FromSeconds(1));
            Assert.Same(source, list.SelectedAsset);
            Assert.True(list.IsAddonGridVisible);
            Assert.Equal(2, grid.SelectedAddonsCount);

            Move(window, EntryPoint(window, "inner"));
            await WaitUntilAsync(() => list.CurrentGroupId == "inner");
            var rootBreadcrumb = window.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible &&
                button.DataContext is AssetBreadcrumbItemViewModel { TargetGroupId: null, IsCurrent: false });
            Move(window, Center(window, rootBreadcrumb));
            await WaitUntilAsync(() => list.IsAtRoot);
            Move(window, EntryPoint(window, "outer"));
            await WaitUntilAsync(() => list.CurrentGroupId == "outer");
            Move(window, EntryPoint(window, "inner"));
            await WaitUntilAsync(() => list.CurrentGroupId == "inner");

            var dropPoint = EntryPoint(window, "destination") + new Vector(10, 0);
            Move(window, dropPoint);
            Assert.True(window.FindControl<Border>("AddonDropOutline")!.IsVisible);
            Assert.Equal(L.Format("AddonDrop.Destination", destination.Name, 2),
                window.FindControl<TextBlock>("AddonDragText")!.Text);
            var cards = grid.FilteredAddons;
            window.MouseUp(dropPoint, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);

            Assert.Equal(["101", "102"], destination.Addons);
            Assert.Same(cards, grid.FilteredAddons);
            Assert.Equal(["101", "102", "103"], sourceModel.Addons);
            Assert.Same(source, list.SelectedAsset);
            Assert.Equal(0, grid.SelectedAddonsCount);
            Assert.False(grid.IsSelectionMode);
            Assert.False(window.FindControl<Border>("AddonDragBadge")!.IsVisible);
            Assert.Equal(L.Format("AddonDrop.ResultWithDuplicates", destination.Name, 1, 1),
                main.StatusBarViewModel.StatusMessage);

            var undo = Assert.IsType<UndoAction>(manager.GetUndoManager().PeekLastAction());
            Assert.Equal(UndoActionType.AddonAddedToAsset, undo.Type);
            Assert.Equal(["102"], undo.AffectedAddonIds);
            await main.UndoLastActionAsync();
            Assert.Equal(["101"], destination.Addons);
            Assert.Equal(["101", "102", "103"], sourceModel.Addons);
            Assert.Same(source, list.SelectedAsset);
            Assert.Equal("inner", list.CurrentGroupId);
            Assert.Same(cards, grid.FilteredAddons);
            Assert.Equal(1, list.GetAssetById(destination.Id)!.AddonCount);

            var destinationCard = list.GetAssetById(destination.Id)!;
            await main.AddDroppedAddonsToAssetAsync(destinationCard, ["102"]);
            list.SelectedAsset = destinationCard;
            grid.SetCurrentAsset(destinationCard);
            var destinationCards = grid.FilteredAddons;
            Assert.Equal(2, destinationCards.Count);
            grid.SelectAddon("101");
            grid.IsSelectionMode = true;
            grid.SelectAddon("102");
            Assert.Equal(2, grid.SelectedAddonsCount);

            await main.UndoLastActionAsync();
            Assert.Same(destinationCard, list.SelectedAsset);
            Assert.Same(destinationCards, grid.FilteredAddons);
            Assert.Equal("101", Assert.Single(destinationCards).AddonId);
            Assert.Equal(1, grid.SelectedAddonsCount);
            Assert.Equal("101", Assert.Single(grid.GetSelectedAddons()).AddonId);
        });

    [AvaloniaFact]
    public Task DragOneWithoutSelectionAndCancelAtAnEdgeOrWithEscape() =>
        WithWindowAsync(async (window, main, manager) =>
        {
            var grid = main.AddonGridViewModel;
            var target = manager.GetConfiguration().Assets.Single(asset => asset.Id == "root-target");
            var start = AddonPoint(window, "103");
            var end = EntryPoint(window, target.Id);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, end);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            Assert.Equal(["103"], target.Addons);
            Assert.Equal(0, grid.SelectedAddonsCount);

            Click(window, AddonPoint(window, "101"));
            window.MouseDown(AddonPoint(window, "102"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            end = EntryPoint(window, target.Id);
            Move(window, end);
            Assert.Equal(L.Format("AddonDrop.Destination", target.Name, 1),
                window.FindControl<TextBlock>("AddonDragText")!.Text);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            Assert.Equal(["103", "102"], target.Addons);
            Assert.Equal("101", Assert.Single(grid.GetSelectedAddons()).AddonId);

            window.MouseDown(AddonPoint(window, "101"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, end);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(["103", "102"], target.Addons);
            Assert.Equal("101", Assert.Single(grid.GetSelectedAddons()).AddonId);
            Assert.False(window.FindControl<Border>("AddonDragBadge")!.IsVisible);

            window.MouseDown(AddonPoint(window, "101"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, end);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            Assert.Equal(["103", "102", "101"], target.Addons);
            Assert.Empty(grid.GetSelectedAddons());

            var lastUndo = manager.GetUndoManager().PeekLastAction();
            Click(window, AddonPoint(window, "101"));
            window.MouseDown(AddonPoint(window, "101"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, end);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.Same(lastUndo, manager.GetUndoManager().PeekLastAction());
            Assert.Empty(grid.GetSelectedAddons());

            var pane = window.FindControl<AssetListView>("AssetPaneContent")!;
            var scroller = pane.FindControl<ScrollViewer>("AssetScrollViewer")!;
            var edge = scroller.TranslatePoint(new Point(100, scroller.Bounds.Height - 9), window)!.Value;
            window.MouseDown(AddonPoint(window, "101"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, edge);
            await WaitUntilAsync(() => scroller.Offset.Y > 20 &&
                scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 12,
                () => $"Offset={scroller.Offset.Y}, extent={scroller.Extent.Height}, viewport={scroller.Viewport.Height}, " +
                      $"hit={window.InputHitTest(edge)?.GetType().Name}, " +
                      $"dragging={window.FindControl<Border>("AddonDragBadge")!.IsVisible}",
                timeout: TimeSpan.FromSeconds(10));
            var lowerTarget = EntryPoint(window, "extra-14");
            Move(window, lowerTarget);
            window.MouseUp(lowerTarget, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            Assert.Equal(["101"], manager.GetConfiguration().Assets.Single(asset => asset.Id == "extra-14").Addons);

            Click(window, AddonPoint(window, "101"));
            window.MouseDown(AddonPoint(window, "101"), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, lowerTarget);
            var outside = AddonPoint(window, "103");
            Move(window, outside);
            window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
            Assert.False(window.FindControl<Border>("AddonDragBadge")!.IsVisible);
            Assert.Equal(["103", "102", "101"], target.Addons);
            Assert.Equal("101", Assert.Single(grid.GetSelectedAddons()).AddonId);
        });

    [AvaloniaFact]
    public Task DoubleClickDispatchesWorkshopCommandWithoutChangingSingleClickSelection() =>
        WithWindowAsync(async (window, main, manager) =>
        {
            var source = manager.GetConfiguration().Assets.Single(asset => asset.Id == "source");
            const string id = "workshop-command-test";
            source.Addons.Add(id);
            var grid = main.AddonGridViewModel;
            var addon = new AddonItemViewModel(
                new WorkshopAddon { Id = id, Title = "Workshop command test", NeedsTitleUpdate = false },
                manager);
            grid.AllAddons.Add(addon);
            grid.ApplyFilter();
            Assert.False(addon.HasWorkshopId);
            var calls = 0;
            using var subscription = addon.OpenWorkshopCommand.Subscribe(_ => calls++);

            var point = AddonPoint(window, id);
            Click(window, point);
            Assert.Equal(id, Assert.Single(grid.GetSelectedAddons()).AddonId);
            Assert.Equal(0, calls);
            Click(window, point);
            await WaitUntilAsync(() => calls == 1);
            Assert.Empty(grid.GetSelectedAddons());
        });

    [AvaloniaFact]
    public Task DragOpensTheAssetPaneInACompactWindowAndAddsToTheTarget() =>
        WithWindowAsync(async (window, main, manager) =>
        {
            var pane = window.FindControl<SplitView>("AssetSplitView")!;
            Assert.False(pane.IsPaneOpen);
            var start = AddonPoint(window, "103");
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Move(window, start + new Vector(-12, 0));
            Assert.True(pane.IsPaneOpen);
            var end = EntryPoint(window, "root-target");
            var paneRoot = pane.GetVisualDescendants().OfType<Panel>()
                .First(panel => panel.Name == "PART_PaneRoot");
            Assert.Equal(pane.OpenPaneLength, paneRoot.Bounds.Width);
            Move(window, end);
            Assert.True(window.FindControl<Border>("AddonDropOutline")!.IsVisible);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            var target = manager.GetConfiguration().Assets.Single(asset => asset.Id == "root-target");
            Assert.Equal(["103"], target.Addons);
        }, windowWidth: 900);

    [AvaloniaFact]
    public Task DropRendersBusyWhileSavingAndKeepsTheScrolledCardAndThumbnail() =>
        WithWindowAsync(async (window, main, manager) =>
        {
            var view = window.FindControl<AddonGridView>("AddonGridControl")!;
            var scroller = view.FindControl<ScrollViewer>("AddonScrollViewer")!;
            window.UpdateLayout();
            scroller.Offset = new Vector(0, 450);
            window.UpdateLayout();
            Assert.True(scroller.Offset.Y > 0);

            var viewport = new Rect(scroller.TranslatePoint(default, window)!.Value, scroller.Bounds.Size);
            var card = view.GetVisualDescendants().OfType<Border>().First(border =>
                border.Classes.Contains("addonCard") && border.DataContext is AddonItemViewModel &&
                border.IsEffectivelyVisible && viewport.Contains(Center(window, border)));
            var addon = Assert.IsType<AddonItemViewModel>(card.DataContext);
            await addon.LoadThumbnailAsync(allowRemote: false);
            var thumbnail = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(addon.ThumbnailBitmap);
            var cards = main.AddonGridViewModel.FilteredAddons;
            var destination = manager.GetConfiguration().Assets.Single(asset => asset.Id == "root-target");
            var end = EntryPoint(window, destination.Id);
            var offset = scroller.Offset;
            var offsetAfterPress = offset;
            var offsetAfterDrag = offset;
            var offsetAfterRelease = offset;

            var saveGate = Assert.IsType<SemaphoreSlim>(typeof(AddonManager)
                .GetField("_configurationSaveGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager));
            await saveGate.WaitAsync();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var releaseSave = watchdog.Token.Register(() => saveGate.Release());
            try
            {
                window.MouseDown(Center(window, card), MouseButton.Left, RawInputModifiers.LeftMouseButton);
                window.UpdateLayout();
                offsetAfterPress = scroller.Offset;
                Move(window, end);
                offsetAfterDrag = scroller.Offset;
                window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
                await Dispatcher.UIThread.InvokeAsync(() => Assert.True(main.IsBusy));
                window.UpdateLayout();
                offsetAfterRelease = scroller.Offset;
                Assert.True(main.IsUpdatingAssetMembership);
            }
            finally
            {
                watchdog.Cancel();
                await WaitUntilAsync(() => !main.IsUpdatingAssetMembership);
            }

            window.UpdateLayout();
            Assert.Equal([addon.AddonId], destination.Addons);
            Assert.Same(cards, main.AddonGridViewModel.FilteredAddons);
            Assert.True(offset == scroller.Offset,
                $"Scroll offset {offset} → press {offsetAfterPress} → drag {offsetAfterDrag} → " +
                $"release {offsetAfterRelease} → completed {scroller.Offset}");
            Assert.Same(card, FindCard(window, "addonCard", item => ReferenceEquals(item, addon)));
            Assert.Same(thumbnail, addon.ThumbnailBitmap);

            window.FindControl<DockPanel>("MainDockPanel")!.Focus();
            Assert.True(card.Focus(NavigationMethod.Tab));
            window.UpdateLayout();
            Assert.NotEqual(offset, scroller.Offset);
        }, extraSourceAddons: 28);

    private async Task WithWindowAsync(
        Func<MainWindow, MainWindowViewModel, AddonManager, Task> test,
        int extraSourceAddons = 0,
        double windowWidth = 1400)
    {
        var workshop = Path.Combine(root, "workshop", "content", "4000");
        var appData = Path.Combine(root, "appdata");
        var gmod = Path.Combine(root, "gmod");
        var manifest = Path.Combine(root, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(Path.Combine(gmod, "garrysmod", "cfg"));
        File.WriteAllText(manifest, """
            "AppWorkshop"
            {
                "WorkshopItemDetails" { }
                "WorkshopItemsInstalled" { }
            }
            """);
        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshop,
            CustomAppDataPath = appData,
            CustomGmodInstallPath = gmod,
            CustomWorkshopCacheFilePaths = [manifest],
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });
        await manager.InitializeAsync();
        var configuration = manager.GetConfiguration();
        configuration.AssetGroups.AddRange(
        [
            new AssetGroup("Outer") { Id = "outer", SortOrder = 2 },
            new AssetGroup("Inner") { Id = "inner", ParentGroupId = "outer" }
        ]);
        var sourceIds = new[] { "101", "102", "103" }
            .Concat(Enumerable.Range(200, extraSourceAddons).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        configuration.Assets.AddRange(
        [
            new Asset("Source") { Id = "source", SortOrder = 0, Addons = sourceIds },
            new Asset("Root target") { Id = "root-target", SortOrder = 1 },
            new Asset("Destination") { Id = "destination", ParentGroupId = "inner", Addons = ["101"] }
        ]);
        for (var i = 0; i < 15; i++)
        {
            configuration.Assets.Add(new Asset($"Extra {i}") { Id = $"extra-{i}", SortOrder = i + 3 });
        }

        using var watcher = new GmodProcessWatcher();
        var pending = new PendingChangeManager(manager, appData);
        var previousList = ViewModelLocator.AssetListViewModel;
        var previousGrid = ViewModelLocator.AddonGridViewModel;
        using var main = new MainWindowViewModel(manager, watcher, pending, new ConfirmUndoDialogs());
        main.AssetListViewModel.LoadAssets();
        main.AssetListViewModel.SelectedAsset = main.AssetListViewModel.GetAssetById("source");
        var thumbnailFolder = string.Empty;
        if (extraSourceAddons > 0)
        {
            thumbnailFolder = Directory.CreateDirectory(Path.Combine(root, "thumbnail")).FullName;
            File.WriteAllBytes(Path.Combine(thumbnailFolder, "addon.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a/vkAAAAASUVORK5CYII="));
        }
        foreach (var id in sourceIds)
        {
            main.AddonGridViewModel.AllAddons.Add(new AddonItemViewModel(
                new WorkshopAddon
                {
                    Id = id,
                    Title = $"Test addon {id}",
                    FolderPath = thumbnailFolder,
                    NeedsTitleUpdate = false
                },
                manager));
        }

        main.AddonGridViewModel.SetCurrentAsset(main.AssetListViewModel.SelectedAsset);
        using var window = new MainWindow { WindowState = WindowState.Normal, Width = windowWidth, Height = 800 };
        // Show first to skip real startup scanning and update checks in this isolated test.
        window.Show();
        window.DataContext = main;
        try
        {
            await test(window, main, manager);
        }
        finally
        {
            window.Close();
            ViewModelLocator.AssetListViewModel = previousList;
            ViewModelLocator.AddonGridViewModel = previousGrid;
        }
    }

    private static Point AddonPoint(MainWindow window, string id) =>
        Center(window, FindCard(window, "addonCard", item => item is AddonItemViewModel addon && addon.AddonId == id));

    private static Point EntryPoint(MainWindow window, string id)
    {
        var card = FindCard(window, "assetEntryCard", item => item is AssetListEntryViewModel entry && entry.Id == id);
        return card.TranslatePoint(new Point(115, 17), window)!.Value;
    }

    private static Border FindCard(MainWindow window, string className, Func<object?, bool> matches)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<Border>().Single(border =>
            border.IsEffectivelyVisible && border.Classes.Contains(className) && matches(border.DataContext));
    }

    private static Point Center(MainWindow window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(MainWindow window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static void Move(MainWindow window, Point point) =>
        window.MouseMove(point, RawInputModifiers.LeftMouseButton);

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        Func<string>? details = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(4));
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(static () => { });
        }
        Assert.True(predicate(), details?.Invoke() ?? "The drag timer did not reach the expected UI state.");
    }

    private sealed class ConfirmUndoDialogs : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) =>
            Task.FromException(new InvalidOperationException(message));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
