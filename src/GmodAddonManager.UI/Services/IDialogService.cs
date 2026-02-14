using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Services;

public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);
    Task ShowInfoAsync(string title, string message);
    Task ShowWarningAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message);
    Task ShowCreateAssetDialogAsync(Func<string, Task> onConfirm);
    Task ShowCreateAssetDialogAsync(Func<string, List<string>, Task> onConfirmWithAddons);
}