using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Services;

public class DialogService : IDialogService
{
    private Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            // メッセージの行数に基づいて高さを計算
            var lineCount = message.Split('\n').Length;
            var estimatedHeight = Math.Max(200, 120 + (lineCount * 20));
            
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = Math.Min(estimatedHeight, 500),
                MinHeight = 200,
                MaxHeight = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var mainPanel = new DockPanel
            {
                LastChildFill = true
            };

            var button = new Button
            {
                Content = L.Get("Dialog.OK"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 80,
                Margin = new Avalonia.Thickness(0, 10, 0, 20),
                IsDefault = true
            };
            button.Click += (s, e) => dialog.Close();

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Avalonia.Thickness(20, 20, 20, 10)
            };

            scrollViewer.Content = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 400
            };

            DockPanel.SetDock(button, Dock.Bottom);
            mainPanel.Children.Add(button);
            mainPanel.Children.Add(scrollViewer);

            dialog.Content = mainPanel;
            await dialog.ShowDialog(mainWindow);
        }
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        await ShowErrorAsync(title, message); // 簡略化のため同じ実装
    }

    public async Task ShowWarningAsync(string title, string message)
    {
        await ShowErrorAsync(title, message); // 簡略化のため同じ実装
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        await ShowInfoAsync(title, message);
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            // メッセージの行数に基づいて高さを計算
            var lineCount = message.Split('\n').Length;
            var estimatedHeight = Math.Max(250, 150 + (lineCount * 20)); // 最小250px、1行あたり20px追加
            
            var dialog = new Window
            {
                Title = title,
                Width = 500,
                Height = Math.Min(estimatedHeight, 600), // 最大600pxに制限
                MinHeight = 250,
                MaxHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var result = false;

            // メインパネル
            var mainPanel = new DockPanel
            {
                LastChildFill = true
            };

            // ボタンパネル（下部に固定）
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 10,
                Margin = new Avalonia.Thickness(0, 10, 0, 20)
            };

            var yesButton = new Button
            {
                Width = 80,
                IsDefault = true
            };
            yesButton.Content = L.Get("Dialog.Yes");
            yesButton.Click += (s, e) => 
            {
                result = true;
                dialog.Close();
            };

            var noButton = new Button
            {
                Width = 80,
                IsCancel = true
            };
            noButton.Content = L.Get("Dialog.No");
            noButton.Click += (s, e) => 
            {
                result = false; // 明示的にfalseを設定
                dialog.Close();
            };

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);

            // ScrollViewerでメッセージをラップ
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Avalonia.Thickness(20, 20, 20, 10)
            };

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 450
            };

            scrollViewer.Content = messageBlock;

            // レイアウト構築
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            mainPanel.Children.Add(buttonPanel);
            mainPanel.Children.Add(scrollViewer);

            dialog.Content = mainPanel;
            
            // ウィンドウが閉じられた時のイベントハンドラを追加
            dialog.Closing += (s, e) =>
            {
                // resultがtrueでない場合は、必ずfalseにする（念のため）
                if (!result)
                {
                    result = false;
                }
            };
            
            await dialog.ShowDialog(mainWindow);
            return result;
        }
        return false;
    }

    public async Task ShowCreateAssetDialogAsync(Func<string, Task> onConfirm)
    {
        // コレクションなしのオーバーロードを呼び出し
        await ShowCreateAssetDialogAsync(async (name, addonIds) =>
        {
            // addonIdsが空の場合は通常のアセット作成
            await onConfirm(name);
        });
    }
    
    public async Task ShowCreateAssetDialogAsync(Func<string, List<string>, Task> onConfirmWithAddons)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            // 元のCollectionImportDialogを使用
            var dialog = new CollectionImportDialog(onConfirmWithAddons);
            await dialog.ShowDialog(mainWindow);
            return;
            
            /*
            // 旧実装
            var dialog = new Window
            {
                Width = 400,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };
            dialog.Title = "新しいアセットを作成";

            var panel = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10
            };

            var promptText = new TextBlock();
            promptText.Text = "アセット名を入力してください：";
            panel.Children.Add(promptText);

            var textBox = new TextBox
            {
                Text = ""
            };
            panel.Children.Add(textBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 10
            };

            var okButton = new Button
            {
                Width = 80
            };
            okButton.Content = "作成";
            okButton.Click += async (s, e) => 
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    dialog.Close();
                    await onConfirm(textBox.Text);
                }
            };

            var cancelButton = new Button
            {
                Width = 80
            };
            cancelButton.Content = "キャンセル";
            cancelButton.Click += (s, e) => dialog.Close();

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            await dialog.ShowDialog(mainWindow);
            */
        }
    }
    
    public async Task<(string? title, string? description, bool openLink)> ShowShareCollectionDialogAsync()
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            var dialog = new ShareCollectionDialog();
            await dialog.ShowDialog(mainWindow);
            
            if (dialog.DialogResult)
            {
                return (dialog.CollectionTitle, dialog.CollectionDescription, dialog.OpenLinkAfterCreation);
            }
        }
        
        return (null, null, false);
    }
    
    public async Task<int> ShowSelectionAsync(string title, string message, string[] options)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var result = -1;

            var panel = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10
            };

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            panel.Children.Add(messageBlock);

            var buttonPanel = new StackPanel
            {
                Spacing = 5,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };

            for (int i = 0; i < options.Length; i++)
            {
                var index = i;
                var button = new Button
                {
                    Content = options[i],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Avalonia.Thickness(10, 5)
                };
                button.Click += (s, e) =>
                {
                    result = index;
                    dialog.Close();
                };
                buttonPanel.Children.Add(button);
            }

            panel.Children.Add(buttonPanel);

            var cancelButton = new Button
            {
                Content = L.Get("Dialog.Cancel"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 80,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };
            cancelButton.Click += (s, e) => dialog.Close();
            panel.Children.Add(cancelButton);

            dialog.Content = panel;
            await dialog.ShowDialog(mainWindow);
            return result;
        }
        return -1;
    }
    
    public async Task<bool> ShowVersionRestoreConfirmAsync(
        string confirmMessage,
        List<string> addonsToSubscribe,
        List<string> addonsToUnsubscribe,
        bool showSubscribeInfo)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            var dialog = new VersionRestoreConfirmDialog(
                confirmMessage,
                addonsToSubscribe,
                addonsToUnsubscribe,
                showSubscribeInfo);
            
            await dialog.ShowDialog(mainWindow);
            return dialog.Result;
        }
        return false;
    }
}
