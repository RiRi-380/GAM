using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using System.IO;
using GmodAddonManager.UI.Models;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AssetListViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly IDialogService dialogService;
    private IDisposable? selectedAssetSubscription;
    private bool disposed;

    private ObservableCollection<AssetItemViewModel> assets;
    private AssetItemViewModel? selectedAsset;
    private ObservableCollection<AssetItemViewModel> junctionAsset;

    public AssetListViewModel(
        AddonManager addonManager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher)
    {
        this.addonManager = addonManager;
        this.pendingChangeManager = pendingChangeManager;
        this.processWatcher = processWatcher;
        this.dialogService = new DialogService();

        assets = new ObservableCollection<AssetItemViewModel>();
        junctionAsset = new ObservableCollection<AssetItemViewModel>();

        // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・
        CreateAssetCommand = ReactiveCommand.CreateFromTask(CreateAssetAsync);
        ImportAssetCommand = ReactiveCommand.CreateFromTask(ImportAssetAsync);
        DeleteSelectedAssetCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedAssetAsync,
            this.WhenAnyValue(x => x.SelectedAsset)
                .Select(asset => asset != null && !asset.IsSystem)
        );
        RefreshCommand = ReactiveCommand.Create(LoadAssets);

        // 驕ｸ謚槫､画峩縺ｮ逶｣隕・
        selectedAssetSubscription = this.WhenAnyValue(x => x.SelectedAsset)
            .Subscribe(asset =>
            {
                // 莉･蜑阪・驕ｸ謚槭→IsCurrent迥ｶ諷九ｒ隗｣髯､
                foreach (var a in Assets)
                {
                    a.IsSelected = false;
                    a.IsCurrent = false;
                }
                foreach (var a in JunctionAsset)
                {
                    a.IsSelected = false;
                    a.IsCurrent = false;
                }
                
                // 譁ｰ縺励＞驕ｸ謚槭ｒ險ｭ螳・
                if (asset != null)
                {
                    asset.IsSelected = true;
                    asset.IsCurrent = true;
                }
            });
    }

    public ObservableCollection<AssetItemViewModel> Assets
    {
        get => assets;
        private set => SetAndRaise(ref assets, value);
    }

    public AssetItemViewModel? SelectedAsset
    {
        get => selectedAsset;
        set => SetAndRaise(ref selectedAsset, value);
    }
    
    public ObservableCollection<AssetItemViewModel> JunctionAsset
    {
        get => junctionAsset;
        private set => SetAndRaise(ref junctionAsset, value);
    }

    public bool ShowJunctionAsset => addonManager.DisableMode == DisableMode.Hard;

    public ReactiveCommand<Unit, Unit> CreateAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        selectedAssetSubscription?.Dispose();
        selectedAssetSubscription = null;
        ClearAssets();
        GC.SuppressFinalize(this);
    }

    private void ClearAssets()
    {
        foreach (var asset in Assets)
        {
            asset.Dispose();
        }
        foreach (var asset in JunctionAsset)
        {
            asset.Dispose();
        }
        Assets.Clear();
        JunctionAsset.Clear();
    }

    public void LoadAssets()
    {
        try
        {
            // 迴ｾ蝨ｨ縺ｮ驕ｸ謚槭ｒ險俶・
            var previousSelectedId = SelectedAsset?.Id;
            
            
            ClearAssets();

            var settings = AppSettings.Load();
            var showExclusiveApply = DeveloperModeCommands.ShouldShowExclusiveApply(
                addonManager,
                settings.DeveloperModePhrase);
            
            var configuration = addonManager.GetConfiguration();
            
            foreach (var asset in configuration.Assets)
            {
                var assetVm = new AssetItemViewModel(
                    asset,
                    addonManager,
                    pendingChangeManager,
                    processWatcher,
                    showExclusiveApply
                );
                
                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺ｯ蛻･謇ｱ縺・               
                if (asset.Id == "junction-system-asset")
                {
                    if (ShowJunctionAsset)
                    {
                        JunctionAsset.Add(assetVm);
                    }
                    else
                    {
                        assetVm.Dispose();
                    }
                }
                else
                {
                    Assets.Add(assetVm);
                }
            }

            // 莉･蜑阪・驕ｸ謚槭ｒ蠕ｩ蜈・
            if (!string.IsNullOrEmpty(previousSelectedId))
            {
                var previousAsset = Assets.FirstOrDefault(a => a.Id == previousSelectedId) 
                                   ?? JunctionAsset.FirstOrDefault(a => a.Id == previousSelectedId);
                if (previousAsset != null)
                {
                    SelectedAsset = previousAsset;
                    return;
                }
                if (!ShowJunctionAsset && previousSelectedId == "junction-system-asset")
                {
                    _ = ShowAssetUnavailableInModeWarningAsync();
                }
            }
            
            // 莉･蜑阪・驕ｸ謚槭′隕九▽縺九ｉ縺ｪ縺・ｴ蜷医・縺ｿ縲∵怙蛻昴・繧｢繧ｻ繝・ヨ繧帝∈謚・
            if (Assets.Count > 0)
            {
                SelectedAsset = Assets[0];
            }

        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.LoadAssets", ex);
        }
    }


    private async Task ShowAssetUnavailableInModeWarningAsync()
    {
        try
        {
            await dialogService.ShowErrorAsync(
                L.Get("Warning.Title"),
                L.Get("Warning.AssetUnavailableInMode"));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ShowAssetUnavailableInModeWarningAsync", ex);
        }
    }

    private async Task CreateAssetAsync()
    {
        try
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                return;
            }

            var dialog = new SimpleAssetCreateDialog();
            var result = await dialog.ShowDialog<string?>(mainWindow);
            if (string.IsNullOrWhiteSpace(result))
            {
                return;
            }

            var trimmedName = result.Trim();
            if (addonManager.AssetNameExists(trimmedName))
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Format("Error.AssetNameAlreadyExists", trimmedName));
                return;
            }

            addonManager.CreateAsset(trimmedName);

            var config = addonManager.GetConfiguration();
            if (config?.Assets == null)
            {
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.ConfigLoadFailed"));
                return;
            }

            var createdAssetId = config.Assets.FirstOrDefault(a => a.Name == trimmedName)?.Id;
            await addonManager.SaveConfigurationImmediatelyAsync();
            LoadAssets();

            if (!string.IsNullOrWhiteSpace(createdAssetId))
            {
                SelectedAsset = Assets.FirstOrDefault(a => a.Id == createdAssetId)
                    ?? JunctionAsset.FirstOrDefault(a => a.Id == createdAssetId);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.CreateAsset", ex);
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetCreateFailedGeneric"));
        }
    }
    private async Task ImportAssetAsync()
    {
        try
        {
            // 繝繧､繧｢繝ｭ繧ｰ繧定｡ｨ遉ｺ
            await dialogService.ShowCreateAssetDialogAsync(async (name, addonIds) =>
            {
                
                try
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var trimmedName = name.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedName))
                        {
                            return;
                        }

                        if (addonManager.AssetNameExists(trimmedName))
                        {
                            await dialogService.ShowErrorAsync(
                                L.Get("Error.Title"),
                                L.Format("Error.AssetNameAlreadyExists", trimmedName));
                            return;
                        }

                        // 繧｢繧ｻ繝・ヨ繧剃ｽ懈・
                        addonManager.CreateAsset(trimmedName);
                    
                    // 菴懈・縺励◆繧｢繧ｻ繝・ヨ繧貞叙蠕・
                    var config = addonManager.GetConfiguration();
                    if (config?.Assets == null)
                    {
                        await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.ConfigLoadFailed"));
                        return;
                    }
                    
                    var asset = config.Assets.FirstOrDefault(a => a.Name == trimmedName);
                    if (asset == null)
                    {
                        await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Format("Error.AssetCreateFailed", trimmedName));
                        return; // 繧｢繧ｻ繝・ヨ縺ｮ菴懈・縺ｫ螟ｱ謨・
                    }
                    
                    // 繧ｳ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ縺ｾ縺溘・GAM繝輔ぃ繧､繝ｫ縺九ｉ縺ｮ繧､繝ｳ繝昴・繝医・蝣ｴ蜷・
                    if (addonIds != null && addonIds.Count > 0)
                    {
                        if (addonManager.DisableMode == DisableMode.Soft)
                        {
                            var allAddons = addonManager.GetAllAddons();
                            var localAddonIds = new HashSet<string>(
                                allAddons?.Values.Select(addon => addon.Id) ?? Enumerable.Empty<string>());
                            var localExistingItems = new List<string>();
                            var localMissingItems = new List<string>();

                            foreach (var addonId in addonIds)
                            {
                                if (addonId == "*")
                                {
                                    continue;
                                }

                                if (localAddonIds.Contains(addonId))
                                {
                                    localExistingItems.Add(addonId);
                                }
                                else
                                {
                                    localMissingItems.Add(addonId);
                                }
                            }

                            if (localExistingItems.Count > 0)
                            {
                                using var progressDialog = ProgressDialogService.Show(
                                    GetMainWindow(),
                                    L.Get("Busy.AddingAddonsToAsset"),
                                    L.Format("Busy.Detail.AssetNameWithCount", asset.Name, localExistingItems.Count));
                                var progress = progressDialog?.CreateProgress();

                                addonManager.AddAddonsToAssetBatch(asset.Id, localExistingItems, AddonState.Enabled, progress);
                                
                                // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ繝舌・繧ｸ繝ｧ繝ｳ繧剃ｽ懈・
                                var importBaselineVersion = new AssetVersion
                                {
                                    Version = -1,
                                    CreatedAt = DateTime.Now,
                                    AddonIds = new List<string>(localExistingItems),
                                    IncludeAddonStates = true,
                                    IsImportBaseline = true,
                                    NewlySubscribedAddonIds = new List<string>(),
                                    ImportType = ImportTypes.GamFormat,
                                    Note = L.Format("Version.ImportBaselineNote", name)
                                };
                                
                                importBaselineVersion.AddonStates = new Dictionary<string, AddonState>();
                                foreach (var addonId in localExistingItems)
                                {
                                    importBaselineVersion.AddonStates[addonId] = AddonState.Enabled;
                                }
                                
                                asset.VersionHistory.Add(importBaselineVersion);
                            }

                            if (localMissingItems.Count > 0)
                            {
                                var message = L.Format("Import.LocalMissingAddonsWarning", localMissingItems.Count);
                                await dialogService.ShowWarningAsync(L.Get("Warning.Title"), message);
                            }
                            else if (localExistingItems.Count == 0)
                            {
                                await dialogService.ShowWarningAsync(
                                    L.Get("Warning.Title"),
                                    L.Get("Warning.NoAddonsToAdd"));
                            }
                        }

                        if (addonManager.DisableMode != DisableMode.Soft)
                        {
                            var normalizedAddonIds = addonIds.Where(id => id != "*").Distinct().ToList();
                            var allAddons = addonManager.GetAllAddons() ?? new Dictionary<string, WorkshopAddon>();
                            var subscribedIds = new HashSet<string>(SteamWorkshopCacheReader.GetSubscribedAddonIds());
                            var itemsToSubscribe = normalizedAddonIds.Where(id => !subscribedIds.Contains(id)).ToList();

                            var workshopService = ViewModelLocator.SteamWorkshopService;
                            if (workshopService != null)
                            {
                                var existingConfig = addonManager.GetConfiguration();
                                if (existingConfig != null)
                                {
                                    var needsMetadataUpdate = normalizedAddonIds.Where(id =>
                                    {
                                        if (existingConfig.AddonMetadata.TryGetValue(id, out var addon))
                                        {
                                            return addon.NeedsTitleUpdate ||
                                                   string.IsNullOrEmpty(addon.Title) ||
                                                   AddonTitleHelper.IsPlaceholderTitle(addon.Title);
                                        }
                                        return true;
                                    }).ToList();

                                    if (needsMetadataUpdate.Count > 0)
                                    {
                                        var mainWindow = GetMainWindow();
                                        using var progressDialog = ProgressDialogService.Show(
                                            mainWindow,
                                            L.Get("Busy.FetchingMetadata"),
                                            L.Format("Busy.Detail.AddonCount", needsMetadataUpdate.Count));
                                        progressDialog?.UpdateProgress(0, needsMetadataUpdate.Count);

                                        var metadataResults = await workshopService.GetWorkshopDetailsBatchAsync(needsMetadataUpdate);
                                        foreach (var kvp in metadataResults)
                                        {
                                            if (existingConfig.AddonMetadata.ContainsKey(kvp.Key))
                                            {
                                                var addon = existingConfig.AddonMetadata[kvp.Key];
                                                addon.Title = kvp.Value.Title ?? addon.Title;
                                                addon.Description = kvp.Value.Description ?? addon.Description;
                                            addon.Author = kvp.Value.Creator ?? addon.Author;
                                            addon.Size = (long)kvp.Value.FileSize;
                                            addon.LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)kvp.Value.TimeUpdated).DateTime;
                                            addon.NeedsTitleUpdate = false;
                                            if (kvp.Value.Tags != null && kvp.Value.Tags.Length > 0 &&
                                                (addon.Tags == null || addon.Tags.Length == 0))
                                            {
                                                addon.Tags = kvp.Value.Tags;
                                            }
                                        }
                                        else
                                        {
                                            var addon = new WorkshopAddon(kvp.Key, "")
                                            {
                                                Title = kvp.Value.Title ?? kvp.Key,
                                                Description = kvp.Value.Description ?? string.Empty,
                                                Author = kvp.Value.Creator ?? string.Empty,
                                                Size = (long)kvp.Value.FileSize,
                                                LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)kvp.Value.TimeUpdated).DateTime,
                                                NeedsTitleUpdate = false,
                                                Tags = kvp.Value.Tags ?? Array.Empty<string>()
                                            };
                                            existingConfig.AddonMetadata[kvp.Key] = addon;
                                        }

                                            if (!string.IsNullOrEmpty(kvp.Value.PreviewUrl) && ulong.TryParse(kvp.Key, out var workshopId))
                                            {
                                                var iconResolver = (Avalonia.Application.Current as App)?.WorkshopIconResolver;
                                                if (iconResolver != null)
                                                {
                                                    _ = iconResolver.GetIconAsync(workshopId);
                                                }
                                            }
                                        }

                                        await addonManager.SaveConfigurationAsync();
                                        progressDialog?.Close();
                                    }
                                }
                            }var addonsToAdd = new List<string>(normalizedAddonIds);

                            if (addonsToAdd.Count > 0)
                            {
                                using var progressDialog = ProgressDialogService.Show(
                                    GetMainWindow(),
                                    L.Get("Busy.AddingAddonsToAsset"),
                                    L.Format("Busy.Detail.AssetNameWithCount", asset.Name, addonsToAdd.Count));
                                var progress = progressDialog?.CreateProgress();

                                addonManager.AddAddonsToAssetBatch(asset.Id, addonsToAdd, AddonState.Enabled, progress);

                                var importBaselineVersion = new AssetVersion
                                {
                                    Version = -1,
                                    CreatedAt = DateTime.Now,
                                    AddonIds = new List<string>(addonsToAdd),
                                    IncludeAddonStates = true,
                                    IsImportBaseline = true,
                                    NewlySubscribedAddonIds = itemsToSubscribe,
                                    ImportType = itemsToSubscribe.Count > 0 ? ImportTypes.Collection : ImportTypes.GamFormat,
                                    Note = L.Format("Version.ImportBaselineNote", name)
                                };

                                importBaselineVersion.AddonStates = new Dictionary<string, AddonState>();
                                foreach (var addonId in addonsToAdd)
                                {
                                    importBaselineVersion.AddonStates[addonId] = AddonState.Enabled;
                                }

                                asset.VersionHistory.Add(importBaselineVersion);
                            }

                            var missingItems = normalizedAddonIds.Where(id => !allAddons.ContainsKey(id)).ToList();

                            if (missingItems.Count > 0 || itemsToSubscribe.Count > 0)
                            {
                                var lines = new List<string>
                                {
                                    L.Format("Import.AddedSummary", addonsToAdd.Count, normalizedAddonIds.Count),
                                    ""
                                };

                                if (missingItems.Count > 0)
                                {
                                    lines.Add(L.Format("Import.LocalMissingAddonsWarning", missingItems.Count));
                                }

                                if (itemsToSubscribe.Count > 0)
                                {
                                    lines.Add(L.Format("Import.NewlySubscribedCount", itemsToSubscribe.Count));
                                    lines.Add(L.Get("Warning.AddAddonsSubscribeHint"));
                                }

                                var message = string.Join("\n", lines);
                                await dialogService.ShowInfoAsync(L.Get("Import.ResultTitle"), message);
                            }
                        }

                        }
                    
                    // 蜊ｳ蠎ｧ縺ｫ菫晏ｭ假ｼ医ョ繝舌え繝ｳ繧ｹ繧堤┌隕厄ｼ・
                    await addonManager.SaveConfigurationImmediatelyAsync();
                    
                    // 繝｡繧､繝ｳ繧ｦ繧｣繝ｳ繝峨え蜈ｨ菴薙ｒ繝ｪ繝輔Ξ繝・す繝･・域眠縺励＞繧｢繝峨が繝ｳ繧貞性繧・・
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktopApp)
                    {
                        if (desktopApp.MainWindow?.DataContext is MainWindowViewModel mainVm)
                        {
                            await mainVm.RefreshAddonsAsync(showProgress: false);
                        }
                    }
                    
                    // 譁ｰ縺励￥菴懈・縺励◆繧｢繧ｻ繝・ヨ繧帝∈謚・
                    var newAsset = Assets.FirstOrDefault(a => a.Id == asset.Id);
                    if (newAsset != null)
                    {
                        SelectedAsset = newAsset;
                    }
                }
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("AssetListViewModel.ImportAsset.Callback", ex);
                    await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetCreateFailedGeneric"));
                }
            });
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ImportAsset", ex);
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.DialogDisplayFailed"));
        }
    }

    private IDisposable? BeginBusy(string title, string? detail = null)
    {
        return ViewModelLocator.MainWindowViewModel?.BeginBusy(title, detail);
    }

    private void UpdateBusyProgress(int current, int total)
    {
        ViewModelLocator.MainWindowViewModel?.UpdateBusyProgress(current, total);
    }

    private Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    private async Task DeleteSelectedAssetAsync()
    {
        if (SelectedAsset == null || SelectedAsset.IsSystem) return;

        var confirmed = await dialogService.ShowConfirmAsync(
            L.Get("Confirm.Title"),
            L.Format("Confirm.DeleteAsset", SelectedAsset.Name)
        );

        if (confirmed)
        {
            try
            {
                await SelectedAsset.DeleteCommand.Execute();
                LoadAssets();
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AssetListViewModel.DeleteSelectedAssetAsync", ex);
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetDeleteFailed"));
            }
        }
    }

    public void EnableAllAssets()
    {
        foreach (var asset in Assets.Where(a => !a.IsEnabled))
        {
            ExecuteToggleEnabledSafe(asset);
        }
    }

    public void DisableAllAssets()
    {
        foreach (var asset in Assets.Where(a => a.IsEnabled && !a.IsSystem))
        {
            ExecuteToggleEnabledSafe(asset);
        }
    }


    private void ExecuteToggleEnabledSafe(AssetItemViewModel asset)
    {
        try
        {
            asset.ToggleEnabledCommand.Execute().Subscribe(
                _ => { },
                ex => SafeFileLogger.TryLogException("AssetListViewModel.ToggleEnabledCommand", ex));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ToggleEnabledCommand.Dispatch", ex);
        }
    }

    public AssetItemViewModel? GetAssetById(string assetId)
    {
        return Assets.FirstOrDefault(a => a.Id == assetId);
    }

    public void RefreshAssetStates()
    {
        var configuration = addonManager.GetConfiguration();
        foreach (var assetVm in Assets)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetVm.Id);
            if (asset != null)
            {
                assetVm.RefreshFromModel(asset);
            }
        }
    }
}



