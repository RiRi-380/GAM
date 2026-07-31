using GmodAddonManager.Core.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

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

        // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・
        CreateAssetCommand = ReactiveCommand.CreateFromTask(CreateAssetAsync);
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
    
    public ReactiveCommand<Unit, Unit> CreateAssetCommand { get; }
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
        Assets.Clear();
    }

    public void LoadAssets()
    {
        try
        {
            // 迴ｾ蝨ｨ縺ｮ驕ｸ謚槭ｒ險俶・
            var previousSelectedId = SelectedAsset?.Id;
            
            
            ClearAssets();

            var configuration = addonManager.GetConfiguration();

            var orderedAssets = configuration.Assets
                .OrderBy(asset => asset.Id == "subscribe-system-asset" ? 0 : asset.IsFavorite ? 1 : 2)
                .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.Id, StringComparer.Ordinal);

            foreach (var asset in orderedAssets)
            {
                var assetVm = new AssetItemViewModel(
                    asset,
                    addonManager,
                    pendingChangeManager,
                    processWatcher
                );

                Assets.Add(assetVm);
            }

            // 莉･蜑阪・驕ｸ謚槭ｒ蠕ｩ蜈・
            if (!string.IsNullOrEmpty(previousSelectedId))
            {
                var previousAsset = Assets.FirstOrDefault(a => a.Id == previousSelectedId);
                if (previousAsset != null)
                {
                    SelectedAsset = previousAsset;
                    return;
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

            var createdAsset = await addonManager.CreateAssetAsync(trimmedName);
            var config = addonManager.GetConfiguration();
            if (config?.Assets == null)
            {
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.ConfigLoadFailed"));
                return;
            }

            var createdAssetId = createdAsset.Id;
            LoadAssets();

            if (!string.IsNullOrWhiteSpace(createdAssetId))
            {
                SelectedAsset = Assets.FirstOrDefault(a => a.Id == createdAssetId);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.CreateAsset", ex);
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetCreateFailedGeneric"));
        }
    }
    private Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
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



