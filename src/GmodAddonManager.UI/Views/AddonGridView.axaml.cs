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
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public sealed partial class AddonGridView : UserControl, IDisposable
{
    private Point? _dragStartPoint;
    private bool _isDragging;
    private int _lastStartIndex = -1;
    private int _lastEndIndex = -1;
    private bool _viewportExceptionLogged;

    private CancellationTokenSource? _scrollIdleCts;
    private ItemsRepeater? _itemsRepeater;

    public AddonGridView()
    {
        InitializeComponent();
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
            _itemsRepeater.EffectiveViewportChanged -= OnEffectiveViewportChanged;
            _itemsRepeater = null;
        }

        _itemsRepeater = this.FindControl<ItemsRepeater>("AddonItemsRepeater");
        if (_itemsRepeater != null)
        {
            _itemsRepeater.EffectiveViewportChanged += OnEffectiveViewportChanged;
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

    private void OnEffectiveViewportChanged(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not AddonGridViewModel gridVm || sender is not ItemsRepeater itemsRepeater)
            {
                return;
            }

            var scrollViewer = this.FindControl<ScrollViewer>("AddonScrollViewer");
            if (scrollViewer == null)
            {
                return;
            }

            var viewport = new Rect(0, scrollViewer.Offset.Y, scrollViewer.Viewport.Width, scrollViewer.Viewport.Height);

            double GetLayoutDouble(string name, double fallback)
            {
                var layout = itemsRepeater.Layout;
                if (layout == null)
                {
                    return fallback;
                }

                var property = layout.GetType().GetProperty(name);
                if (property?.PropertyType == typeof(double))
                {
                    return (double)(property.GetValue(layout) ?? fallback);
                }

                return fallback;
            }

            var itemHeight = GetLayoutDouble("MinItemHeight", 220);
            var itemWidth = GetLayoutDouble("MinItemWidth", 200);
            var rowSpacing = GetLayoutDouble("MinRowSpacing", 10);
            var columnSpacing = GetLayoutDouble("MinColumnSpacing", 10);
            var availableWidth = Math.Max(1, scrollViewer.Viewport.Width);
            var columns = Math.Max(1, (int)Math.Floor((availableWidth + columnSpacing) / (itemWidth + columnSpacing)));
            var rowHeight = itemHeight + rowSpacing;

            var startRow = Math.Max(0, (int)Math.Floor(viewport.Y / rowHeight) - 1);
            var visibleRows = Math.Ceiling(viewport.Height / rowHeight) + 2;
            var startIndex = Math.Max(0, startRow * columns);
            var endIndex = Math.Min(gridVm.FilteredAddons.Count, startIndex + (int)(visibleRows * columns));

            if (startIndex == _lastStartIndex && endIndex == _lastEndIndex)
            {
                return;
            }

            _lastStartIndex = startIndex;
            _lastEndIndex = endIndex;

            _ = LoadVisibleRangeSafeAsync(gridVm, startIndex, endIndex, allowRemote: false);
            ScheduleIdleRemoteLoad(gridVm, startIndex, endIndex);
        }
        catch (Exception ex)
        {
            if (!_viewportExceptionLogged)
            {
                _viewportExceptionLogged = true;
                SafeFileLogger.TryLogException("AddonGridView.OnEffectiveViewportChanged", ex);
            }
        }
    }

    private void ScheduleIdleRemoteLoad(AddonGridViewModel gridVm, int startIndex, int endIndex)
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
                    _ = LoadVisibleRangeSafeAsync(gridVm, startIndex, endIndex, allowRemote: true);
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    public void Dispose()
    {
        if (_itemsRepeater != null)
        {
            _itemsRepeater.EffectiveViewportChanged -= OnEffectiveViewportChanged;
            _itemsRepeater = null;
        }

        _scrollIdleCts?.Cancel();
        _scrollIdleCts?.Dispose();
        _scrollIdleCts = null;
    }
}
