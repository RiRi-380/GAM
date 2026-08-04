using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Views;

public partial class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan PaneCloseVisibilityDelay =
        TimeSpan.FromMilliseconds(120);

    private TextBox? _searchTextBox;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly KeyboardNavigationMode _assetPaneOpenTabNavigationMode;
    private IDisposable? _assetPaneHideTimer;
    private int _activationRefreshGeneration;
    private int _resourcesDisposed;
    private int _startupStarted;
    private bool _isClosed;
    private ResponsiveLayoutKind? _responsiveLayoutKind;
    
    public MainWindow()
    {
        InitializeComponent();
        _assetPaneOpenTabNavigationMode =
            KeyboardNavigation.GetTabNavigation(AssetPaneContent);
        Activated += OnWindowActivated;
        SizeChanged += OnWindowSizeChanged;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double viewportWidth)
    {
        var layout = ResponsiveLayoutPolicy.Resolve(viewportWidth);
        var transitionedFromWide = _responsiveLayoutKind == ResponsiveLayoutKind.Wide;

        AssetSplitView.DisplayMode = layout.UseOverlayPanes
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
        AssetSplitView.OpenPaneLength = layout.AssetPaneWidth;
        AssetPaneToggleButton.IsVisible = layout.UseOverlayPanes;
        AddonGridControl.ApplyResponsiveLayout(layout);
        AddonDetailsPanel.ApplyResponsiveLayout(layout);

        if (!layout.UseOverlayPanes)
        {
            AssetSplitView.IsPaneOpen = true;
        }
        else if (_responsiveLayoutKind is null || transitionedFromWide)
        {
            AssetSplitView.IsPaneOpen = false;
        }

        _responsiveLayoutKind = layout.Kind;
    }

    private void OnAssetPaneToggleClick(object? sender, RoutedEventArgs e)
    {
        AssetSplitView.IsPaneOpen = !AssetSplitView.IsPaneOpen;
    }

    private void OnAssetPaneOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AssetSplitView))
        {
            return;
        }

        RestoreAssetPaneContent();
    }

    private void OnAssetPaneClosed(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AssetSplitView))
        {
            return;
        }

        if (!IsOverlayMode(AssetSplitView.DisplayMode))
        {
            RestoreAssetPaneContent();
            return;
        }

        var returnFocusToToggle = AssetPaneContent.IsKeyboardFocusWithin;
        AssetPaneContent.IsEnabled = false;
        AssetPaneContent.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(
            AssetPaneContent,
            KeyboardNavigationMode.None);

        if (returnFocusToToggle && AssetPaneToggleButton.IsVisible)
        {
            AssetPaneToggleButton.Focus();
        }

        _assetPaneHideTimer?.Dispose();
        _assetPaneHideTimer = DispatcherTimer.RunOnce(
            () =>
            {
                _assetPaneHideTimer = null;
                if (!AssetSplitView.IsPaneOpen &&
                    IsOverlayMode(AssetSplitView.DisplayMode))
                {
                    AssetPaneContent.IsVisible = false;
                    if (ReferenceEquals(AssetSplitView.Pane, AssetPaneContent))
                    {
                        AssetSplitView.Pane = null;
                    }
                }
            },
            PaneCloseVisibilityDelay,
            DispatcherPriority.Normal);
    }

    private void RestoreAssetPaneContent()
    {
        _assetPaneHideTimer?.Dispose();
        _assetPaneHideTimer = null;
        AssetPaneContent.IsVisible = true;
        AssetPaneContent.IsEnabled = true;
        AssetPaneContent.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(
            AssetPaneContent,
            _assetPaneOpenTabNavigationMode);
        if (!ReferenceEquals(AssetSplitView.Pane, AssetPaneContent))
        {
            AssetSplitView.Pane = AssetPaneContent;
        }
    }

    private static bool IsOverlayMode(SplitViewDisplayMode displayMode) =>
        displayMode is SplitViewDisplayMode.Overlay or
            SplitViewDisplayMode.CompactOverlay;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        var generation = Interlocked.Increment(ref _activationRefreshGeneration);
        _ = RefreshActualStateAfterActivationAsync(generation);
    }

    private async Task RefreshActualStateAfterActivationAsync(int generation)
    {
        try
        {
            // Activation can fire repeatedly while Windows is still moving focus.
            // Only the latest event performs the read-only actual-state refresh.
            await Task.Delay(150);
            if (_isClosed ||
                generation != Volatile.Read(ref _activationRefreshGeneration))
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshActualStateFromRuntimeAsync();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "MainWindow.RefreshActualStateAfterActivationAsync",
                ex);
        }
    }
    
    protected override async void OnOpened(EventArgs e)
    {
        try
        {
            base.OnOpened(e);
            ApplyResponsiveLayout(Bounds.Width);

            if (Interlocked.Exchange(ref _startupStarted, 1) != 0)
            {
                return;
            }
            
            // Keep the existing window visible and responsive while the initial
            // Workshop inventory is prepared. Yielding at Render priority makes
            // sure the busy overlay gets a frame before filesystem work starts.
            if (DataContext is MainWindowViewModel viewModel)
            {
                var startupToken = _startupCancellation.Token;
                viewModel.StartStartupUpdateCheck();
                using (viewModel.BeginBusy(
                           L.Get("Busy.RefreshingAddons"),
                           L.Get("Busy.ScanningWorkshop")))
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        static () => { },
                        DispatcherPriority.Render);
                    startupToken.ThrowIfCancellationRequested();
                    await viewModel.InitializeAsync(startupToken);
                }
            }
            
            // 検索ボックスの参照を取得
            _searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            
            // 検索ボックスのLostFocusイベントをハンドリング
            if (_searchTextBox != null)
            {
                _searchTextBox.LostFocus += (s, args) =>
                {
                    // フォーカスが失われた時の処理
                    // 特に何もしなくても、自動的に青枠とカーソルが消える
                };
            }
        }
        catch (OperationCanceledException) when (_isClosed)
        {
            // Closing the window during the initial scan is an expected race.
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindow.OnOpened", ex);
        }
    }
    
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // Windowのどこかをクリックした時の処理
        this.AddHandler(PointerPressedEvent, (sender, args) =>
        {
            // クリックされた要素を取得
            var source = args.Source as Control;
            
            // 検索ボックスがフォーカスを持っていて、クリックされた要素が検索ボックスでない場合
            if (_searchTextBox != null && _searchTextBox.IsFocused && source != _searchTextBox)
            {
                // DockPanelにフォーカスを移す（これで検索ボックスのフォーカスが外れる）
                var dockPanel = this.FindControl<DockPanel>("MainDockPanel");
                if (dockPanel != null)
                {
                    dockPanel.Focus();
                }
            }
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        Dispose();
        Interlocked.Increment(ref _activationRefreshGeneration);
        Activated -= OnWindowActivated;
        SizeChanged -= OnWindowSizeChanged;
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _startupCancellation.Cancel();
        _startupCancellation.Dispose();
        _assetPaneHideTimer?.Dispose();
        _assetPaneHideTimer = null;
        GC.SuppressFinalize(this);
    }
}
