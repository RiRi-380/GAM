using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using System;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionManagementDialog : Window
    {
        private readonly VersionManagementViewModel _viewModel = null!;
        
        public VersionManagementDialog()
        {
            InitializeComponent();
        }
        
        public VersionManagementDialog(Asset asset, AddonManager addonManager) : this()
        {
            _viewModel = new VersionManagementViewModel(asset, addonManager);
            DataContext = _viewModel;
            
            // CloseRequested繧､繝吶Φ繝医ｒ雉ｼ隱ｭ
            _viewModel.CloseRequested += OnViewModelCloseRequested;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.CloseRequested -= OnViewModelCloseRequested;
                _viewModel.Release();
            }
            base.OnClosed(e);
        }
        private void OnViewModelCloseRequested(object? sender, EventArgs e)
        {
            Close();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void OnVersionItemPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is VersionItemViewModel version)
            {
                _viewModel.SelectedVersion = version;
            }
        }
        
        private void OnAddonPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // 蜿ｳ繧ｯ繝ｪ繝・け縺ｮ繝√ぉ繝・け
            if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed)
            {
                // 繧ｳ繝ｳ繝・く繧ｹ繝医Γ繝九Η繝ｼ縺ｯ閾ｪ蜍慕噪縺ｫ陦ｨ遉ｺ縺輔ｌ繧・
            }
        }
    }
}