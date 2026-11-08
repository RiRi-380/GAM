using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public sealed partial class AddonGridView : UserControl, IDisposable
{
    private static readonly TimeSpan PaneCloseVisibilityDelay =
        TimeSpan.FromMilliseconds(120);

    private Border? _pressedAddonCard;
    private IPointer? _pressedAddonPointer;
    private string? _pressedAddonId;
    private Point _addonPressPoint;
    private bool _addonPressWithControl;
    private bool _addonPressIsDoubleClick;
    private string? _lastClickedAddonId;
    private MainWindow? _addonDragWindow;
    private bool _realizedItemExceptionLogged;

    private CancellationTokenSource? _scrollIdleCts;
    private ItemsRepeater? _itemsRepeater;
    private readonly Dictionary<Control, int> _realizedAddonIndices = new();
    private readonly KeyboardNavigationMode _filterPaneOpenTabNavigationMode;
    private IDisposable? _filterPaneHideTimer;
    private ResponsiveLayoutKind? _responsiveLayoutKind;

    public AddonGridView()
    {
        InitializeComponent();
        _filterPaneOpenTabNavigationMode =
            KeyboardNavigation.GetTabNavigation(FilterPaneContent);
    }

    public void ApplyResponsiveLayout(ResponsiveLayoutState layout)
    {
        var transitionedFromWide = _responsiveLayoutKind == ResponsiveLayoutKind.Wide;

        FilterSplitView.DisplayMode = layout.UseOverlayPanes
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
        FilterSplitView.OpenPaneLength = layout.FilterPaneWidth;
        FilterPaneToggleButton.IsVisible = layout.UseOverlayPanes;
        FilterPaneCloseButton.IsVisible = layout.UseOverlayPanes;

        if (!layout.UseOverlayPanes)
        {
            FilterSplitView.IsPaneOpen = true;
        }
        else if (_responsiveLayoutKind is null || transitionedFromWide)
        {
            FilterSplitView.IsPaneOpen = false;
        }

        _responsiveLayoutKind = layout.Kind;
    }

    private void OnFilterPaneToggleClick(object? sender, RoutedEventArgs e)
    {
        FilterSplitView.IsPaneOpen = !FilterSplitView.IsPaneOpen;
    }

    private void OnFilterPaneOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, FilterSplitView))
        {
            return;
        }

        RestoreFilterPaneContent();
    }

    private void OnFilterPaneClosed(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, FilterSplitView))
        {
            return;
        }

        if (!IsOverlayMode(FilterSplitView.DisplayMode))
        {
            RestoreFilterPaneContent();
            return;
        }

        var returnFocusToToggle = FilterPaneContent.IsKeyboardFocusWithin;
        FilterPaneContent.IsEnabled = false;
        FilterPaneContent.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(
            FilterPaneContent,
            KeyboardNavigationMode.None);

        if (returnFocusToToggle && FilterPaneToggleButton.IsVisible)
        {
            FilterPaneToggleButton.Focus();
        }

        _filterPaneHideTimer?.Dispose();
        _filterPaneHideTimer = DispatcherTimer.RunOnce(
            () =>
            {
                _filterPaneHideTimer = null;
                if (!FilterSplitView.IsPaneOpen &&
                    IsOverlayMode(FilterSplitView.DisplayMode))
                {
                    FilterPaneContent.IsVisible = false;
                    if (ReferenceEquals(FilterSplitView.Pane, FilterPaneContent))
                    {
                        FilterSplitView.Pane = null;
                    }
                }
            },
            PaneCloseVisibilityDelay,
            DispatcherPriority.Normal);
    }

    private void RestoreFilterPaneContent()
    {
        _filterPaneHideTimer?.Dispose();
        _filterPaneHideTimer = null;
        FilterPaneContent.IsVisible = true;
        FilterPaneContent.IsEnabled = true;
        FilterPaneContent.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(
            FilterPaneContent,
            _filterPaneOpenTabNavigationMode);
        if (!ReferenceEquals(FilterSplitView.Pane, FilterPaneContent))
        {
            FilterSplitView.Pane = FilterPaneContent;
        }
    }

    private static bool IsOverlayMode(SplitViewDisplayMode displayMode) =>
        displayMode is SplitViewDisplayMode.Overlay or
            SplitViewDisplayMode.CompactOverlay;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(AttachInteractions, DispatcherPriority.Background);
    }

    private void AttachInteractions()
    {
        if (!this.IsAttachedToVisualTree())
        {
            return;
        }

        if (_itemsRepeater != null)
        {
            DetachRepeaterInteractions(_itemsRepeater);
            _itemsRepeater = null;
        }

        _itemsRepeater = this.FindControl<ItemsRepeater>("AddonItemsRepeater");
        if (_itemsRepeater != null)
        {
            _itemsRepeater.ElementPrepared += OnRepeaterElementPrepared;
            _itemsRepeater.ElementClearing += OnRepeaterElementClearing;
            _itemsRepeater.ElementIndexChanged += OnRepeaterElementIndexChanged;

            // Elements may already be realized before this deferred attachment
            // runs. Seed the exact indices from the repeater instead of deriving
            // them from card dimensions or scroll offsets.
            foreach (var element in _itemsRepeater.GetVisualChildren().OfType<Control>())
            {
                var index = _itemsRepeater.GetElementIndex(element);
                if (index >= 0)
                {
                    _realizedAddonIndices[element] = index;
                }
            }

            ScheduleIdleRemoteLoad();
        }

    }

    private void OnAddonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: AddonItemViewModel addon } card ||
            DataContext is not AddonGridViewModel gridVm || addon.IsLocal)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            var isDoubleClick = e.ClickCount % 2 == 0 &&
                                string.Equals(_lastClickedAddonId, addon.AddonId, StringComparison.Ordinal);
            CancelAddonGesture();
            _pressedAddonCard = card;
            _pressedAddonPointer = e.Pointer;
            _pressedAddonId = addon.AddonId;
            _addonPressPoint = point.Position;
            _addonPressWithControl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            _addonPressIsDoubleClick = isDoubleClick;
            card.PointerMoved += OnAddonPointerMoved;
            card.PointerReleased += OnAddonPointerReleased;
            card.PointerCaptureLost += OnAddonPointerCaptureLost;
            e.Pointer.Capture(card);
        }
        else if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _lastClickedAddonId = null;
            gridVm.SelectedAddon = addon;
        }
    }

    private void OnAddonGotFocus(object? sender, GotFocusEventArgs e)
    {
        // The pointer can grab a partially visible card without scrolling to its edge.
        if (e.NavigationMethod != NavigationMethod.Pointer && e.Source is Control focused)
        {
            focused.BringIntoView();
        }
    }

    private void OnAddonKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Border border ||
            !ReferenceEquals(e.Source, border) ||
            e.Key is not (Key.Enter or Key.Space) ||
            border.DataContext is not AddonItemViewModel addonVm ||
            DataContext is not AddonGridViewModel gridVm ||
            addonVm.IsLocal)
        {
            return;
        }

        SelectAddonFromCard(gridVm, addonVm.AddonId, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        e.Handled = true;
    }

    private static void SelectAddonFromCard(AddonGridViewModel gridVm, string addonId, bool withControl)
    {
        gridVm.SelectAddon(addonId, withControl);
        if (!gridVm.IsSelectionMode && gridVm.HasSelectedAddons)
        {
            gridVm.IsSelectionMode = true;
        }
    }

    private void OnAddonPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (!ReferenceEquals(e.Pointer, _pressedAddonPointer))
            {
                return;
            }

            if (_addonDragWindow is { } activeWindow)
            {
                activeWindow.UpdateAddonDrag(e.GetPosition(activeWindow));
                e.Handled = true;
                return;
            }

            var movement = e.GetPosition(this) - _addonPressPoint;
            if (Math.Abs(movement.X) <= 5 && Math.Abs(movement.Y) <= 5)
            {
                return;
            }

            if (sender is not Border { DataContext: AddonItemViewModel addon } ||
                !string.Equals(addon.AddonId, _pressedAddonId, StringComparison.Ordinal) ||
                DataContext is not AddonGridViewModel gridVm ||
                TopLevel.GetTopLevel(this) is not MainWindow window)
            {
                CancelAddonGesture();
                return;
            }

            _addonDragWindow = window;
            if (!window.BeginAddonDrag(gridVm.GetDraggedAddonIds(addon), e.GetPosition(window)))
            {
                CancelAddonGesture();
                return;
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridView.OnAddonPointerMoved", ex);
            CancelAddonGesture();
        }
    }

    private async void OnAddonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pressedAddonPointer) ||
            e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        var dragWindow = _addonDragWindow;
        var addonId = _pressedAddonId;
        var withControl = _addonPressWithControl;
        var isDoubleClick = _addonPressIsDoubleClick;
        ResetAddonPress();

        if (dragWindow != null)
        {
            e.Handled = true;
            try
            {
                await dragWindow.CompleteAddonDragAsync(e.GetPosition(dragWindow));
            }
            catch (Exception ex)
            {
                dragWindow.ClearAddonDrag();
                SafeFileLogger.TryLogException("AddonGridView.OnAddonPointerReleased", ex);
            }
        }
        else if (sender is Border { DataContext: AddonItemViewModel addon } card &&
                 string.Equals(addon.AddonId, addonId, StringComparison.Ordinal) &&
                 new Rect(card.Bounds.Size).Contains(e.GetPosition(card)) &&
                 DataContext is AddonGridViewModel gridVm)
        {
            SelectAddonFromCard(gridVm, addon.AddonId, withControl);
            if (!isDoubleClick)
            {
                _lastClickedAddonId = addon.AddonId;
                return;
            }

            // Avalonia's DoubleTapped fires on the second press, before drag intent is known.
            try
            {
                await addon.OpenWorkshopCommand.Execute();
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AddonGridView.OpenWorkshopAfterClick", ex);
            }
        }
    }

    private void OnAddonPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        CancelAddonGesture();

    internal void CancelAddonGesture()
    {
        var dragWindow = _addonDragWindow;
        _lastClickedAddonId = null;
        ResetAddonPress();
        dragWindow?.ClearAddonDrag();
    }

    private void ResetAddonPress()
    {
        var card = _pressedAddonCard;
        var pointer = _pressedAddonPointer;
        _pressedAddonCard = null;
        _pressedAddonPointer = null;
        _pressedAddonId = null;
        _addonPressIsDoubleClick = false;
        _addonDragWindow = null;
        if (card == null)
        {
            return;
        }

        card.PointerMoved -= OnAddonPointerMoved;
        card.PointerReleased -= OnAddonPointerReleased;
        card.PointerCaptureLost -= OnAddonPointerCaptureLost;
        if (pointer != null && ReferenceEquals(pointer.Captured, card))
        {
            pointer.Capture(null);
        }
    }

    private void OnRepeaterElementPrepared(
        object? sender,
        ItemsRepeaterElementPreparedEventArgs e)
    {
        try
        {
            if (sender is not ItemsRepeater || e.Index < 0)
            {
                return;
            }

            _realizedAddonIndices[e.Element] = e.Index;
            ScheduleIdleRemoteLoad();
        }
        catch (Exception ex)
        {
            LogRealizedItemExceptionOnce("AddonGridView.OnRepeaterElementPrepared", ex);
        }
    }

    private void OnRepeaterElementClearing(
        object? sender,
        ItemsRepeaterElementClearingEventArgs e)
    {
        try
        {
            if (ReferenceEquals(e.Element, _pressedAddonCard))
            {
                CancelAddonGesture();
            }

            if (e.Element.DataContext is AddonItemViewModel addonVm)
            {
                addonVm.ReleaseThumbnailBitmap();
            }

            _realizedAddonIndices.Remove(e.Element);
            ScheduleIdleRemoteLoad();
        }
        catch (Exception ex)
        {
            LogRealizedItemExceptionOnce("AddonGridView.OnRepeaterElementClearing", ex);
        }
    }

    private void OnRepeaterElementIndexChanged(
        object? sender,
        ItemsRepeaterElementIndexChangedEventArgs e)
    {
        try
        {
            if (e.NewIndex >= 0)
            {
                _realizedAddonIndices[e.Element] = e.NewIndex;
            }
            else
            {
                _realizedAddonIndices.Remove(e.Element);
            }

            ScheduleIdleRemoteLoad();
        }
        catch (Exception ex)
        {
            LogRealizedItemExceptionOnce("AddonGridView.OnRepeaterElementIndexChanged", ex);
        }
    }

    private void ScheduleIdleRemoteLoad()
    {
        _scrollIdleCts?.Cancel();
        _scrollIdleCts?.Dispose();
        _scrollIdleCts = new CancellationTokenSource();
        var token = _scrollIdleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (DataContext is not AddonGridViewModel gridVm)
                    {
                        return;
                    }

                    var ranges = GetRealizedRanges(
                        _realizedAddonIndices.Values,
                        gridVm.FilteredAddons.Count);
                    if (ranges.Count == 0)
                    {
                        return;
                    }

                    _ = LoadRealizedRangesSafeAsync(
                        gridVm,
                        ranges,
                        token);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AddonGridView.ScheduleIdleRemoteLoad", ex);
            }
        });
    }

    private static IReadOnlyList<(int StartIndex, int EndIndex)> GetRealizedRanges(
        IEnumerable<int> realizedIndices,
        int itemCount)
    {
        if (itemCount <= 0)
        {
            return Array.Empty<(int StartIndex, int EndIndex)>();
        }

        var orderedIndices = realizedIndices
            .Where(index => index >= 0 && index < itemCount)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        if (orderedIndices.Count == 0)
        {
            return Array.Empty<(int StartIndex, int EndIndex)>();
        }

        var ranges = new List<(int StartIndex, int EndIndex)>();
        var rangeStart = orderedIndices[0];
        var previousIndex = orderedIndices[0];
        foreach (var index in orderedIndices.Skip(1))
        {
            if (index != previousIndex + 1)
            {
                ranges.Add((rangeStart, previousIndex + 1));
                rangeStart = index;
            }

            previousIndex = index;
        }

        ranges.Add((rangeStart, previousIndex + 1));
        return ranges;
    }

    private void LogRealizedItemExceptionOnce(string context, Exception exception)
    {
        if (_realizedItemExceptionLogged)
        {
            return;
        }

        _realizedItemExceptionLogged = true;
        SafeFileLogger.TryLogException(context, exception);
    }

    private void DetachRepeaterInteractions(ItemsRepeater itemsRepeater)
    {
        itemsRepeater.ElementPrepared -= OnRepeaterElementPrepared;
        itemsRepeater.ElementClearing -= OnRepeaterElementClearing;
        itemsRepeater.ElementIndexChanged -= OnRepeaterElementIndexChanged;

        foreach (var element in _realizedAddonIndices.Keys)
        {
            if (element.DataContext is AddonItemViewModel addonVm)
            {
                addonVm.ReleaseThumbnailBitmap();
            }
        }

        _realizedAddonIndices.Clear();
    }

    private async Task LoadVisibleRangeSafeAsync(
        AddonGridViewModel gridVm,
        int startIndex,
        int endIndex,
        bool allowRemote)
    {
        try
        {
            await gridVm.LoadVisibleRangeAsync(startIndex, endIndex, allowRemote);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridView.LoadVisibleRangeSafeAsync", ex);
        }
    }

    private async Task LoadRealizedRangesSafeAsync(
        AddonGridViewModel gridVm,
        IReadOnlyList<(int StartIndex, int EndIndex)> ranges,
        CancellationToken token)
    {
        foreach (var range in ranges)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            await LoadVisibleRangeSafeAsync(
                gridVm,
                range.StartIndex,
                range.EndIndex,
                allowRemote: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    public void Dispose()
    {
        CancelAddonGesture();
        if (_itemsRepeater != null)
        {
            DetachRepeaterInteractions(_itemsRepeater);
            _itemsRepeater = null;
        }

        _scrollIdleCts?.Cancel();
        _scrollIdleCts?.Dispose();
        _scrollIdleCts = null;
        _filterPaneHideTimer?.Dispose();
        _filterPaneHideTimer = null;
    }
}
