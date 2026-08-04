using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;

namespace GmodAddonManager.UI.Views;

public partial class AssetListView : UserControl
{
    private const double WheelPixels = 60;
    private const double InertiaFriction = 0.88;
    private const double InertiaStopThreshold = 0.2;
    private const double MaxInertiaVelocity = 1800;
    private const double HoldMoveTolerance = 7;
    private static readonly TimeSpan ReorderHoldDuration = TimeSpan.FromMilliseconds(360);

    private ScrollViewer? assetScrollViewer;
    private ScrollViewer? breadcrumbScrollViewer;
    private AssetListViewModel? breadcrumbViewModel;
    private Border? insertionMarker;
    private DispatcherTimer? inertiaTimer;
    private DispatcherTimer? reorderHoldTimer;
    private double scrollVelocity;
    private Border? pressedCard;
    private AssetListEntryViewModel? pressedEntry;
    private IPointer? pressedPointer;
    private Point pressPoint;
    private bool isDragging;
    private int dragTargetIndex = -1;

    public AssetListView()
    {
        InitializeComponent();
        DataContextChanged += OnAssetListDataContextChanged;
    }

    private void OnAssetListDataContextChanged(object? sender, EventArgs e)
    {
        if (breadcrumbScrollViewer == null)
        {
            return;
        }

        AttachBreadcrumbViewModel(DataContext as AssetListViewModel);
        ScheduleBreadcrumbScrollToEnd();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        assetScrollViewer = this.FindControl<ScrollViewer>("AssetScrollViewer");
        breadcrumbScrollViewer = this.FindControl<ScrollViewer>("BreadcrumbScrollViewer");
        insertionMarker = this.FindControl<Border>("InsertionMarker");
        if (assetScrollViewer != null)
        {
            assetScrollViewer.PointerWheelChanged += OnAssetScrollWheelChanged;
        }
        AttachBreadcrumbViewModel(DataContext as AssetListViewModel);
        ScheduleBreadcrumbScrollToEnd();
        EnsureInertiaTimer();
        EnsureReorderHoldTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (assetScrollViewer != null)
        {
            assetScrollViewer.PointerWheelChanged -= OnAssetScrollWheelChanged;
        }
        AttachBreadcrumbViewModel(null);

        StopInertia();
        ResetReorderGesture(releaseCapture: true);
        if (inertiaTimer != null)
        {
            inertiaTimer.Tick -= OnInertiaTick;
            inertiaTimer = null;
        }
        if (reorderHoldTimer != null)
        {
            reorderHoldTimer.Tick -= OnReorderHoldElapsed;
            reorderHoldTimer = null;
        }

        insertionMarker = null;
        breadcrumbScrollViewer = null;
        assetScrollViewer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachBreadcrumbViewModel(AssetListViewModel? viewModel)
    {
        if (ReferenceEquals(breadcrumbViewModel, viewModel))
        {
            return;
        }

        if (breadcrumbViewModel != null)
        {
            breadcrumbViewModel.Breadcrumbs.CollectionChanged -= OnBreadcrumbsChanged;
        }

        breadcrumbViewModel = viewModel;
        if (breadcrumbViewModel != null)
        {
            breadcrumbViewModel.Breadcrumbs.CollectionChanged += OnBreadcrumbsChanged;
        }
    }

    private void OnBreadcrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleBreadcrumbScrollToEnd();
    }

    private void ScheduleBreadcrumbScrollToEnd()
    {
        Dispatcher.UIThread.Post(ScrollBreadcrumbToEnd, DispatcherPriority.Render);
    }

    private void ScrollBreadcrumbToEnd()
    {
        if (breadcrumbScrollViewer == null)
        {
            return;
        }

        var maxOffset = Math.Max(
            0,
            breadcrumbScrollViewer.Extent.Width - breadcrumbScrollViewer.Viewport.Width);
        breadcrumbScrollViewer.Offset = new Vector(
            maxOffset,
            breadcrumbScrollViewer.Offset.Y);
    }

