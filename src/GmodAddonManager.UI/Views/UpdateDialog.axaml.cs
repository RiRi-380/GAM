using Avalonia.Controls;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Views
{
    public partial class UpdateDialog : Window
    {
        private UpdateDialogViewModel? subscribedViewModel;

        public UpdateDialog()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Closed += (_, _) => UnsubscribeViewModel();
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            UnsubscribeViewModel();

            if (DataContext is UpdateDialogViewModel viewModel)
            {
                subscribedViewModel = viewModel;
                subscribedViewModel.CloseRequested += OnCloseRequested;
            }
        }

        private void OnCloseRequested(object? sender, System.EventArgs e)
        {
            Close();
        }

        private void UnsubscribeViewModel()
        {
            if (subscribedViewModel != null)
            {
                subscribedViewModel.CloseRequested -= OnCloseRequested;
                subscribedViewModel = null;
            }
        }
    }
}
