using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using System.Collections.Generic;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionRestoreConfirmDialog : Window
    {
        private readonly VersionRestoreConfirmViewModel _viewModel;
        
        public bool Result { get; private set; }
        
        public VersionRestoreConfirmDialog(
            string confirmMessage,
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool isSteamworksAvailable)
        {
            InitializeComponent();
            
            _viewModel = new VersionRestoreConfirmViewModel(
                confirmMessage,
                addonsToSubscribe,
                addonsToUnsubscribe,
                isSteamworksAvailable);
            
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
            var detailsDialog = new VersionRestoreDetailsDialog(
                _viewModel.AddonsToSubscribe,
                _viewModel.AddonsToUnsubscribe);
            
            await detailsDialog.ShowDialog(this);
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