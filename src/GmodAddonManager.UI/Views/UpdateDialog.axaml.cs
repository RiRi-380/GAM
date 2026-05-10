using Avalonia.Controls;
using GmodAddonManager.UI.ViewModels;
using System;

namespace GmodAddonManager.UI.Views
{
    public partial class UpdateDialog : Window
    {
        private UpdateDialogViewModel? viewModel;

        public UpdateDialog()
        {
            InitializeComponent();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (DataContext is UpdateDialogViewModel vm)
            {
                viewModel = vm;
                viewModel.CloseRequested += OnCloseRequested;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (viewModel != null)
            {
                viewModel.CloseRequested -= OnCloseRequested;
                viewModel.Release();
                viewModel = null;
            }
            else if (DataContext is UpdateDialogViewModel vm)
            {
                vm.Release();
            }

            base.OnClosed(e);
        }

        private void OnCloseRequested(object? sender, bool? result)
        {
            Close(result);
        }
    }
}
