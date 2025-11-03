using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views
{
    public partial class GamExportDialog : Window
    {
        public string CollectionTitle { get; private set; } = string.Empty;
        public string CollectionDescription { get; private set; } = string.Empty;
        public string SavePath { get; private set; } = string.Empty;
        public bool DialogResult { get; private set; }
        
        private List<string> _addonIds = new();
        
        public GamExportDialog()
        {
            InitializeComponent();
            
            // デフォルトの保存先をデスクトップに設定
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var defaultFileName = $"collection_{DateTime.Now:yyyyMMdd_HHmmss}.gam";
            SavePath = Path.Combine(desktopPath, defaultFileName);
            
            SavePathTextBox = this.FindControl<TextBox>("SavePathTextBox");
            if (SavePathTextBox != null)
            {
                SavePathTextBox.Text = SavePath;
            }
        }
        
        public void SetAddonIds(List<string> addonIds)
        {
            _addonIds = addonIds;
        }
        
        private async void OnBrowseClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;
            
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "GAMファイルの保存先を選択",
                DefaultExtension = "gam",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("GAM Collection File")
                    {
                        Patterns = new[] { "*.gam" }
                    }
                },
                SuggestedFileName = Path.GetFileName(SavePath)
            });
            
            if (file != null)
            {
                SavePath = file.Path.LocalPath;
                if (SavePathTextBox != null)
                {
                    SavePathTextBox.Text = SavePath;
                }
            }
        }
        
        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            var titleTextBox = this.FindControl<TextBox>("TitleTextBox");
            var descriptionTextBox = this.FindControl<TextBox>("DescriptionTextBox");
            
            if (titleTextBox == null || descriptionTextBox == null)
                return;
            
            var title = titleTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                titleTextBox.Focus();
                return;
            }
            
            CollectionTitle = title;
            CollectionDescription = descriptionTextBox.Text?.Trim() ?? string.Empty;
            
            try
            {
                // GAMファイルを作成
                var lines = new List<string>
                {
                    "# GAM Collection Export v1",
                    $"# Title: {CollectionTitle}",
                    $"# Description: {CollectionDescription}",
                    $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"# Count: {_addonIds.Count}",
                    ""
                };
                
                lines.AddRange(_addonIds);
                
                await File.WriteAllLinesAsync(SavePath, lines);
                
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                // エラー処理
                var messageBox = new Window
                {
                    Title = "エラー",
                    Width = 400,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBlock
                    {
                        Text = $"ファイルの保存に失敗しました:\n{ex.Message}",
                        Margin = new Thickness(20),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                
                await messageBox.ShowDialog(this);
            }
        }
        
        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}