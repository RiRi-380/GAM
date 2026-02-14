using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using ReactiveUI;
using System;

namespace GmodAddonManager.UI.Controls
{
    public partial class AddonDetailsFloatingPanel : UserControl
    {
        private Action? _disposeSelectedAddonSubscription;

        public AddonDetailsFloatingPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (FloatingPanel != null)
            {
                var transform = TransformOperations.Parse("translateX(400px)");
                FloatingPanel.RenderTransform = transform;
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
            if (FloatingPanel != null)
            {
                var transform = TransformOperations.Parse("translateX(0px)");
                FloatingPanel.RenderTransform = transform;
            }

            if (BackgroundOverlay != null)
            {
                BackgroundOverlay.Opacity = 0;
                BackgroundOverlay.Opacity = 1;
            }
        }

        private void HidePanel()
        {
            if (FloatingPanel != null)
            {
                var transform = TransformOperations.Parse("translateX(400px)");
                FloatingPanel.RenderTransform = transform;
            }

            if (BackgroundOverlay != null)
            {
                BackgroundOverlay.Opacity = 0;
            }
        }

        private void OnBackgroundOverlayPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.SelectedAddon = null;
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
