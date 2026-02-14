using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Windows.Input;

namespace GmodAddonManager.UI.Views;

public partial class AssetListView : UserControl
{
    private const double WheelPixels = 60;
    private const double InertiaFriction = 0.88;
    private const double InertiaStopThreshold = 0.2;
    private const double MaxInertiaVelocity = 1800;

    private ScrollViewer? _assetScrollViewer;
    private DispatcherTimer? _inertiaTimer;
    private double _scrollVelocity;

    public AssetListView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _assetScrollViewer = this.FindControl<ScrollViewer>("AssetScrollViewer");
        if (_assetScrollViewer != null)
        {
            _assetScrollViewer.PointerWheelChanged += OnAssetScrollWheelChanged;
        }

        EnsureInertiaTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_assetScrollViewer != null)
        {
            _assetScrollViewer.PointerWheelChanged -= OnAssetScrollWheelChanged;
        }

        StopInertia();
        if (_inertiaTimer != null)
        {
            _inertiaTimer.Tick -= OnInertiaTick;
            _inertiaTimer = null;
        }

        _assetScrollViewer = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border &&
            border.DataContext is AssetItemViewModel assetVm &&
            DataContext is AssetListViewModel listVm)
        {
            listVm.SelectedAsset = assetVm;
        }
    }

    private void OnAssetImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (control.DataContext is not AssetItemViewModel assetVm)
        {
            return;
        }

        if (!assetVm.CanEditImage)
        {
            return;
        }

        var command = (ICommand)assetVm.EditCommand;
        if (command.CanExecute(null))
        {
            try
            {
                command.Execute(null);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AssetListView.OnAssetImagePointerPressed", ex);
            }
        }
    }

    private void OnAssetScrollWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var scrollViewer = _assetScrollViewer;
        if (scrollViewer == null)
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

        _scrollVelocity = (_scrollVelocity * 0.35) + deltaPixels;
        _scrollVelocity = Clamp(_scrollVelocity, -MaxInertiaVelocity, MaxInertiaVelocity);

        StartInertia();
        e.Handled = true;
    }

    private void EnsureInertiaTimer()
    {
        if (_inertiaTimer != null)
        {
            return;
        }

        _inertiaTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inertiaTimer.Tick += OnInertiaTick;
    }

    private void StartInertia()
    {
        EnsureInertiaTimer();
        if (_inertiaTimer != null && !_inertiaTimer.IsEnabled)
        {
            _inertiaTimer.Start();
        }
    }

    private void StopInertia()
    {
        if (_inertiaTimer != null && _inertiaTimer.IsEnabled)
        {
            _inertiaTimer.Stop();
        }
        _scrollVelocity = 0;
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        var scrollViewer = _assetScrollViewer;
        if (scrollViewer == null)
        {
            StopInertia();
            return;
        }

        if (Math.Abs(_scrollVelocity) < InertiaStopThreshold)
        {
            StopInertia();
            return;
        }

        _scrollVelocity *= InertiaFriction;

        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextOffset = Clamp(scrollViewer.Offset.Y + _scrollVelocity, 0, maxOffset);
        if (Math.Abs(nextOffset - scrollViewer.Offset.Y) < 0.1)
        {
            StopInertia();
            return;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextOffset);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
