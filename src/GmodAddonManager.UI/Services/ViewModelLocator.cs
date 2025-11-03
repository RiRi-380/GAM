using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Services;

public static class ViewModelLocator
{
    public static AddonManager? AddonManager { get; set; }
    public static GmodProcessWatcher? ProcessWatcher { get; set; }
    public static PendingChangeManager? PendingChangeManager { get; set; }
    public static SteamWorkshopService? SteamWorkshopService { get; set; }
    public static AssetListViewModel? AssetListViewModel { get; set; }
    public static AddonGridViewModel? AddonGridViewModel { get; set; }
    public static IErrorHandler? ErrorHandler { get; set; }
    public static IDialogService? DialogService { get; set; }
    public static MainWindowViewModel? MainWindowViewModel { get; set; }
    public static HybridWorkshopService? HybridWorkshopService { get; set; }
}