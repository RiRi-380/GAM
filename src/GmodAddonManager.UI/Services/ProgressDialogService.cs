using Avalonia.Controls;
using GmodAddonManager.UI.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Services;

public sealed class ProgressDialogHandle : IDisposable
{
    private readonly ProgressDialog dialog;
    private readonly CancellationTokenSource showCts;
    private bool shown;
    private bool closed;

    internal ProgressDialogHandle(Window owner, string title, string? detail, int showDelayMs)
    {
        dialog = new ProgressDialog();
        dialog.UpdateStatus(title);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            dialog.UpdateDetail(detail);
        }
        showCts = new CancellationTokenSource();
        _ = ShowWithDelayAsync(owner, showDelayMs);
    }

    private async Task ShowWithDelayAsync(Window owner, int showDelayMs)
    {
        if (showDelayMs > 0)
        {
            try
            {
                await Task.Delay(showDelayMs, showCts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (showCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            shown = true;
            await dialog.ShowDialog(owner);
        }
        catch (ObjectDisposedException)
        {
            // Owner/dialog disposed during shutdown path.
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("ProgressDialogHandle.ShowWithDelayAsync", ex);
        }
    }

    public IProgress<(int current, int total)> CreateProgress()
    {
        return new Progress<(int current, int total)>(value =>
        {
            UpdateProgress(value.current, value.total);
        });
    }

    public void UpdateProgress(int current, int total)
    {
        dialog.UpdateProgress(current, total);
    }

    public void UpdateStatus(string status)
    {
        dialog.UpdateStatus(status);
    }

    public void UpdateDetail(string detail)
    {
        dialog.UpdateDetail(detail);
    }

    public void SetIndeterminate(string? detail = null)
    {
        dialog.SetIndeterminate(detail);
    }

    public void Close()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        showCts.Cancel();
        if (shown)
        {
            dialog.Close();
        }
    }

    public void Dispose()
    {
        Close();
        showCts.Dispose();
    }
}

public static class ProgressDialogService
{
    public static ProgressDialogHandle? Show(Window? owner, string title, string? detail = null, int showDelayMs = 150)
    {
        if (owner == null)
        {
            return null;
        }

        return new ProgressDialogHandle(owner, title, detail, showDelayMs);
    }
}
