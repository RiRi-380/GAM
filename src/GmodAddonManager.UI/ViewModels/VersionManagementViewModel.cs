using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels;

/// <summary>
/// Presents membership-only snapshots for one custom Asset.
/// All mutations go through <see cref="AddonManager"/> so version numbering,
/// persistence, Undo, and runtime reconciliation stay owned by Core.
/// </summary>
public sealed class VersionManagementViewModel : ViewModelBase
{
    private readonly AddonManager addonManager;
    private readonly ObservableCollection<VersionItemViewModel> versions = new();
    private readonly ObservableCollection<VersionAddonItemViewModel> selectedVersionAddons = new();
    private readonly Dictionary<string, AddonItemViewModel> addonViewModelCache =
        new(StringComparer.Ordinal);
    private Asset asset;
    private VersionItemViewModel? selectedVersion;
    private bool isNewestFirst = true;
    private bool showDiff = true;
    private bool disposed;
    private int addonLoadGeneration;

    public VersionManagementViewModel(Asset asset, AddonManager addonManager)
    {
        this.asset = asset ?? throw new ArgumentNullException(nameof(asset));
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));

        CreateNewVersionCommand = ReactiveCommand.CreateFromTask(CreateNewVersionAsync);
        RestoreSelectedVersionCommand = ReactiveCommand.CreateFromTask(
            RestoreSelectedVersionAsync,
            this.WhenAnyValue(viewModel => viewModel.SelectedVersion)
                .Select(version => version?.CanRestore == true));
        DeleteVersionCommand =
            ReactiveCommand.CreateFromTask<VersionItemViewModel>(DeleteVersionAsync);
        ClearVersionHistoryCommand =
            ReactiveCommand.CreateFromTask(ClearVersionHistoryAsync);

        LoadVersions();
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public string AssetName => GetAssetDisplayName();

    public string AssetTitle => L.Format("VersionManagement.AssetTitleFormat", AssetName);

    public bool IsNewestFirst
    {
        get => isNewestFirst;
        set
        {
            this.RaiseAndSetIfChanged(ref isNewestFirst, value);
            this.RaisePropertyChanged(nameof(IsOldestFirst));
            this.RaisePropertyChanged(nameof(SortedVersions));
        }
    }

    public bool IsOldestFirst
    {
        get => !isNewestFirst;
        set
        {
            if (value == isNewestFirst)
            {
                IsNewestFirst = !value;
            }
        }
    }

    public bool ShowDiff
    {
        get => showDiff;
        set
        {
            this.RaiseAndSetIfChanged(ref showDiff, value);
            if (SelectedVersion != null)
            {
                _ = LoadSelectedVersionAddonsAsync(SelectedVersion);
            }
        }
    }

    public ObservableCollection<VersionItemViewModel> Versions => versions;

    public IEnumerable<VersionItemViewModel> SortedVersions =>
        IsNewestFirst
            ? versions.OrderByDescending(version => version.Version)
            : versions.OrderBy(version => version.Version);

    public VersionItemViewModel? SelectedVersion
    {
        get => selectedVersion;
        set
        {
            if (ReferenceEquals(selectedVersion, value))
            {
                return;
            }

            if (selectedVersion != null)
            {
                selectedVersion.IsSelected = false;
            }

            this.RaiseAndSetIfChanged(ref selectedVersion, value);

            if (value != null)
            {
                value.IsSelected = true;
                _ = LoadSelectedVersionAddonsAsync(value);
            }
            else
            {
                selectedVersionAddons.Clear();
            }

            this.RaisePropertyChanged(nameof(SelectedVersionTitle));
            this.RaisePropertyChanged(nameof(CanRestore));
        }
    }

    public ObservableCollection<VersionAddonItemViewModel> SelectedVersionAddons =>
        selectedVersionAddons;

    public string SelectedVersionTitle =>
        SelectedVersion != null
            ? L.Format(
                "VersionManagement.SelectedVersionTitleFormat",
                SelectedVersion.VersionDisplay,
                SelectedVersion.CreatedAtDisplay)
            : L.Get("VersionManagement.SelectVersionPrompt");

    public bool CanRestore => SelectedVersion?.CanRestore == true;

    public ReactiveCommand<Unit, Unit> CreateNewVersionCommand { get; }

    public ReactiveCommand<Unit, Unit> RestoreSelectedVersionCommand { get; }

    public ReactiveCommand<VersionItemViewModel, Unit> DeleteVersionCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearVersionHistoryCommand { get; }

    private Asset ResolveCurrentAsset()
    {
        var latest = addonManager.GetConfiguration().Assets
            .FirstOrDefault(candidate => candidate.Id == asset.Id);
        if (latest != null)
        {
            asset = latest;
        }

        return asset;
    }

    private void LoadVersions(int? preferredVersion = null)
    {
        var currentAsset = ResolveCurrentAsset();
        var previousSelection = preferredVersion ?? SelectedVersion?.Version;

        SelectedVersion = null;
        versions.Clear();

        foreach (var snapshot in currentAsset.VersionHistory
                     .Where(snapshot => snapshot != null)
                     .OrderBy(snapshot => snapshot.Version))
        {
            var isCurrent = snapshot.Version == currentAsset.CurrentVersion;
            versions.Add(new VersionItemViewModel
            {
                Version = snapshot.Version,
                CreatedAt = snapshot.CreatedAt,
                AddonCount = NormalizeAddonIds(snapshot.AddonIds).Count,
                IsCurrent = isCurrent,
                HasMembershipChanges =
                    isCurrent &&
                    addonManager.AssetVersionHasMembershipChanges(
                        currentAsset.Id,
                        snapshot.Version)
            });
        }

        this.RaisePropertyChanged(nameof(SortedVersions));

        SelectedVersion =
            versions.FirstOrDefault(version => version.Version == previousSelection) ??
            versions.OrderByDescending(version => version.Version).FirstOrDefault();
    }

    private async Task CreateNewVersionAsync()
    {
        var dialogService = new DialogService();
        try
        {
            var currentAsset = ResolveCurrentAsset();
            var maximumVersion = currentAsset.VersionHistory.Count == 0
                ? 0
                : Math.Max(
                    0,
                    currentAsset.VersionHistory.Max(version => version.Version));
            var nextVersion = checked(maximumVersion + 1);
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("VersionManagement.CreateConfirmTitle"),
                L.Format("VersionManagement.CreateConfirmMessage", nextVersion));
            if (!confirmed)
            {
                return;
            }

            var snapshot =
                await addonManager.CreateAssetVersionAsync(currentAsset.Id);
            LoadVersions(snapshot.Version);
            RefreshAssetList();

            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                L.Format("VersionManagement.CreateCompleteMessage", snapshot.Version));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "VersionManagementViewModel.CreateNewVersionAsync",
                ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("VersionManagement.CreateFailed"));
        }
    }

    private async Task RestoreSelectedVersionAsync()
    {
        if (SelectedVersion == null)
        {
            return;
        }

        await RestoreVersionAsync(SelectedVersion);
    }

    private async Task RestoreVersionAsync(VersionItemViewModel version)
    {
        var dialogService = new DialogService();
        try
        {
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("VersionManagement.Title"),
                L.Format(
                    "VersionManagement.RestoreVersionConfirm",
                    version.Version));
            if (!confirmed)
            {
                return;
            }

            var restored = await addonManager.RestoreAssetVersionAsync(
                ResolveCurrentAsset().Id,
                version.Version);
            if (!restored)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.VersionNotFound"));
                return;
            }

            LoadVersions(version.Version);
            await RefreshMainWindowAsync();

            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                L.Format(
                    "VersionManagement.RestoreCompleteMessage",
                    version.Version));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "VersionManagementViewModel.RestoreVersionAsync",
                ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("VersionManagement.RestoreFailed"));
        }
    }

    private async Task DeleteVersionAsync(VersionItemViewModel version)
    {
        var dialogService = new DialogService();
        try
        {
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("VersionManagement.DeleteConfirmTitle"),
                L.Format(
                    "VersionManagement.DeleteConfirmMessage",
                    version.Version));
            if (!confirmed)
            {
                return;
            }

            var deleted = await addonManager.DeleteAssetVersionAsync(
                ResolveCurrentAsset().Id,
                version.Version);
            if (!deleted)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.VersionNotFound"));
                return;
            }

            LoadVersions();
            RefreshAssetList();
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                L.Format(
                    "VersionManagement.DeleteCompleteMessage",
                    version.Version));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "VersionManagementViewModel.DeleteVersionAsync",
                ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("VersionManagement.DeleteFailed"));
        }
    }

    private async Task ClearVersionHistoryAsync()
    {
        if (ResolveCurrentAsset().VersionHistory.Count == 0)
        {
            return;
        }

        var dialogService = new DialogService();
        try
        {
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("VersionManagement.ClearHistoryConfirmTitle"),
                L.Get("VersionManagement.ClearHistoryConfirmMessage"));
            if (!confirmed)
            {
                return;
            }

            await addonManager.ClearAssetVersionHistoryAsync(
                ResolveCurrentAsset().Id);
            LoadVersions();
            RefreshAssetList();

            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                L.Get("VersionManagement.ClearHistoryCompleteMessage"));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "VersionManagementViewModel.ClearVersionHistoryAsync",
                ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("VersionManagement.ClearHistoryFailed"));
        }
    }

    private Task LoadSelectedVersionAddonsAsync(VersionItemViewModel version)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        var generation = ++addonLoadGeneration;
        var currentAsset = ResolveCurrentAsset();
        var snapshot = currentAsset.VersionHistory
            .FirstOrDefault(candidate => candidate.Version == version.Version);
        if (snapshot == null)
        {
            selectedVersionAddons.Clear();
            return Task.CompletedTask;
        }

        var selectedIds = NormalizeAddonIds(snapshot.AddonIds);
        var selectedSet = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        var previousSnapshot = currentAsset.VersionHistory
            .Where(candidate => candidate.Version < snapshot.Version)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        var previousIds = previousSnapshot == null
            ? new List<string>()
            : NormalizeAddonIds(previousSnapshot.AddonIds);
        var previousSet = new HashSet<string>(previousIds, StringComparer.Ordinal);

        IEnumerable<string> displayIds = selectedIds;
        if (ShowDiff)
        {
            displayIds = selectedIds
                .Concat(previousIds)
                .Distinct(StringComparer.Ordinal);
        }

        var metadata = addonManager.GetAllAddons();
        var items = displayIds
            .Select(addonId =>
            {
                var status = AddonDiffStatus.Unchanged;
                if (ShowDiff)
                {
                    if (!previousSet.Contains(addonId))
                    {
                        status = AddonDiffStatus.Added;
                    }
                    else if (!selectedSet.Contains(addonId))
                    {
                        status = AddonDiffStatus.Removed;
                    }
                }

                return CreateVersionAddonItem(addonId, status, metadata);
            })
            .OrderBy(item => item.Status == AddonDiffStatus.Removed ? 1 : 0)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.AddonId, StringComparer.Ordinal)
            .ToList();

        if (generation != addonLoadGeneration || disposed)
        {
            return Task.CompletedTask;
        }

        selectedVersionAddons.Clear();
        foreach (var item in items)
        {
            selectedVersionAddons.Add(item);
        }

        _ = LoadAddonVisualsAsync(items, generation);
        return Task.CompletedTask;
    }

    private VersionAddonItemViewModel CreateVersionAddonItem(
        string addonId,
        AddonDiffStatus status,
        IReadOnlyDictionary<string, WorkshopAddon> metadata)
    {
        var addon = metadata.TryGetValue(addonId, out var saved)
            ? saved
            : new WorkshopAddon
            {
                Id = addonId,
                Title = L.Format(
                    "VersionManagement.WorkshopIdDeletedFormat",
                    addonId),
                IsAvailable = false
            };

        if (!addonViewModelCache.TryGetValue(addonId, out var addonViewModel))
        {
            addonViewModel = new AddonItemViewModel(addon, addonManager);
            addonViewModelCache.Add(addonId, addonViewModel);
        }
        else
        {
            addonViewModel.UpdateFromWorkshopAddon(addon);
        }

        return new VersionAddonItemViewModel(addonViewModel, status);
    }

    private async Task LoadAddonVisualsAsync(
        IReadOnlyList<VersionAddonItemViewModel> items,
        int generation)
    {
        foreach (var item in items)
        {
            if (disposed || generation != addonLoadGeneration)
            {
                return;
            }

            try
            {
                await item.AddonItemViewModel.LoadThumbnailCommand.Execute();
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException(
                    "VersionManagementViewModel.LoadAddonVisualsAsync",
                    ex);
            }
        }
    }

    private static List<string> NormalizeAddonIds(IEnumerable<string>? addonIds)
    {
        return addonIds?
                   .Where(addonId => !string.IsNullOrWhiteSpace(addonId) && addonId != "*")
                   .Select(addonId => addonId.Trim())
                   .Distinct(StringComparer.Ordinal)
                   .ToList() ??
               new List<string>();
    }

    private async Task RefreshMainWindowAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.DataContext is not MainWindowViewModel mainWindow)
        {
            return;
        }

        mainWindow.AssetListViewModel.LoadAssets();
        await mainWindow.RefreshAddonsAsync(
            rescanWorkshop: false,
            showProgress: false);
    }

    private static void RefreshAssetList()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainWindow)
        {
            mainWindow.AssetListViewModel.LoadAssets();
        }
    }

    private string GetAssetDisplayName()
    {
        return asset.Id switch
        {
            "subscribe-system-asset" => L.Get("Asset.SubscribeAsset"),
            _ => asset.Name
        };
    }

    private void OnLocalizationChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (disposed ||
            (e.PropertyName != nameof(LocalizationManager.CurrentLanguage) &&
             !string.IsNullOrEmpty(e.PropertyName)))
        {
            return;
        }

        this.RaisePropertyChanged(nameof(AssetName));
        this.RaisePropertyChanged(nameof(AssetTitle));
        this.RaisePropertyChanged(nameof(SelectedVersionTitle));
        foreach (var version in versions)
        {
            version.NotifyLanguageChanged();
        }

        foreach (var addon in selectedVersionAddons)
        {
            addon.NotifyLanguageChanged();
        }
    }

    public void Release()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        addonLoadGeneration++;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        foreach (var addonViewModel in addonViewModelCache.Values)
        {
            addonViewModel.Dispose();
        }

        addonViewModelCache.Clear();
        selectedVersionAddons.Clear();
    }
}

