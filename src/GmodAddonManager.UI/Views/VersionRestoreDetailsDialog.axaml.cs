using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using System.Collections.Generic;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionRestoreDetailsDialog : Window
    {
        public VersionRestoreDetailsDialog(
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe)
        {
            InitializeComponent();
            
            var viewModel = new VersionRestoreDetailsViewModel(
                addonsToSubscribe,
                addonsToUnsubscribe);
            
            DataContext = viewModel;
        }
        
        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}