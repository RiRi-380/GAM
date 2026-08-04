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
    private Point? _dragStartPoint;
    private bool _isDragging;
    private bool _realizedItemExceptionLogged;

    private CancellationTokenSource? _scrollIdleCts;
    private ItemsRepeater? _itemsRepeater;
    private readonly Dictionary<Control, int> _realizedAddonIndices = new();
    private ResponsiveLayoutKind? _responsiveLayoutKind;

    public AddonGridView()
    {
        InitializeComponent();
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
        if (sender is not Border border ||
            border.DataContext is not AddonItemViewModel addonVm ||
            DataContext is not AddonGridViewModel gridVm)
        {
            return;
        }

        // Local addons are surfaced for discovery only. Return before any
        // selection clearing, selection-mode transition, or drag setup.
        if (addonVm.IsLocal)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (point.Properties.IsLeftButtonPressed)
        {
            gridVm.SelectAddon(addonVm.AddonId, isCtrlPressed);

            if (!gridVm.IsSelectionMode && gridVm.HasSelectedAddons)
            {
                gridVm.IsSelectionMode = true;
            }

            _dragStartPoint = point.Position;
            _isDragging = false;

            border.PointerMoved += OnAddonPointerMoved;
            border.PointerReleased += OnAddonPointerReleased;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            gridVm.SelectedAddon = addonVm;
        }
    }

    private async void OnAddonPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (sender is not Border border ||
                border.DataContext is not AddonItemViewModel addonVm ||
                DataContext is not AddonGridViewModel gridVm ||
                !_dragStartPoint.HasValue ||
                _isDragging)
            {
                return;
            }

            if (!gridVm.IsSelectionMode)
            {
                return;
            }

            var point = e.GetCurrentPoint(this);
            var distance = Math.Abs(point.Position.X - _dragStartPoint.Value.X) +
                           Math.Abs(point.Position.Y - _dragStartPoint.Value.Y);

            if (distance <= 5)
            {
                return;
            }

            _isDragging = true;

            var selectedAddons = gridVm.GetSelectedAddons();
            if (!selectedAddons.Any(a => a.AddonId == addonVm.AddonId))
            {
                gridVm.ClearSelection();
                gridVm.SelectAddon(addonVm.AddonId);
                selectedAddons = gridVm.GetSelectedAddons();
            }

            var dragData = new DataObject();
            var addonIds = selectedAddons.Select(a => a.AddonId).ToList();
            dragData.Set("AddonIds", addonIds);
            dragData.Set("DraggedAddons", selectedAddons);

            _ = await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy | DragDropEffects.Move);
            _isDragging = false;
            _dragStartPoint = null;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridView.OnAddonPointerMoved", ex);
            _isDragging = false;
            _dragStartPoint = null;
        }
    }

    private void OnAddonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        border.PointerMoved -= OnAddonPointerMoved;
        border.PointerReleased -= OnAddonPointerReleased;
        _dragStartPoint = null;
        _isDragging = false;
    }

    private async void OnAddonDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (sender is Border border && border.DataContext is AddonItemViewModel addonVm)
            {
                await addonVm.OpenFolderCommand.Execute();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridView.OnAddonDoubleTapped", ex);
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
        if (_itemsRepeater != null)
        {
            DetachRepeaterInteractions(_itemsRepeater);
            _itemsRepeater = null;
        }

        _scrollIdleCts?.Cancel();
        _scrollIdleCts?.Dispose();
        _scrollIdleCts = null;
    }
}
