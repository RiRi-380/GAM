using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Views;

public partial class MainWindow
{
    private const double AddonDragGroupHoverMilliseconds = 1000;
    private const double AddonDragBreadcrumbHoverMilliseconds = 600;
    private readonly DispatcherTimer _addonDragTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(25)
    };

    private string[]? _draggedAddonIds;
    private Point _addonDragPosition;
    private Point? _lastAddonDragNavigationPosition;
    private Control? _addonDragTarget;
    private long _addonDragTargetSince;

    internal bool BeginAddonDrag(string[] addonIds, Point position)
    {
        if (addonIds.Length == 0 ||
            DataContext is not MainWindowViewModel { IsBusy: false } viewModel ||
            viewModel.AssetListViewModel.IsShareMode)
        {
            return false;
        }

        _draggedAddonIds = addonIds;
        if (!AssetSplitView.IsPaneOpen)
        {
            // Give a compact-window drop a fixed hit area as soon as the pane opens.
            AssetSplitView.Classes.Add("addonDragOpening");
            AssetSplitView.IsPaneOpen = true;
        }
        AssetPaneContent.StopInertia();
        AddonDragBadge.IsVisible = true;
        UpdateAddonDrag(position);
        _addonDragTimer.Start();
        return true;
    }

    internal void UpdateAddonDrag(Point position)
    {
        _addonDragPosition = position;
        RefreshAddonDragFeedback();
    }

    private void OnAddonDragTick(object? sender, EventArgs e)
    {
        try
        {
            RefreshAddonDragFeedback(scrollAtEdge: true);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindow.OnAddonDragTick", ex);
            AddonGridControl.CancelAddonGesture();
        }
    }

    private void RefreshAddonDragFeedback(bool scrollAtEdge = false)
    {
        if (_draggedAddonIds is not { } ids)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel { IsBusy: false } viewModel ||
            viewModel.AssetListViewModel.IsShareMode)
        {
            AddonGridControl.CancelAddonGesture();
            return;
        }

        Control? target = null;
        if (IsAddonDragRearmed(_addonDragPosition))
        {
            // Edge scrolling belongs to the pane viewport, including the gaps between cards.
            var scrolled = scrollAtEdge && AssetSplitView.IsPaneOpen &&
                AssetPaneContent.IsEffectivelyVisible &&
                this.TranslatePoint(_addonDragPosition, AssetPaneContent) is { } panePosition &&
                AssetPaneContent.ScrollForAddonDrop(panePosition);
            if (!scrolled && FindAddonDragPaneHit(_addonDragPosition) is { } hit)
            {
                target = FindAddonDragTarget(hit);
            }
        }

        if (!ReferenceEquals(target, _addonDragTarget))
        {
            _addonDragTarget = target;
            _addonDragTargetSince = Stopwatch.GetTimestamp();
        }

        var message = L.Format("AddonDrop.Count", ids.Length);
        var hoverProgress = 0d;
        if (target?.DataContext is AssetListEntryViewModel { Asset: { } asset })
        {
            message = L.Format("AddonDrop.Destination", asset.Name, ids.Length);
        }
        else if (target != null)
        {
            var (groupId, name) = target.DataContext switch
            {
                AssetListEntryViewModel group => (group.Id, group.Name),
                AssetBreadcrumbItemViewModel crumb => (crumb.TargetGroupId, crumb.Name),
                _ => throw new InvalidOperationException("Invalid add-on drag navigation target.")
            };
            message = L.Format("AddonDrop.Navigate", name, ids.Length);
            var hoverDelay = target.DataContext is AssetBreadcrumbItemViewModel
                ? AddonDragBreadcrumbHoverMilliseconds
                : AddonDragGroupHoverMilliseconds;
            hoverProgress = Stopwatch.GetElapsedTime(_addonDragTargetSince).TotalMilliseconds / hoverDelay;
            if (hoverProgress >= 1)
            {
                _lastAddonDragNavigationPosition = _addonDragPosition;
                _addonDragTarget = null;
                viewModel.AssetListViewModel.BrowseAddonDropDestinations(groupId);
                target = null;
                hoverProgress = 0;
                message = L.Format("AddonDrop.Count", ids.Length);
            }
        }

        ShowAddonDragFeedback(target, message, hoverProgress);
    }

    private Control? FindAddonDragPaneHit(Point position)
    {
        if (this.InputHitTest(position) is not Control hit ||
            !hit.GetSelfAndVisualAncestors().Contains(AssetPaneContent))
        {
            return null;
        }

        return hit;
    }

    private static Control? FindAddonDragTarget(Control hit) =>
        hit.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(control => control switch
        {
            Border card when card.Classes.Contains("assetEntryCard") =>
                card.DataContext is AssetListEntryViewModel
                {
                    IsGroup: true
                } or AssetListEntryViewModel { Asset: { IsSystem: false, IsSmart: false } },
            Button { DataContext: AssetBreadcrumbItemViewModel { IsCurrent: false } } => true,
            _ => false
        });

    private bool IsAddonDragRearmed(Point position)
    {
        if (_lastAddonDragNavigationPosition is not { } navigationPosition)
        {
            return true;
        }

        var delta = position - navigationPosition;
        if (Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 3)
        {
            return false;
        }

        _lastAddonDragNavigationPosition = null;
        AssetSplitView.Classes.Remove("addonDragOpening");
        return true;
    }

    private void ShowAddonDragFeedback(Control? target, string message, double hoverProgress)
    {
        AddonDragText.Text = message;
        AddonDragHoverProgress.IsVisible = hoverProgress > 0;
        AddonDragHoverProgress.Value = hoverProgress;
        AddonDragBadge.Measure(new Size(300, double.PositiveInfinity));
        if (this.TranslatePoint(_addonDragPosition, AddonDragOverlay) is { } position)
        {
            var badgeSize = AddonDragBadge.DesiredSize;
            var overlaySize = AddonDragOverlay.Bounds.Size;
            Canvas.SetLeft(AddonDragBadge,
                Math.Clamp(position.X + 16, 0, Math.Max(0, overlaySize.Width - badgeSize.Width)));
            Canvas.SetTop(AddonDragBadge,
                Math.Clamp(position.Y + 18, 0, Math.Max(0, overlaySize.Height - badgeSize.Height)));
        }

        if (target?.TranslatePoint(default, AddonDragOverlay) is not { } origin)
        {
            AddonDropOutline.IsVisible = false;
            return;
        }

        var bounds = new Rect(origin, target.Bounds.Size);
        var viewport = target.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (viewport?.TranslatePoint(default, AddonDragOverlay) is { } viewportOrigin)
        {
            bounds = bounds.Intersect(new Rect(viewportOrigin, viewport.Bounds.Size));
        }

        AddonDropOutline.Width = bounds.Width;
        AddonDropOutline.Height = bounds.Height;
        Canvas.SetLeft(AddonDropOutline, bounds.X);
        Canvas.SetTop(AddonDropOutline, bounds.Y);
        AddonDropOutline.IsVisible = bounds.Width > 0 && bounds.Height > 0;
    }

    internal async Task CompleteAddonDragAsync(Point position)
    {
        var ids = _draggedAddonIds;
        var destination = ids != null && IsAddonDragRearmed(position) &&
                          FindAddonDragPaneHit(position) is { } hit
            ? (FindAddonDragTarget(hit)?.DataContext as AssetListEntryViewModel)?.Asset
            : null;
        ClearAddonDrag();
        if (ids != null && destination != null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.AddDroppedAddonsToAssetAsync(destination, ids);
        }
    }

    internal void ClearAddonDrag()
    {
        _addonDragTimer.Stop();
        _draggedAddonIds = null;
        _addonDragTarget = null;
        _lastAddonDragNavigationPosition = null;
        AddonDragBadge.IsVisible = false;
        AddonDropOutline.IsVisible = false;
    }

    private void OnAddonDragKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsUpdatingAssetMembership: true })
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _draggedAddonIds != null)
        {
            AddonGridControl.CancelAddonGesture();
            e.Handled = true;
        }
    }

    private void OnAddonDragWindowDeactivated(object? sender, EventArgs e) =>
        AddonGridControl.CancelAddonGesture();
}
