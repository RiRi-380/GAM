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
                SplitCountText.Text = $"({collectionCount}個のコレクションに分割されます)";
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
            var dialogService = new DialogService();
            await dialogService.ShowInfoAsync(
                "複数コレクションについて",
                "Steam Workshopの仕様により、1つのコレクションには最大1000個までしかアドオンを追加できません。\n\n" +
                $"あなたのアセットには{AddonCount}個のアドオンが含まれているため、{(AddonCount + 999) / 1000}個のコレクションに自動的に分割されます。\n\n" +
                "各コレクションは以下の名前で作成されます：\n" +
                "• 1つ目: [入力した名前] (1)\n" +
                "• 2つ目: [入力した名前] (2)\n" +
                "• 以降同様...\n\n" +
                "これはSteam Workshopの制限によるもので、避けることができません。"
            );
        }
    }
}