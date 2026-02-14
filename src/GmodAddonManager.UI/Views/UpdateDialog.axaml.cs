using Avalonia.Controls;
using GmodAddonManager.UI.ViewModels;
using System;

namespace GmodAddonManager.UI.Views
{
    public partial class UpdateDialog : Window
    {
        public UpdateDialog()
        {
            InitializeComponent();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is UpdateDialogViewModel vm)
            {
                vm.Release();
            }

            base.OnClosed(e);
        }
    }
}
