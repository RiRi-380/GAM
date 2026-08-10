using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using GmodAddonManager.UI.Services;
using ReactiveUI;
using System;

namespace GmodAddonManager.UI.Controls
{
    public partial class AddonDetailsFloatingPanel : UserControl
    {
        private Action? _disposeSelectedAddonSubscription;
        private double _panelWidth = 400;
        private bool _isPanelOpen;

        public AddonDetailsFloatingPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        public void ApplyResponsiveLayout(ResponsiveLayoutState layout)
        {
            _panelWidth = layout.DetailsPaneWidth;
            FloatingPanel.Width = _panelWidth;

            if (!_isPanelOpen)
            {
                FloatingPanel.RenderTransform = CreateHorizontalTranslation(_panelWidth);
            }
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (FloatingPanel != null)
            {
                FloatingPanel.RenderTransform = CreateHorizontalTranslation(_panelWidth);
            }

            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                _disposeSelectedAddonSubscription?.Invoke();
                var subscription = vm.AddonGridViewModel.WhenAnyValue(x => x.SelectedAddon)
                    .Subscribe(addon =>
                    {
                        if (addon != null)
                        {
                            ShowPanel();
                        }
                        else
                        {
                            HidePanel();
                        }
                    });

                _disposeSelectedAddonSubscription = subscription.Dispose;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _disposeSelectedAddonSubscription?.Invoke();
            _disposeSelectedAddonSubscription = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void ShowPanel()
        {
            _isPanelOpen = true;
            if (FloatingPanel != null)
            {
                FloatingPanel.RenderTransform = CreateHorizontalTranslation(0);
            }

            if (BackgroundOverlay != null)
            {
                BackgroundOverlay.Opacity = 0;
                BackgroundOverlay.Opacity = 1;
            }
        }

        private void HidePanel()
        {
            _isPanelOpen = false;
            if (FloatingPanel != null)
            {
                FloatingPanel.RenderTransform = CreateHorizontalTranslation(_panelWidth);
            }

            if (BackgroundOverlay != null)
            {
                BackgroundOverlay.Opacity = 0;
            }
        }

        private static TransformOperations CreateHorizontalTranslation(double pixels) =>
            TransformOperations.Parse(
                FormattableString.Invariant($"translateX({pixels}px)"));

        private void OnBackgroundOverlayPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.SelectedAddon = null;
            }
        }

        private void OnPanelPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            // The overlay covers the previous card after a right-click. Route
            // only wheel gestures which the actual details ScrollViewer (or a
            // nested scroller) did not already consume, avoiding double scroll.
            if (_isPanelOpen && !e.Handled && DetailsControl.ScrollFromUnhandledWheel(e.Delta))
            {
                e.Handled = true;
            }
        }

        private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.SelectedAddon = null;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm)
                {
                    vm.AddonGridViewModel.SelectedAddon = null;
                }

                e.Handled = true;
            }
        }
    }
}