public sealed class VersionItemViewModel : ViewModelBase
{
    private bool isSelected;

    public int Version { get; init; }

    public DateTime CreatedAt { get; init; }

    public int AddonCount { get; init; }

    public bool IsCurrent { get; init; }

    public bool HasMembershipChanges { get; init; }

    public bool CanDelete => true;

    public bool CanRestore => !IsCurrent || HasMembershipChanges;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref isSelected, value);
            this.RaisePropertyChanged(nameof(BorderColor));
            this.RaisePropertyChanged(nameof(OuterBorderColor));
            this.RaisePropertyChanged(nameof(OuterBorderThickness));
            this.RaisePropertyChanged(nameof(OuterBorderPadding));
        }
    }

    public string VersionDisplay => $"v{Version}";

    public string CreatedAtDisplay =>
        CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");

    public string AddonCountDisplay =>
        L.Format("VersionManagement.AddonCountFormat", AddonCount);

    public string ChangeIndicator =>
        !HasMembershipChanges
            ? string.Empty
            : LocalizationManager.Instance.CurrentLanguage.StartsWith(
                "ja",
                StringComparison.OrdinalIgnoreCase)
                ? "● 変更あり"
                : "● Changed";

    public string BackgroundColor => IsCurrent ? "#1E3A5F" : "Transparent";

    public string BorderColor
    {
        get
        {
            if (IsSelected && IsCurrent)
            {
                return "#4A90E2";
            }

            if (IsSelected)
            {
                return "#4CAF50";
            }

            return IsCurrent ? "#4A90E2" : "#444444";
        }
    }

    public string OuterBorderColor =>
        IsSelected && IsCurrent ? "#4CAF50" : "Transparent";

    public string OuterBorderThickness =>
        IsSelected && IsCurrent ? "3" : "0";

    public string OuterBorderPadding =>
        IsSelected && IsCurrent ? "3" : "0";

    public void NotifyLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(AddonCountDisplay));
        this.RaisePropertyChanged(nameof(ChangeIndicator));
    }
}

public sealed class VersionAddonItemViewModel : ViewModelBase
{
    public VersionAddonItemViewModel(
        AddonItemViewModel addonItemViewModel,
        AddonDiffStatus status)
    {
        AddonItemViewModel = addonItemViewModel;
        Status = status;
    }

    public AddonItemViewModel AddonItemViewModel { get; }

    public string AddonId => AddonItemViewModel.AddonId;

    public string Title => AddonItemViewModel.Title;

    public AddonDiffStatus Status { get; }

    public string BorderColor => Status switch
    {
        AddonDiffStatus.Added => "#4CAF50",
        AddonDiffStatus.Removed => "#F44336",
        _ => "#666666"
    };

    public string StatusText => Status switch
    {
        AddonDiffStatus.Added => L.Get("VersionManagement.DiffAdded"),
        AddonDiffStatus.Removed => L.Get("VersionManagement.DiffRemoved"),
        _ => string.Empty
    };

    public void NotifyLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(StatusText));
    }
}
