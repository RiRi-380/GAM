using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Threading;

namespace GmodAddonManager.UI.Views;

public partial class CollectionUploadProgressDialog : Window
{
    private CancellationTokenSource? _cancellationTokenSource;
    
    public CollectionUploadProgressDialog()
    {
        InitializeComponent();
    }
    
    public void SetCancellationTokenSource(CancellationTokenSource cts)
    {
        _cancellationTokenSource = cts;
    }
    
    public void UpdateStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = status;
        });
    }
    
    public void UpdateTotalProgress(int current, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalProgressPanel.IsVisible = total > 1000;
            if (total > 1000)
            {
                TotalProgressText.Text = $"全体: {current}/{total}";
                TotalProgressBar.Maximum = total;
                TotalProgressBar.Value = current;
            }
        });
    }
    
    public void UpdateBatchProgress(int currentBatch, int totalBatches, int itemsInBatch, int totalItemsInBatch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            BatchProgressText.Text = $"バッチ: {currentBatch}/{totalBatches}";
            BatchProgressBar.Maximum = totalItemsInBatch;
            BatchProgressBar.Value = itemsInBatch;
        });
    }
    
    public void UpdateDetail(string detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DetailText.Text = detail;
        });
    }
    
    public void ShowError(string error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            CancelButton.Content = "閉じる";
        });
    }
    
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (ErrorText.IsVisible)
        {
            Close();
        }
        else
        {
            _cancellationTokenSource?.Cancel();
            CancelButton.IsEnabled = false;
            UpdateStatus("キャンセル中...");
        }
    }
}