using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views
{
    public partial class ShareCollectionDialog : Window
    {
        public string CollectionTitle { get; private set; } = string.Empty;
        public string CollectionDescription { get; private set; } = string.Empty;
        public bool OpenLinkAfterCreation { get; private set; } = true;
        public bool DialogResult { get; private set; }
        public int AddonCount { get; private set; }

        public ShareCollectionDialog()
        {
            InitializeComponent();
        }
        
        public void SetAddonCount(int count)
        {
            AddonCount = count;
            
            // 1000個以上の場合は分割情報を表示
            if (count > 1000)
            {
                SplitInfoPanel.IsVisible = true;
                int collectionCount = (count + 999) / 1000; // 切り上げ
                SplitCountText.Text = L.Format("ShareCollection.SplitCountFormat", collectionCount);
            }
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            var titleTextBox = this.FindControl<TextBox>("TitleTextBox");
            var descriptionTextBox = this.FindControl<TextBox>("DescriptionTextBox");
            var openLinkCheckBox = this.FindControl<CheckBox>("OpenLinkCheckBox");

            if (titleTextBox == null || descriptionTextBox == null || openLinkCheckBox == null)
                return;

            var title = titleTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                titleTextBox.Focus();
                return;
            }

            CollectionTitle = title;
            CollectionDescription = descriptionTextBox.Text?.Trim() ?? string.Empty;
            OpenLinkAfterCreation = openLinkCheckBox.IsChecked ?? true;
            DialogResult = true;
            
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        private async void OnInfoButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialogService = new DialogService();
                var collectionCount = (AddonCount + 999) / 1000;
                await dialogService.ShowInfoAsync(
                    L.Get("ShareCollection.SplitInfoTitle"),
                    L.Format("ShareCollection.SplitInfoMessage", AddonCount, collectionCount)
                );
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("ShareCollectionDialog.OnInfoButtonClick", ex);
            }
        }
    }
}
