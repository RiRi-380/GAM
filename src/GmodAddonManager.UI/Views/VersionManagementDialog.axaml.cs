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
            
            // CloseRequestedイベントを購読
            _viewModel.CloseRequested += (_, _) => Close();
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
            // 右クリックのチェック
            if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed)
            {
                // コンテキストメニューは自動的に表示される
            }
        }
    }
}