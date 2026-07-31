using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;

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

}
