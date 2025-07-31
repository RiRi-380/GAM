using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using System;
using ReactiveUI;

namespace GmodAddonManager.UI.Controls
{
    public partial class AddonDetailsFloatingPanel : UserControl
    {
        public AddonDetailsFloatingPanel()
        {
            InitializeComponent();
            
            // 初期状態では右側に隠す
            Loaded += OnLoaded;
        }
        
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // パネルを右側に配置
            if (FloatingPanel != null)
            {
                var transform = TransformOperations.Parse($"translateX(400px)");
                FloatingPanel.RenderTransform = transform;
            }
            
            // ViewModelの変更を監視
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.WhenAnyValue(x => x.SelectedAddon)
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
            }
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
            // 背景クリックでパネルを閉じる
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.SelectedAddon = null;
            }
        }
        
        private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
        {
            // 閉じるボタンクリックでパネルを閉じる
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.AddonGridViewModel.SelectedAddon = null;
            }
        }
        
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            // Escキーでパネルを閉じる
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