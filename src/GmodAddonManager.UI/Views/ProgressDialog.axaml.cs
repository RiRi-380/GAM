using Avalonia.Controls;
using Avalonia.Threading;
using GmodAddonManager.UI.Services;
using System;

namespace GmodAddonManager.UI.Views;

public partial class ProgressDialog : Window
{
    public ProgressDialog()
    {
        InitializeComponent();
        SetIndeterminate();
    }

    public void UpdateStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = status;
            Title = status;
        });
    }

    public void UpdateDetail(string detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DetailText.Text = detail ?? string.Empty;
            DetailText.IsVisible = !string.IsNullOrWhiteSpace(detail);
        });
    }

    public void UpdateProgress(int current, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (total <= 1)
            {
                SetIndeterminate();
                return;
            }

            var safeTotal = Math.Max(total, 1);
            var safeCurrent = Math.Min(Math.Max(current, 0), safeTotal);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = safeTotal;
            ProgressBar.Value = safeCurrent;
            ProgressText.IsVisible = true;

            var percent = (int)Math.Round((double)safeCurrent / safeTotal * 100);
            ProgressText.Text = L.Format("Busy.ProgressText", safeCurrent, safeTotal, percent);
        });
    }

    public void SetIndeterminate(string? detail = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.IsIndeterminate = true;
            ProgressText.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                DetailText.Text = detail;
                DetailText.IsVisible = true;
            }
        });
    }
}
