using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GmodAddonManager.UI.Views
{
    public partial class SaveVersionDialog : Window
    {
        public string AssetName { get; set; } = "";
        public bool IncludeAddonStates { get; private set; }
        public bool IsSaved { get; private set; }

        public SaveVersionDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            IncludeAddonStates = IncludeAddonStatesCheckBox.IsChecked ?? true;
            IsSaved = true;
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            IsSaved = false;
            Close();
        }
    }
}