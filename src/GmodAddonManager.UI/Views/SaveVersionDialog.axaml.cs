using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Views
{
    public partial class SaveVersionDialog : Window, INotifyPropertyChanged
    {
        private string assetName = "";

        public string AssetName
        {
            get => assetName;
            set
            {
                if (assetName != value)
                {
                    assetName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ConfirmText));
                }
            }
        }

        public string ConfirmText => L.Format("SaveVersion.ConfirmFormat", AssetName);

        public bool IncludeAddonStates { get; private set; }
        public bool IsSaved { get; private set; }

        public SaveVersionDialog()
        {
            InitializeComponent();
            DataContext = this;
            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
            Closed += OnClosed;
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

        private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
            {
                OnPropertyChanged(nameof(ConfirmText));
            }
        }

        private void OnClosed(object? sender, System.EventArgs e)
        {
            LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        }

        public new event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
