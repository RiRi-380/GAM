using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionRestoreDetailsDialog : Window
    {
        public VersionRestoreDetailsDialog()
        {
            InitializeComponent();
        }

        public VersionRestoreDetailsDialog(
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool showSubscribeInfo)
        {
            InitializeComponent();
            
            var viewModel = new VersionRestoreDetailsViewModel(
                addonsToSubscribe,
                addonsToUnsubscribe,
                showSubscribeInfo);
            
            DataContext = viewModel;
        }
        
        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is VersionRestoreDetailsViewModel viewModel)
            {
                viewModel.Release();
            }

            base.OnClosed(e);
        }
    }
}