    private void OnBreadcrumbPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
            ? e.Delta.X
            : e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        var maxOffset = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        var nextOffset = Clamp(
            scrollViewer.Offset.X - (delta * WheelPixels),
            0,
            maxOffset);
        if (Math.Abs(nextOffset - scrollViewer.Offset.X) < double.Epsilon)
        {
            return;
        }

        scrollViewer.Offset = new Vector(nextOffset, scrollViewer.Offset.Y);
        e.Handled = true;
    }

    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card ||
            card.DataContext is not AssetListEntryViewModel entry ||
            DataContext is not AssetListViewModel listViewModel)
        {
            return;
        }

        var point = e.GetCurrentPoint(card);
        if (!point.Properties.IsLeftButtonPressed || IsActionControl(e.Source, card))
        {
            return;
        }

        StopInertia();
        ResetReorderGesture(releaseCapture: true);
        pressedCard = card;
        pressedEntry = entry;
        pressedPointer = e.Pointer;
        pressPoint = e.GetPosition(this);
        dragTargetIndex = -1;
        e.Pointer.Capture(card);

        if (entry.Asset != null)
        {
            listViewModel.SelectedAsset = entry.Asset;
        }

        if (entry.CanReorder)
        {
            EnsureReorderHoldTimer();
            reorderHoldTimer?.Start();
        }
    }

    private void OnEntryPointerMoved(object? sender, PointerEventArgs e)
    {
        if (pressedCard == null || pressedEntry == null || pressedPointer != e.Pointer)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (!isDragging)
        {
            var distance = current - pressPoint;
            if (Math.Abs(distance.X) > HoldMoveTolerance ||
                Math.Abs(distance.Y) > HoldMoveTolerance)
            {
                reorderHoldTimer?.Stop();
            }
            return;
        }

        UpdateDragTarget(e);
        e.Handled = true;
    }

    private async void OnEntryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (pressedEntry == null || pressedPointer != e.Pointer)
            {
                return;
            }

            var entry = pressedEntry;
            var wasDragging = isDragging;
            var targetIndex = dragTargetIndex;
            ResetReorderGesture(releaseCapture: true);

            if (DataContext is not AssetListViewModel listViewModel)
            {
                return;
            }

            if (wasDragging)
            {
                if (targetIndex >= 0)
                {
                    await listViewModel.ReorderEntryAsync(entry, targetIndex);
                }
                e.Handled = true;
                return;
            }

            if (entry.Group != null)
            {
                listViewModel.OpenGroup(entry);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListView.OnEntryPointerReleased", ex);
            ResetReorderGesture(releaseCapture: true);
        }
    }

    private void OnEntryPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (pressedCard != null)
        {
            ResetReorderGesture(releaseCapture: false);
        }
    }

    private void OnEntryImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            control.DataContext is not AssetListEntryViewModel entry ||
            !entry.CanEditImage)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (entry.EditImageCommand.CanExecute(null))
        {
            try
            {
                entry.EditImageCommand.Execute(null);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AssetListView.OnEntryImagePointerPressed", ex);
            }
        }
    }

    private void OnToggleGmodDisabledCollapse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetListViewModel listViewModel)
        {
            return;
        }

        var command = (ICommand)listViewModel.ToggleGmodDisabledCollapseCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
        e.Handled = true;
    }

    private void OnToggleShareSelection(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AssetListEntryViewModel entry } &&
            DataContext is AssetListViewModel listViewModel)
        {
            listViewModel.ToggleShareSelection(entry);
        }
        e.Handled = true;
    }

    private void EnsureReorderHoldTimer()
    {
        if (reorderHoldTimer != null)
        {
            return;
        }

        reorderHoldTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = ReorderHoldDuration
        };
        reorderHoldTimer.Tick += OnReorderHoldElapsed;
    }

    private void OnReorderHoldElapsed(object? sender, EventArgs e)
    {
        reorderHoldTimer?.Stop();
        if (pressedCard == null || pressedEntry?.CanReorder != true)
        {
            return;
        }

        isDragging = true;
        pressedCard.Classes.Add("dragging");
        if (DataContext is AssetListViewModel listViewModel)
        {
            var reorderable = listViewModel.GetReorderableEntries();
            dragTargetIndex = IndexOfEntry(reorderable, pressedEntry);
            ShowInsertionMarkerForTarget(reorderable, dragTargetIndex, dragTargetIndex);
        }
    }

    private void UpdateDragTarget(PointerEventArgs e)
    {
        if (DataContext is not AssetListViewModel listViewModel ||
            pressedEntry == null ||
            assetScrollViewer == null)
        {
            return;
        }

        var reorderable = listViewModel.GetReorderableEntries();
        var currentIndex = IndexOfEntry(reorderable, pressedEntry);
        if (currentIndex < 0 || reorderable.Count == 0)
        {
            return;
        }

        var pointerPosition = e.GetPosition(assetScrollViewer);
        AutoScrollDuringDrag(pointerPosition.Y);
        var cards = GetVisibleReorderCards(assetScrollViewer, reorderable);
        var insertionSlot = 0;
        foreach (var card in cards)
        {
            if (pointerPosition.Y >= card.Top + (card.Height / 2))
            {
                insertionSlot++;
            }
        }

        var requestedTarget = insertionSlot;
        if (requestedTarget > currentIndex)
        {
            requestedTarget--;
        }
        requestedTarget = Math.Clamp(requestedTarget, 0, reorderable.Count - 1);
        dragTargetIndex = listViewModel.GetClampedReorderTargetIndex(
            pressedEntry,
            requestedTarget);
        ShowInsertionMarkerForTarget(reorderable, currentIndex, dragTargetIndex);
    }

    private void ShowInsertionMarkerForTarget(
        IReadOnlyList<AssetListEntryViewModel> reorderable,
        int currentIndex,
        int targetIndex)
    {
        if (insertionMarker == null || assetScrollViewer == null ||
            targetIndex < 0 || targetIndex >= reorderable.Count)
        {
            return;
        }

        var cards = GetVisibleReorderCards(assetScrollViewer, reorderable);
        var targetEntry = reorderable[targetIndex];
        var targetCard = cards.FirstOrDefault(card =>
            card.Entry != null &&
            card.Entry.EntryKind == targetEntry.EntryKind &&
            string.Equals(card.Entry.Id, targetEntry.Id, StringComparison.Ordinal));
        if (targetCard.Entry == null)
        {
            insertionMarker.IsVisible = false;
            return;
        }

        var markerY = targetIndex > currentIndex
            ? targetCard.Top + targetCard.Height
            : targetCard.Top;
        insertionMarker.RenderTransform = new TranslateTransform(0, Math.Max(0, markerY - 1));
        insertionMarker.IsVisible = true;
    }

    private List<VisibleReorderCard> GetVisibleReorderCards(
        ScrollViewer scrollViewer,
        IReadOnlyList<AssetListEntryViewModel> reorderable)
    {
        var allowed = new HashSet<string>(reorderable.Select(EntryKey), StringComparer.Ordinal);
        return this.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("assetEntryCard"))
            .Select(border => new
            {
                Border = border,
                Entry = border.DataContext as AssetListEntryViewModel,
                Position = border.TranslatePoint(new Point(0, 0), scrollViewer)
            })
            .Where(item => item.Entry != null &&
                           item.Position.HasValue &&
                           allowed.Contains(EntryKey(item.Entry)))
            .Select(item => new VisibleReorderCard(
                item.Entry!,
                item.Position!.Value.Y,
                item.Border.Bounds.Height))
            .OrderBy(item => item.Top)
            .ToList();
    }

    private void AutoScrollDuringDrag(double pointerY)
    {
        if (assetScrollViewer == null)
        {
            return;
        }

        const double edge = 34;
        const double step = 13;
        var delta = pointerY < edge
            ? -step
            : pointerY > assetScrollViewer.Viewport.Height - edge
                ? step
                : 0;
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        var maxOffset = Math.Max(
            0,
            assetScrollViewer.Extent.Height - assetScrollViewer.Viewport.Height);
        assetScrollViewer.Offset = new Vector(
            assetScrollViewer.Offset.X,
            Clamp(assetScrollViewer.Offset.Y + delta, 0, maxOffset));
    }

    private void ResetReorderGesture(bool releaseCapture)
    {
        reorderHoldTimer?.Stop();
        if (pressedCard != null)
        {
            pressedCard.Classes.Remove("dragging");
        }
        if (insertionMarker != null)
        {
            insertionMarker.IsVisible = false;
        }

        var pointer = pressedPointer;
        pressedCard = null;
        pressedEntry = null;
        pressedPointer = null;
        isDragging = false;
        dragTargetIndex = -1;
        if (releaseCapture)
        {
            pointer?.Capture(null);
        }
    }

    private static bool IsActionControl(object? source, Border card)
    {
        for (var current = source as Control;
             current != null && !ReferenceEquals(current, card);
             current = current.Parent as Control)
        {
            if (current is Button || current is ToggleButton || current is RadioButton)
            {
                return true;
            }
        }
        return false;
    }

    private static int IndexOfEntry(
        IReadOnlyList<AssetListEntryViewModel> entries,
        AssetListEntryViewModel target)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntryKind == target.EntryKind &&
                string.Equals(entries[index].Id, target.Id, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static string EntryKey(AssetListEntryViewModel entry)
    {
        return $"{(int)entry.EntryKind}:{entry.Id}";
    }

    private void OnAssetScrollWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var scrollViewer = assetScrollViewer;
        if (scrollViewer == null || isDragging)
        {
            return;
        }

        var deltaY = e.Delta.Y;
        if (Math.Abs(deltaY) < double.Epsilon)
        {
            return;
        }

        var deltaPixels = -deltaY * WheelPixels;
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextOffset = Clamp(scrollViewer.Offset.Y + deltaPixels, 0, maxOffset);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextOffset);
        scrollVelocity = (scrollVelocity * 0.35) + deltaPixels;
        scrollVelocity = Clamp(scrollVelocity, -MaxInertiaVelocity, MaxInertiaVelocity);
        StartInertia();
        e.Handled = true;
    }

    private void EnsureInertiaTimer()
    {
        if (inertiaTimer != null)
        {
            return;
        }

        inertiaTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        inertiaTimer.Tick += OnInertiaTick;
    }

    private void StartInertia()
    {
        EnsureInertiaTimer();
        if (inertiaTimer != null && !inertiaTimer.IsEnabled)
        {
            inertiaTimer.Start();
        }
    }

    private void StopInertia()
    {
        if (inertiaTimer != null && inertiaTimer.IsEnabled)
        {
            inertiaTimer.Stop();
        }
        scrollVelocity = 0;
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        var scrollViewer = assetScrollViewer;
        if (scrollViewer == null || Math.Abs(scrollVelocity) < InertiaStopThreshold)
        {
            StopInertia();
            return;
        }

        scrollVelocity *= InertiaFriction;
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextOffset = Clamp(scrollViewer.Offset.Y + scrollVelocity, 0, maxOffset);
        if (Math.Abs(nextOffset - scrollViewer.Offset.Y) < 0.1)
        {
            StopInertia();
            return;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextOffset);
    }

    private static double Clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }

    private readonly record struct VisibleReorderCard(
        AssetListEntryViewModel? Entry,
        double Top,
        double Height);
}
