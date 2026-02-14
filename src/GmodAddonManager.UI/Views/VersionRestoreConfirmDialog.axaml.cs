using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System.Collections.Generic;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionRestoreConfirmDialog : Window
    {
        private readonly VersionRestoreConfirmViewModel? _viewModel;
        
        public bool Result { get; private set; }
        
        public VersionRestoreConfirmDialog()
        {
            InitializeComponent();
        }

        public VersionRestoreConfirmDialog(
            string confirmMessage,
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool showSubscribeInfo)
        {
            InitializeComponent();
            
            _viewModel = new VersionRestoreConfirmViewModel(
                confirmMessage,
                addonsToSubscribe,
                addonsToUnsubscribe,
                showSubscribeInfo);
            
            DataContext = _viewModel;
        }
        
        private void OnYesClick(object sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }
        
        private void OnNoClick(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }
        
        private async void OnDetailsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_viewModel == null)
                {
                    return;
                }

                if (!_viewModel.ShowSubscribeInfo)
                {
                    return;
                }

                var detailsDialog = new VersionRestoreDetailsDialog(
                    _viewModel.AddonsToSubscribe,
                    _viewModel.AddonsToUnsubscribe,
                    _viewModel.ShowSubscribeInfo);
                
                await detailsDialog.ShowDialog(this);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("VersionRestoreConfirmDialog.OnDetailsClick", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Release();
            base.OnClosed(e);
        }
        
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            // ウィンドウが閉じられた時、結果がtrueでなければfalseにする
            if (!Result)
            {
                Result = false;
            }
        }
    }
}
