using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicData;
using DynamicData.Binding;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace GmodAddonManager.UI.ViewModels;

public class AddonGridViewModel : ViewModelBase
{
    private readonly AddonManager addonManager;
    
    private ObservableCollection<AddonItemViewModel> allAddons;
    private ObservableCollection<AddonItemViewModel> filteredAddons;
    private string filterText = "";
    private bool isLoading;
    private AssetItemViewModel? currentAsset;
    private bool showOnlyAssetAddons;
    private bool isMultiSelectEnabled;
    private HashSet<string> selectedAddonIds;
    private AddonItemViewModel? selectedAddon;
    private bool isSelectionMode;
    private bool hasSelectedAddons;
    private int addonFilterIndex = 0; // 0=全て, 1=通常のみ, 2=キャッシュのみ
    private DashboardViewModel? dashboardViewModel;

    public AddonGridViewModel(AddonManager addonManager)
    {
        this.addonManager = addonManager;

        allAddons = new ObservableCollection<AddonItemViewModel>();
        filteredAddons = new ObservableCollection<AddonItemViewModel>();
        selectedAddonIds = new HashSet<string>();

        // コマンドの初期化
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAddonsAsync);
        LoadDetailsCommand = ReactiveCommand.CreateFromTask<AddonItemViewModel>(LoadAddonDetailsAsync);
        AddSelectedAddonsCommand = ReactiveCommand.CreateFromTask(ShowAssetSelectionDialogAsync);
        SelectAllCommand = ReactiveCommand.Create(SelectAll);
        RemoveSelectedAddonsCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAddonsAsync);
        ChangeSelectedAddonStateCommand = ReactiveCommand.CreateFromTask<string>(ChangeSelectedAddonStateAsync);

        // フィルタリングの設定
        this.WhenAnyValue(
                x => x.FilterText,
                x => x.ShowOnlyAssetAddons,
                x => x.CurrentAsset,
                x => x.AddonFilterIndex)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(_ => ApplyFilter());
            
    }

    public ObservableCollection<AddonItemViewModel> AllAddons
    {
        get => allAddons;
        private set => SetAndRaise(ref allAddons, value);
    }

    public ObservableCollection<AddonItemViewModel> FilteredAddons
    {
        get => filteredAddons;
        private set
        {
            SetAndRaise(ref filteredAddons, value);
            this.RaisePropertyChanged(nameof(FilteredAddonsCount));
        }
    }

    public string FilterText
    {
        get => filterText;
        set => SetAndRaise(ref filterText, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        set => SetAndRaise(ref isLoading, value);
    }

    public AssetItemViewModel? CurrentAsset
    {
        get => currentAsset;
        set
        {
            SetAndRaise(ref currentAsset, value);
            this.RaisePropertyChanged(nameof(SelectionButtonText));
            this.RaisePropertyChanged(nameof(CanRemoveFromAsset));
            this.RaisePropertyChanged(nameof(AddonCountDisplay));
        }
    }

    public bool ShowOnlyAssetAddons
    {
        get => showOnlyAssetAddons;
        set => SetAndRaise(ref showOnlyAssetAddons, value);
    }

    public bool IsMultiSelectEnabled
    {
        get => isMultiSelectEnabled;
        set
        {
            SetAndRaise(ref isMultiSelectEnabled, value);
            if (!value)
            {
                // 複数選択を無効にしたら選択をクリア
                ClearSelection();
            }
        }
    }

    public AddonItemViewModel? SelectedAddon
    {
        get => selectedAddon;
        set => SetAndRaise(ref selectedAddon, value);
    }

    public DashboardViewModel? DashboardViewModel
    {
        get
        {
            if (dashboardViewModel == null)
            {
                dashboardViewModel = new DashboardViewModel(addonManager);
            }
            return dashboardViewModel;
        }
    }

    public int TotalAddonsCount => AllAddons.Count;
    public int FilteredAddonsCount => FilteredAddons.Count;
    
    public string AddonCountDisplay
    {
        get
        {
            if (CurrentAsset == null)
            {
                return $"({FilteredAddonsCount})";
            }
            
            // フィルタが適用されている場合
            if (!string.IsNullOrEmpty(FilterText) || addonFilterIndex != 0)
            {
                // フィルタ適用時は「表示数/総数」を表示
                var totalCount = CurrentAsset.AddonCount;
                return $"({FilteredAddonsCount}/{totalCount})";
            }
            else
            {
                // フィルタなしの場合は表示数のみ
                return $"({FilteredAddonsCount})";
            }
        }
    }

    public bool IsSelectionMode
    {
        get => isSelectionMode;
        set
        {
            SetAndRaise(ref isSelectionMode, value);
            if (!value)
            {
                // 選択モードを解除したら選択をクリア
                ClearSelection();
            }
        }
    }

    public bool HasSelectedAddons
    {
        get => hasSelectedAddons;
        private set
        {
            SetAndRaise(ref hasSelectedAddons, value);
            this.RaisePropertyChanged(nameof(CanRemoveFromAsset));
        }
    }
    
    public int SelectedAddonsCount => selectedAddonIds.Count;
    
    public string SelectionButtonText => currentAsset?.Id == "junction-system-asset" ? L.Get("Action.Restore") : L.Get("Action.Transfer");
    
    public bool CanRemoveFromAsset => HasSelectedAddons && 
                                      currentAsset != null && 
                                      !currentAsset.IsSystem &&
                                      currentAsset.Id != "junction-system-asset" &&
                                      currentAsset.Id != "subscribe-system-asset";
    
    public int AddonFilterIndex
    {
        get => addonFilterIndex;
        set
        {
            if (addonFilterIndex != value)
            {
                addonFilterIndex = value;
                this.RaisePropertyChanged(nameof(AddonFilterIndex));
                this.RaisePropertyChanged(nameof(AddonCountDisplay));
            }
        }
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<AddonItemViewModel, Unit> LoadDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSelectedAddonsCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedAddonsCommand { get; }
    public ReactiveCommand<string, Unit> ChangeSelectedAddonStateCommand { get; }

    public async Task LoadAddonsAsync()
    {
        try
        {
            IsLoading = true;
#if DEBUG
            // AddonGridViewModel.LoadAddonsAsync called
#endif
            
            // 新しいコレクションを作成
            var newAllAddons = new ObservableCollection<AddonItemViewModel>();

            // ScanWorkshopFolderAsyncは全てのアドオン（GMAファイル含む）を返す
#if DEBUG
            // Calling ScanWorkshopFolderAsync from AddonGridViewModel
#endif
            var addonList = await addonManager.ScanWorkshopFolderAsync();
#if DEBUG
            // ScanWorkshopFolderAsync returned {addonList.Count} addons
#endif
            
            // ローカルアドオンIDのセットを作成
            var localAddonIds = new HashSet<string>(addonList.Select(a => a.Id));
            
            // アセットに含まれているが、ローカルに存在しないアドオンも追加
            var config = addonManager.GetConfiguration();
            var allAssetAddonIds = new HashSet<string>();
            
            // すべてのアセットからアドオンIDを収集
            foreach (var asset in config.Assets)
            {
                // *を除外してアドオンIDを収集
                foreach (var addonId in asset.Addons.Where(id => id != "*"))
                {
                    allAssetAddonIds.Add(addonId);
                }
            }
            
            // アセットに登録されているが、ローカルに存在しないアドオンを追加
            foreach (var addonId in allAssetAddonIds)
            {
                if (!localAddonIds.Contains(addonId))
                {
                    // メタデータから情報を取得
                    WorkshopAddon addonToAdd = null;
                    if (config.AddonMetadata.TryGetValue(addonId, out var metadata))
                    {
                        addonToAdd = new WorkshopAddon(metadata.Id, metadata.FolderPath)
                        {
                            Title = metadata.Title,
                            Size = metadata.Size,
                            LastUpdated = metadata.LastUpdated,
                            ThumbnailUrl = metadata.ThumbnailUrl,
                            Author = metadata.Author,
                            IsEnabled = metadata.IsEnabled,
                            Description = metadata.Description,
                            Type = metadata.Type,
                            Tags = metadata.Tags,
                            IsGmaFile = metadata.IsGmaFile,
                            NeedsTitleUpdate = metadata.NeedsTitleUpdate,
                            IsFavorite = metadata.IsFavorite
                        };
                    }
                    else
                    {
                        // メタデータがない場合は基本情報のみで作成
                        addonToAdd = new WorkshopAddon(addonId, "")
                        {
                            Title = $"Workshop-{addonId}",
                            NeedsTitleUpdate = true
                        };
                    }
                    addonList.Add(addonToAdd);
                }
            }
            
            // 既存のViewModelのマッピングを作成（再利用のため）
            var existingViewModels = AllAddons.ToDictionary(vm => vm.AddonId, vm => vm);
            
            foreach (var addon in addonList.OrderBy(a => a.Title ?? a.Id))
            {
                // 既存のViewModelがあれば再利用、なければ新規作成
                if (existingViewModels.TryGetValue(addon.Id, out var existingVm))
                {
                    // 既存のViewModelを更新（タイトル等が変更されている可能性がある）
                    if (!addon.NeedsTitleUpdate && addon.Title != null)
                    {
                        existingVm.UpdateTitle(addon.Title);
                    }
                    newAllAddons.Add(existingVm);
                }
                else
                {
                    var addonVm = new AddonItemViewModel(addon, addonManager, null); // logger removed
                    newAllAddons.Add(addonVm);
                }
            }
            
            // UIスレッドで一度に置き換える
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AllAddons = newAllAddons;
            });
            
            ApplyFilter();
            
            // フィルタ適用後、表示されているアドオンのみ詳細とサムネイルを読み込む
            await LoadVisibleAddonDetailsAsync();
            
            // プロパティ変更通知
            this.RaisePropertyChanged(nameof(FilteredAddonsCount));
            this.RaisePropertyChanged(nameof(TotalAddonsCount));
            
            // バックグラウンドでタイトルを更新
            _ = UpdateAddonTitlesInBackgroundAsync();
            
            // logger.LogInformation($"Loaded {AllAddons.Count} addons"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to load addons", ex); // Removed logging
#if DEBUG
            // Failed to load addons: {ex}
#endif
            throw; // エラーを再スローして問題を明確にする
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyFilter()
    {
        try
        {
            var query = AllAddons.AsEnumerable();

            // テキストフィルタ
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                query = query.Where(a => a.MatchesFilter(FilterText));
            }

            // Normal/Cacheフィルタ
            switch (addonFilterIndex)
            {
                case 1: // 通常のみ
                    query = query.Where(a => !a.IsGmaFile);
                    break;
                case 2: // キャッシュのみ
                    query = query.Where(a => a.IsGmaFile);
                    break;
                // case 0: 全て表示（フィルタなし）
            }

            // アセットフィルタ
            if (ShowOnlyAssetAddons)
            {
                // CurrentAssetがnullの場合はフィルタリングしない（すべて表示）
                if (CurrentAsset == null)
                {
                    // アセットが未設定の場合は、すべてのアドオンを表示
                    // ただし、後で適切なアセットが設定されることを想定
                }
                else
                {
                    var assetAddonIds = CurrentAsset.GetAddonIds();
                
                // デバッグログ（ジャンクションアセットの問題調査用）
                if (CurrentAsset.Id == "junction-system-asset" && assetAddonIds.Count > 0)
                {
                    // logger.LogDebug($"Junction asset has {assetAddonIds.Count} addons"); // Removed logging
                }
                
                if (CurrentAsset.Id == "subscribe-system-asset" || assetAddonIds.Contains("*"))
                {
                    // 全アドオンを表示するが、ジャンクションアセットのアドオンは除外
                    var junctionAsset = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                    if (junctionAsset != null && junctionAsset.Addons.Count > 0)
                    {
                        var junctionAddonIds = new HashSet<string>(junctionAsset.Addons);
                        query = query.Where(a => !junctionAddonIds.Contains(a.AddonId));
                    }
                }
                    else
                    {
                        query = query.Where(a => assetAddonIds.Contains(a.AddonId));
                    }
                }
            }

            // 結果を適用
            var results = query.ToList();
            
            // 新しいコレクションを作成してから一度に置き換える
            var newFilteredAddons = new ObservableCollection<AddonItemViewModel>();
            foreach (var addon in results)
            {
                // 現在のアセットを設定して状態を更新
                addon.SetCurrentAsset(CurrentAsset);
                newFilteredAddons.Add(addon);
            }
            
            // UIスレッドで実行されていることを確認
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                FilteredAddons = newFilteredAddons;
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => FilteredAddons = newFilteredAddons);
            }
            
            // フィルタ適用後、表示されているアドオンの詳細を読み込む
            _ = LoadVisibleAddonDetailsAsync();
            
            // アドオン数表示を更新
            this.RaisePropertyChanged(nameof(FilteredAddonsCount));
            this.RaisePropertyChanged(nameof(AddonCountDisplay));
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to apply filter", ex); // Removed logging
        }
    }

    public void SetCurrentAsset(AssetItemViewModel? asset)
    {
        CurrentAsset = asset;
        ShowOnlyAssetAddons = asset != null;
        // アセットが設定されたらフィルターを再適用
        ApplyFilter();
        
        // デバッグ用ログ（起動時の問題調査）
        if (asset == null)
        {
            // [AddonGridViewModel] SetCurrentAsset: asset is null
        }
        else
        {
            // [AddonGridViewModel] SetCurrentAsset: {asset.Id}, ShowOnlyAssetAddons: {ShowOnlyAssetAddons}
        }
    }

    private async Task LoadAddonDetailsAsync(AddonItemViewModel addon)
    {
        if (addon != null && !addon.IsDetailsLoaded)
        {
            await addon.LoadDetailsCommand.Execute().GetAwaiter();
        }
    }

    public void SelectAddon(string addonId, bool isControlPressed = false)
    {
        var addon = FilteredAddons.FirstOrDefault(a => a.AddonId == addonId);
        if (addon != null)
        {
            if (IsSelectionMode || (IsMultiSelectEnabled && isControlPressed))
            {
                // 選択モードまたは複数選択モードでCtrlキー押下時はトグル
                addon.IsSelected = !addon.IsSelected;
                if (addon.IsSelected)
                {
                    selectedAddonIds.Add(addonId);
                }
                else
                {
                    selectedAddonIds.Remove(addonId);
                }
                HasSelectedAddons = selectedAddonIds.Count > 0;
                this.RaisePropertyChanged(nameof(SelectedAddonsCount));
                
                // 選択アイテムが0になったら自動で選択モード解除
                if (IsSelectionMode && selectedAddonIds.Count == 0)
                {
                    IsSelectionMode = false;
                }
            }
            else
            {
                // 単一選択
                ClearSelection();
                addon.IsSelected = true;
                selectedAddonIds.Add(addonId);
                HasSelectedAddons = true;
                this.RaisePropertyChanged(nameof(SelectedAddonsCount));
            }
        }
    }

    public void ClearSelection()
    {
        foreach (var a in FilteredAddons)
        {
            a.IsSelected = false;
        }
        selectedAddonIds.Clear();
        // 注: SelectedAddonは選択とは独立して管理（右クリックで設定）
        HasSelectedAddons = false;
        this.RaisePropertyChanged(nameof(SelectedAddonsCount));
    }

    public ObservableCollection<AddonItemViewModel> GetSelectedAddons()
    {
        return new ObservableCollection<AddonItemViewModel>(
            FilteredAddons.Where(a => a.IsSelected)
        );
    }

    public async Task LoadVisibleAddonDetailsAsync()
    {
        // 現在フィルタされて表示されているアドオンを取得
        var visibleAddons = FilteredAddons.Take(30).ToList();
        
        // Loading details for visible addons
        
        // 表示されているアドオンの詳細とサムネイルを並列で読み込み
        var tasks = new List<Task>();
        foreach (var addon in visibleAddons)
        {
            tasks.Add(Task.Run(async () =>
            {
                await addon.LoadDetailsCommand.Execute();
                await addon.LoadThumbnailCommand.Execute();
            }));
        }
        
        await Task.WhenAll(tasks);
        
        // 残りのアドオンはバックグラウンドで読み込み
        _ = LoadRemainingAddonsAsync(visibleAddons);
    }
    
    public async Task LoadVisibleRangeAsync(int startIndex, int endIndex)
    {
        // 指定範囲のアドオンを取得
        var addonsToLoad = FilteredAddons
            .Skip(startIndex)
            .Take(endIndex - startIndex)
            .Where(a => a.IsThumbnailLoading || !a.IsDetailsLoaded)
            .ToList();
        
        if (!addonsToLoad.Any())
            return;
        
        // 並列で読み込み
        var semaphore = new System.Threading.SemaphoreSlim(10, 10);
        var tasks = new List<Task>();
        
        foreach (var addon in addonsToLoad)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (!addon.IsDetailsLoaded)
                        await addon.LoadDetailsCommand.Execute();
                    if (!addon.IsDetailsLoaded)
                        await addon.LoadThumbnailCommand.Execute();
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }
        
        await Task.WhenAll(tasks);
    }

    private async Task LoadRemainingAddonsAsync(List<AddonItemViewModel> alreadyLoaded)
    {
        var remainingAddons = FilteredAddons.Except(alreadyLoaded).ToList();
        
        foreach (var addon in remainingAddons)
        {
            await addon.LoadDetailsCommand.Execute();
            await addon.LoadThumbnailCommand.Execute();
            await Task.Delay(50); // 負荷分散のための遅延
        }
    }
    
    private async Task UpdateAddonTitlesInBackgroundAsync()
    {
        try
        {
            // バックグラウンドでタイトルを更新
            await addonManager.UpdateAddonTitlesInBackgroundAsync();
            
            // タイトルが更新されたアドオンを反映
            await Task.Delay(2000); // 少し待機してから更新を確認
            
            // UIスレッドで更新
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var config = addonManager.GetConfiguration();
                foreach (var addon in AllAddons)
                {
                    if (config.AddonMetadata.ContainsKey(addon.AddonId))
                    {
                        var metadata = config.AddonMetadata[addon.AddonId];
                        if (!metadata.NeedsTitleUpdate && addon.Title != metadata.Title)
                        {
                            addon.UpdateTitle(metadata.Title);
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
#if DEBUG
            // Failed to update addon titles in background: {ex}
#endif
        }
    }

    private async Task PreloadThumbnailsAsync()
    {
        try
        {
            // logger?.LogInformation("Starting thumbnail preload"); // Removed logging
            
            // 表示されているアドオンから優先的に読み込む
            var visibleAddons = FilteredAddons.Take(30).ToList();
            var remainingAddons = AllAddons.Except(visibleAddons).ToList();
            
            // 表示中のアドオンのサムネイルを並列で読み込み（最大20つ同時）
            var semaphore = new System.Threading.SemaphoreSlim(20, 20);
            var tasks = new List<Task>();
            
            foreach (var addon in visibleAddons)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await addon.LoadThumbnailCommand.Execute();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            // 表示中のアドオンの読み込みを待つ
            await Task.WhenAll(tasks);
            
            // 残りのアドオンもバックグラウンドで読み込み
            _ = Task.Run(async () =>
            {
                foreach (var addon in remainingAddons)
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await addon.LoadThumbnailCommand.Execute();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
            });
            
            // logger?.LogInformation("Thumbnail preload started for visible items"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger?.LogError("Failed to preload thumbnails", ex); // Removed logging
        }
    }
    
    private async Task ShowAssetSelectionDialogAsync()
    {
        try
        {
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0)
            {
                return;
            }
            
            // AssetSelectionDialogを作成
            var dialogService = new DialogService();
            var assetListVm = ViewModelLocator.AssetListViewModel;
            
            if (assetListVm == null)
            {
                // logger.LogError("AssetListViewModel not found"); // Removed logging
                return;
            }
            
            // 全アセットリストを作成（サブスクライブとジャンクションを含む）
            var allAssets = new List<AssetItemViewModel>();
            allAssets.AddRange(assetListVm.Assets);
            allAssets.AddRange(assetListVm.JunctionAsset);
            
            // アセットをソート（サブスクライブとジャンクションを最上位に）
            var sortedAssets = new List<AssetItemViewModel>();
            
            // サブスクライブを最初に
            var subscribeAsset = allAssets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            if (subscribeAsset != null) sortedAssets.Add(subscribeAsset);
            
            // ジャンクションを2番目に
            var junctionAsset = allAssets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset != null) sortedAssets.Add(junctionAsset);
            
            // その他のアセット
            sortedAssets.AddRange(allAssets.Where(a => a != subscribeAsset && a != junctionAsset));
            
            if (!sortedAssets.Any())
            {
                await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoAvailableAssets"));
                return;
            }
            
            // 現在のアセットがジャンクションかどうかで異なるダイアログを表示
            if (currentAsset?.Id == "junction-system-asset")
            {
                // ジャンクションアセットの場合は「戻す」ダイアログを表示
                var junctionDialog = new JunctionAssetSelectionDialog(sortedAssets, 
                    selectedAddons.Count == 1 ? addonManager.GetAddonSourceAssets(selectedAddons.First().AddonId) : new List<string>());
                var mainWindow = GetMainWindow();
                
                if (mainWindow != null)
                {
                    var result = await junctionDialog.ShowDialog<JunctionAssetSelectionDialog.AssetSelectionResult?>(mainWindow);
                    
                    if (result != null)
                    {
                        if (result.RestoreToOriginal)
                        {
                            // 元の場所に戻す
                            foreach (var addon in selectedAddons)
                            {
                                addonManager.RestoreAddonFromJunction(addon.AddonId);
                            }
                            
                            // 状態を更新（ジャンクションの作成/削除を実行）
                            await addonManager.UpdateAddonStatesAsync();
                            
                            await addonManager.SaveConfigurationAsync();
                            
                            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                L.Format("Success.RestoredToOriginal", selectedAddons.Count));
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                        else if (result.SelectedAsset != null)
                        {
                            // 選択したアセットに移動
                            foreach (var addon in selectedAddons)
                            {
                                // ジャンクションアセットから削除
                                addonManager.RemoveAddonFromAsset(currentAsset.Id, addon.AddonId);
                                
                                // 対象アセットに追加（これによりExcluded状態が解除される）
                                addonManager.AddAddonToAsset(result.SelectedAsset.Id, addon.AddonId);
                            }
                            
                            // 状態を更新（ジャンクションの作成/削除を実行）
                            await addonManager.UpdateAddonStatesAsync();
                            
                            await addonManager.SaveConfigurationAsync();
                            
                            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                L.Format("Success.RestoredToAsset", selectedAddons.Count, result.SelectedAsset.Name));
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                }
            }
            else
            {
                // 通常のアセットの場合は従来のダイアログを表示
                var dialog = new AssetSelectionDialog(sortedAssets);
                var mainWindow = GetMainWindow();
                
                if (mainWindow != null)
                {
                    var selectedAsset = await dialog.ShowDialog<AssetItemViewModel?>(mainWindow);
                    
                    if (selectedAsset != null)
                    {
                    // ジャンクション送りの場合は確認
                    if (selectedAsset.Id == "junction-system-asset")
                    {
                        var confirmMessage = selectedAddons.Count == 1
                            ? L.Get("Confirm.SendToJunctionSingle")
                            : L.Format("Confirm.SendToJunctionMultiple", selectedAddons.Count);
                        
                        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), confirmMessage);
                        if (!confirmed)
                        {
                            return;
                        }
                    }
                    
                    // 重複チェック
                    var targetAssetAddonIds = selectedAsset.GetAddonIds();
                    var duplicateAddons = selectedAddons.Where(a => targetAssetAddonIds.Contains(a.AddonId)).ToList();
                    var newAddons = selectedAddons.Except(duplicateAddons).ToList();
                    
                    // 全て重複している場合
                    if (duplicateAddons.Count == selectedAddons.Count)
                    {
                        var message = selectedAddons.Count == 1
                            ? "このアドオンは既に含まれています。"
                            : $"{selectedAddons.Count}つのアドオンは既に含まれています。";
                        await dialogService.ShowInfoAsync("情報", message);
                        return;
                    }
                    
                    // 一部重複している場合
                    if (duplicateAddons.Count > 0)
                    {
                        var message = $"{selectedAddons.Count}つのうち{duplicateAddons.Count}つのアドオンは既に含まれていました。\nそれ以外のアドオンを転送しました。";
                        
                        // 新規アドオンのみ追加
                        var addedCount = 0;
                        var isJunctionTransfer = selectedAsset.Id == "junction-system-asset";
                        
                        foreach (var addon in newAddons)
                        {
                            try
                            {
                                // ジャンクション送りの場合、元のアセットから削除
                                if (isJunctionTransfer && CurrentAsset != null && !CurrentAsset.IsSystem)
                                {
                                    CurrentAsset.RemoveAddon(addon.AddonId);
                                }
                                
                                selectedAsset.AddAddon(addon.AddonId);
                                addedCount++;
                            }
                            catch (Exception ex)
                            {
                                // logger.LogError($"Failed to add addon {addon.AddonId} to asset {selectedAsset.Name}", ex); // Removed logging
                            }
                        }
                        
                        if (addedCount > 0)
                        {
                            // 設定を保存
                            await addonManager.SaveConfigurationAsync();
                            
                            // ジャンクションアセットを更新
                            if (isJunctionTransfer)
                            {
                                await addonManager.UpdateJunctionAssetAsync();
                            }
                            
                            selectedAsset.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == selectedAsset.Id));
                            
                            await dialogService.ShowInfoAsync("情報", message);
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                    else
                    {
                        // 重複なしの場合（従来の処理）
                        var addedCount = 0;
                        var isJunctionTransfer = selectedAsset.Id == "junction-system-asset";
                        
                        foreach (var addon in selectedAddons)
                        {
                            try
                            {
                                // ジャンクション送りの場合、元のアセットから削除
                                if (isJunctionTransfer && CurrentAsset != null && !CurrentAsset.IsSystem)
                                {
                                    CurrentAsset.RemoveAddon(addon.AddonId);
                                }
                                
                                selectedAsset.AddAddon(addon.AddonId);
                                addedCount++;
                            }
                            catch (Exception ex)
                            {
                                // logger.LogError($"Failed to add addon {addon.AddonId} to asset {selectedAsset.Name}", ex); // Removed logging
                            }
                        }
                        
                        if (addedCount > 0)
                        {
                            // 設定を保存
                            await addonManager.SaveConfigurationAsync();
                            
                            // ジャンクションアセットを更新
                            if (isJunctionTransfer)
                            {
                                await addonManager.UpdateJunctionAssetAsync();
                            }
                            
                            selectedAsset.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == selectedAsset.Id));
                            
                            var message = isJunctionTransfer
                                ? (addedCount == 1 
                                    ? L.Get("Success.SentToJunctionSingle") 
                                    : L.Format("Success.SentToJunctionMultiple", addedCount))
                                : (addedCount == 1
                                    ? L.Format("Success.AddedToAssetSingle", selectedAsset.Name)
                                    : L.Format("Success.AddedToAssetMultiple", addedCount, selectedAsset.Name));
                            
                            await dialogService.ShowInfoAsync(L.Get("Success.Title"), message);
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                }
            }
            }
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to show asset selection dialog", ex); // Removed logging
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetSelectionDialogFailed"));
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
    
    private async Task ReloadAddons()
    {
        try
        {
            // MainWindowViewModelを取得してリロード
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
                {
                    await mainVm.RefreshAddonsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to reload addons", ex); // Removed logging
        }
    }
    
    private void SelectAll()
    {
        try
        {
            if (FilteredAddons == null) return;
            
            // 全てのフィルタリングされたアドオンを選択
            foreach (var addon in FilteredAddons)
            {
                if (!addon.IsSelected)
                {
                    addon.IsSelected = true;
                    selectedAddonIds.Add(addon.AddonId);
                }
            }
            
            HasSelectedAddons = selectedAddonIds.Count > 0;
            this.RaisePropertyChanged(nameof(SelectedAddonsCount));
            // logger?.LogInformation($"Selected all {FilteredAddons.Count} visible addons"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger?.LogError("Failed to select all addons", ex); // Removed logging
        }
    }
    
    private void UpdateSelectionState()
    {
        HasSelectedAddons = selectedAddonIds.Count > 0;
        this.RaisePropertyChanged(nameof(SelectedAddonsCount));
    }
    
    private async Task RemoveSelectedAddonsAsync()
    {
        try
        {
            if (currentAsset == null || currentAsset.IsSystem) return;
            
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0) return;
            
            var dialogService = new DialogService();
            var confirmMessage = selectedAddons.Count == 1
                ? L.Get("Confirm.RemoveFromAssetSingle")
                : L.Format("Confirm.RemoveFromAssetMultiple", selectedAddons.Count);
                
            var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), confirmMessage);
            
            if (confirmed)
            {
                // アセットから削除
                foreach (var addon in selectedAddons)
                {
                    addonManager.RemoveAddonFromAsset(currentAsset.Id, addon.AddonId);
                }
                
                // 状態を更新
                await addonManager.UpdateAddonStatesAsync();
                await addonManager.SaveConfigurationAsync();
                
                await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                    selectedAddons.Count == 1 
                        ? L.Get("Success.RemovedFromAssetSingle") 
                        : L.Format("Success.RemovedFromAssetMultiple", selectedAddons.Count));
                
                // 選択モードを解除
                IsSelectionMode = false;
                
                // リロード処理
                await ReloadAddons();
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.RemoveAddonFailed"));
        }
    }
    
    private async Task ChangeSelectedAddonStateAsync(string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(action))
                return;
                
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0)
                return;
                
            // サブスクライブ解除の処理
            if (action == L.Get("AddonGrid.Unsubscribe"))
            {
                await UnsubscribeSelectedAddonsAsync();
                return;
            }
            
            // アセットが選択されていない場合は状態変更不可
            if (currentAsset == null)
                return;
                
            AddonState newState;
            // Check against localized values
            if (action == L.Get("AddonGrid.Enable"))
            {
                newState = AddonState.Enabled;
            }
            else if (action == L.Get("AddonGrid.Disable"))
            {
                newState = AddonState.Disabled;
                
                // Check if Steam is running before disabling addons
                if (SteamProcessChecker.IsSteamRunningViaAPI())
                {
                    var dialog = new DialogService();
                    var result = await dialog.ShowConfirmAsync(
                        L.Get("Warning.SteamRunningTitle") ?? "Steam Running", 
                        L.Get("Warning.SteamRunningDisable") ?? 
                        "Steam is currently running. Disabled addons may be re-downloaded when you start Garry's Mod.\n\n" +
                        "For best results:\n" +
                        "1. Close Garry's Mod\n" +
                        "2. Close Steam completely\n" +
                        "3. Disable addons in GAM\n" +
                        "4. Restart Steam\n\n" +
                        "Continue anyway?"
                    );
                    
                    if (!result)
                        return;
                }
            }
            else if (action == L.Get("AddonGrid.Exclude"))
            {
                newState = AddonState.Excluded;
            }
            else
            {
                return;
            }
            
            // 各アドオンの状態を変更
            foreach (var addon in selectedAddons)
            {
                addonManager.SetAddonState(currentAsset.Id, addon.AddonId, newState);
            }
            
            // 状態を更新
            await addonManager.UpdateAddonStatesAsync();
            await addonManager.SaveConfigurationAsync();
            
            var dialogService = new DialogService();
            await dialogService.ShowInfoAsync(L.Get("Success.Title"),
                L.Format("Success.StateChanged", selectedAddons.Count, action));
                
            // 選択モードを解除
            IsSelectionMode = false;
            
            // リロード
            await ReloadAddons();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.StateChangeFailed"));
        }
    }
    
    private async Task UnsubscribeSelectedAddonsAsync()
    {
        try
        {
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0)
                return;
                
            var dialogService = new DialogService();
            
            // 確認ダイアログを表示
            var result = await dialogService.ShowConfirmAsync(
                L.Get("UnsubscribeConfirm.Title"),
                L.Format("UnsubscribeConfirm.Message", selectedAddons.Count));
                
            if (!result)
                return;
                
            // Steamworksを取得
            var app = Avalonia.Application.Current as App;
            var steamworksManager = app?.SteamworksManager;
            
            // Steamworksが初期化されているか確認
            if (steamworksManager == null || !steamworksManager.IsInitialized)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("Error.SteamworksNotInitialized"));
                return;
            }
            
            // メインウィンドウを取得
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("Error.MainWindowNotFound"));
                return;
            }
            
            // 進捗ダイアログを表示
            var progressDialog = new CollectionUploadProgressDialog();
            progressDialog.Title = L.Get("Unsubscribe.ProgressTitle");
            var cts = new CancellationTokenSource();
            progressDialog.SetCancellationTokenSource(cts);
            _ = progressDialog.ShowDialog(mainWindow);
            
            // サブスクライブ解除処理を実行
            var results = new List<(string addonId, bool success)>();
            int current = 0;
            
            try
            {
                foreach (var addon in selectedAddons)
                {
                    if (cts.Token.IsCancellationRequested)
                        break;
                        
                    // 進捗を更新
                    progressDialog.UpdateStatus(L.Format("Unsubscribe.Processing", current + 1, selectedAddons.Count));
                    progressDialog.UpdateBatchProgress(current + 1, selectedAddons.Count, current + 1, selectedAddons.Count);
                    progressDialog.UpdateDetail($"{addon.Title} ({addon.AddonId})");
                    
                    var success = await steamworksManager.UnsubscribeItemAsync(addon.AddonId);
                    results.Add((addon.AddonId, success));
                    
                    current++;
                    // レート制限を避けるため少し待機
                    await Task.Delay(100);
                }
            }
            finally
            {
                progressDialog.Close();
            }
            
            // 結果を集計
            var successCount = results.Count(r => r.success);
            var failedCount = results.Count(r => !r.success);
            var failedAddons = results.Where(r => !r.success).Select(r => r.addonId).ToList();
            var successfullyUnsubscribed = results.Where(r => r.success).Select(r => r.addonId).ToList();
            
            // 成功したアドオンをすべてのアセットから削除
            if (successfullyUnsubscribed.Any())
            {
                var configuration = addonManager.GetConfiguration();
                foreach (var asset in configuration.Assets)
                {
                    bool assetModified = false;
                    foreach (var addonId in successfullyUnsubscribed)
                    {
                        if (asset.Addons.Remove(addonId))
                        {
                            assetModified = true;
                        }
                        // AddonStatesからも削除
                        if (asset.AddonStates.ContainsKey(addonId))
                        {
                            asset.AddonStates.Remove(addonId);
                            assetModified = true;
                        }
                    }
                    
                    if (assetModified)
                    {
                        // Asset modified, will save configuration later
                    }
                }
                
                // 設定を保存
                await addonManager.SaveConfigurationAsync();
            }
            
            if (failedCount > 0)
            {
                // 部分的な成功の場合、失敗したアドオンに対してリトライオプションを提供
                var retryMessage = L.Format("UnsubscribeResult.PartialSuccess", successCount, failedCount) + 
                                  "\n\n" + L.Get("UnsubscribeResult.RetryQuestion");
                
                var retry = await dialogService.ShowConfirmAsync(
                    L.Get("UnsubscribeResult.Title"),
                    retryMessage);
                    
                if (retry)
                {
                    // 失敗したアドオンのWorkshopページを開く
                    foreach (var addonId in failedAddons)
                    {
                        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = url,
                                UseShellExecute = true
                            });
                            
                            // 複数のブラウザタブを開く際の負荷を軽減
                            await Task.Delay(500);
                        }
                        catch (Exception ex)
                        {
                            // URLを開けなかった場合は無視
                        }
                    }
                    
                    await dialogService.ShowInfoAsync(
                        L.Get("Info.Title"),
                        L.Get("UnsubscribeResult.OpenedWorkshopPages"));
                }
            }
            else
            {
                await dialogService.ShowInfoAsync(
                    L.Get("UnsubscribeResult.Title"),
                    L.Format("UnsubscribeResult.Success", successCount));
            }
            
            // 選択モードを解除
            IsSelectionMode = false;
            
            // リロード
            await ReloadAddons();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"), 
                L.Get("Error.UnsubscribeFailed"));
        }
    }
}