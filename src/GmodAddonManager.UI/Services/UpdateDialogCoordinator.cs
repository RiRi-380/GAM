using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Services;

public static class UpdateDialogCoordinator
{
    private static int isDialogOpen;

    public static async Task<bool> TryShowAsync(
        Window owner,
        UpdateService updateService,
        UpdateInfo updateInfo)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (Interlocked.Exchange(ref isDialogOpen, 1) == 1)
        {
            return false;
        }

        try
        {
            var dialog = new UpdateDialog
            {
                DataContext = new UpdateDialogViewModel(updateService, updateInfo)
            };
            await dialog.ShowDialog(owner);
            return true;
        }
        finally
        {
            Volatile.Write(ref isDialogOpen, 0);
        }
    }
}
