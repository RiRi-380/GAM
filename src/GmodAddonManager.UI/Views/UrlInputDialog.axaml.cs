using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using GmodAddonManager.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class UrlInputDialog : Window
{
    private List<string> validUrls = new List<string>();

    public UrlInputDialog()
    {
        InitializeComponent();
    }

    private bool ValidateUrls()
    {
        validUrls.Clear();
        var text = UrlTextBox.Text;
        
        if (string.IsNullOrWhiteSpace(text))
        {
            ValidationText.Text = "URLを入力してください。";
            ValidationText.Foreground = Brushes.Orange;
            return false;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var invalidUrls = new List<string>();
        var workshopIds = new HashSet<string>();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;

            var workshopId = SteamUrlParser.ExtractWorkshopId(trimmedLine);
            if (!string.IsNullOrEmpty(workshopId))
            {
                if (workshopIds.Add(workshopId))
                {
                    validUrls.Add(trimmedLine);
                }
            }
            else
            {
                invalidUrls.Add(trimmedLine);
            }
        }

        if (invalidUrls.Count > 0)
        {
            ValidationText.Text = $"無効なURL: {string.Join(", ", invalidUrls.Take(3))}" +
                                (invalidUrls.Count > 3 ? $" 他{invalidUrls.Count - 3}個" : "");
            ValidationText.Foreground = Brushes.Red;
            return false;
        }
        else if (validUrls.Count > 0)
        {
            ValidationText.Text = $"{validUrls.Count}個の有効なURLが見つかりました。";
            ValidationText.Foreground = Brushes.Green;
            return true;
        }
        else
        {
            ValidationText.Text = "有効なURLが見つかりませんでした。";
            ValidationText.Foreground = Brushes.Orange;
            return false;
        }
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ValidateUrls() && validUrls.Count > 0)
        {
            Close(validUrls);
        }
        else
        {
            // エラーの場合は少し待ってから元に戻す
            await Task.Delay(2000);
            ValidationText.Text = "";
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}