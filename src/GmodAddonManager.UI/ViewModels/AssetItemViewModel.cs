using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;
using System;
using System.ComponentModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GmodAddonManager.UI.ViewModels;

// アセットの状態を表す列挙型
public enum AssetState
{
    Enabled,   // 有効
    Disabled,  // 無効
    Excluded   // 除外
}

public class AssetItemViewModel : ViewModelBase, IDisposable
{
    private Asset asset;
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;

    private bool isSelected;
    private bool isEnabled;
    private int addonCount;
    private bool isSystem;
    private AssetState assetState;
    private bool isPublished;
    private bool autoUpdateEnabled;
    private bool isCurrent;

    public AssetItemViewModel(
        Asset asset, 
        AddonManager addonManager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher)
    {
        this.asset = asset;
        this.addonManager = addonManager;
        this.pendingChangeManager = pendingChangeManager;
        this.processWatcher = processWatcher;

        // 初期値設定
        Id = asset.Id;
        name = asset.Name;
        IsEnabled = asset.Enabled;
        IsSystem = asset.IsSystem;
        UpdateAddonCount();
        
        // アセットの状態を設定（DefaultAddonStateから）
        assetState = (AssetState)asset.DefaultAddonState;
        isPublished = !string.IsNullOrEmpty(asset.WorkshopCollectionId);
        autoUpdateEnabled = asset.AutoUpdateCollection;

        // コマンドの初期化
        ToggleEnabledCommand = ReactiveCommand.CreateFromTask(ToggleEnabledAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(
            DeleteAsync,
            this.WhenAnyValue(x => x.IsSystem, x => x.Id, (isSystem, id) => !isSystem && id != "subscribe-system-asset" && id != "junction-system-asset"));
        AddAddonsCommand = ReactiveCommand.CreateFromTask(ShowAddAddonsDialogAsync);
        ShowDetailsCommand = ReactiveCommand.CreateFromTask(ShowDetailsDialogAsync);
        SetEnabledCommand = ReactiveCommand.CreateFromTask(SetEnabledAsync);
        SetDisabledCommand = ReactiveCommand.CreateFromTask(SetDisabledAsync);
        SetExcludedCommand = ReactiveCommand.CreateFromTask(SetExcludedAsync);
        ShareCommand = ReactiveCommand.CreateFromTask(ShareAsync);
        ToggleAutoUpdateCommand = ReactiveCommand.CreateFromTask(ToggleAutoUpdateAsync);
        VersionManageCommand = ReactiveCommand.CreateFromTask(VersionManageAsync);
        ApplyExclusiveCommand = ReactiveCommand.CreateFromTask(ApplyExclusiveAsync);
        CleanupCommand = ReactiveCommand.CreateFromTask(
            ShowCleanupDialogAsync,
            this.WhenAnyValue(x => x.IsSystem, isSystem => !isSystem));
        
        // 言語変更を監視
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }
    
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "CurrentLanguage" || e.PropertyName == "")
        {
            // システムアセットの名前を更新
            if (IsSystem)
            {
                this.RaisePropertyChanged(nameof(Name));
            }
        }
    }

    public string Id { get; }
    
    private string name;
    public string Name 
    { 
        get
        {
            // システムアセットの名前をローカライズ
            if (IsSystem)
            {
                if (Id == "subscribe-system-asset")
                    return L.Get("Asset.Subscribe");
                else if (Id == "junction-system-asset")
                    return L.Get("Asset.Junction");
            }
            return name;
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set => SetAndRaise(ref isSelected, value);
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetAndRaise(ref isEnabled, value);
    }

    public int AddonCount
    {
        get => addonCount;
        private set => SetAndRaise(ref addonCount, value);
    }

    public bool IsSystem
    {
        get => isSystem;
        private set => SetAndRaise(ref isSystem, value);
    }
    
    // 削除ボタンを表示するかどうか
    public bool CanDelete => !IsSystem && Id != "subscribe-system-asset" && Id != "junction-system-asset";
    

    public ReactiveCommand<Unit, Unit> ToggleEnabledCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAddonsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> SetEnabledCommand { get; }
    public ReactiveCommand<Unit, Unit> SetDisabledCommand { get; }
    public ReactiveCommand<Unit, Unit> SetExcludedCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAutoUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> VersionManageCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyExclusiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CleanupCommand { get; }
    
    // バージョン表示
    public string VersionDisplay 
    {
        get
        {
            // インポートベースラインで現在バージョンが0の場合も特別な表示
            if (asset.CurrentVersion == 0 && asset.HasImportBaseline)
            {
                return "インポート前";
            }
            // インポートベースラインで現在バージョンが-1の場合は特別な表示（互換性のため）
            if (asset.CurrentVersion == -1 && asset.HasImportBaseline)
            {
                return "インポート前";
            }
            return $"v{asset.CurrentVersion}";
        }
    }
    
    // 状態プロパティ
    public bool IsEnabledState => assetState == AssetState.Enabled;
    public bool IsDisabledState => assetState == AssetState.Disabled;
    public bool IsExcludedState => assetState == AssetState.Excluded;
    
    // 公開状態プロパティ
    public bool IsPublished
    {
        get => isPublished;
        set => SetAndRaise(ref isPublished, value);
    }
    
    public bool AutoUpdateEnabled
    {
        get => autoUpdateEnabled;
        set => SetAndRaise(ref autoUpdateEnabled, value);
    }
    
    // 現在アクティブなアセットかどうか
    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            SetAndRaise(ref isCurrent, value);
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }
    
    // 枠の色（現在のアセット: 青、公開状態: 緑/赤）
    public string BorderColor
    {
        get
        {
            if (IsCurrent) return "#4A90E2"; // Blue for current asset
            if (!IsPublished) return "Transparent";
            return AutoUpdateEnabled ? "#4CAF50" : "#F44336"; // 緑 or 赤
        }
    }
    
    public string ShareButtonText => IsPublished ? L.Get("Asset.Share") : L.Get("Asset.Share");
    
    // 共有可能かどうか（Junctionアセット以外）
    public bool CanShare => Name != "Junction";
    public bool CanApplyExclusive => Id != "junction-system-asset";
    
    // 状態に応じた色
    public string AssetStateColor
    {
        get
        {
            return assetState switch
            {
                AssetState.Enabled => "#4CAF50",   // 緑
                AssetState.Disabled => "#FF9800",  // オレンジ
                AssetState.Excluded => "#F44336",  // 赤
                _ => "#9E9E9E"  // グレー
            };
        }
    }

    private async Task ToggleEnabledAsync()
    {
        try
        {
            if (processWatcher.IsGmodRunning)
            {
                // Gmodが実行中の場合は変更を保留
                pendingChangeManager.AddPendingChange(
                    IsEnabled ? "disable" : "enable",
                    Id
                );
                return;
            }

            // Steam起動中のHard無効化は再DLの可能性があるため確認（有効化/Soft無効化はそのまま続行）
            if (SteamProcessChecker.IsSteamRunningViaAPI() && IsEnabled && addonManager.DisableMode == DisableMode.Hard)
            {
                var dialogService = new DialogService();
                var result = await dialogService.ShowConfirmAsync(
                    L.Get("Warning.SteamRunningTitle"),
                    L.Get("Warning.SteamRunningDisable"));
                if (!result)
                {
                    return;
                }
            }

            // 即座に切り替え
            if (IsEnabled)
            {
                await addonManager.DisableAssetAsync(Id);
            }
            else
            {
                await addonManager.EnableAssetAsync(Id);
            }

            await addonManager.SaveConfigurationAsync();
            IsEnabled = !IsEnabled;
            asset.Enabled = IsEnabled;
        }
        catch (Exception)
        {
            throw;
        }
    }

    private async Task ApplyExclusiveAsync()
    {
        try
        {
            var dialogService = new DialogService();

            if (processWatcher.IsGmodRunning)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Warning.Title"),
                    L.Get("Warning.ApplyExclusiveWhileGmodRunning"));
                return;
            }

            if (SteamProcessChecker.IsSteamRunningViaAPI() && addonManager.DisableMode == DisableMode.Hard)
            {
                var confirmed = await dialogService.ShowConfirmAsync(
                    L.Get("Warning.SteamRunningTitle"),
                    L.Get("Warning.SteamRunningDisable"));
                if (!confirmed)
                {
                    return;
                }
            }

            var applyResult = await addonManager.ApplyAssetExclusiveAsync(Id);
            if (!applyResult.Success)
            {
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("Error.ApplyExclusiveFailed"));
            }

            ViewModelLocator.AssetListViewModel?.LoadAssets();
            await ReloadAddons();
        }
        catch (Exception)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.ApplyExclusiveFailed"));
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 空のアセットは確認なしで削除
            if (GetAddonIds().Count == 0)
            {
                addonManager.DeleteAsset(Id);
                await addonManager.SaveConfigurationAsync();
                await ReloadAddons();
                return;
            }
            
            var deleteDialog = new AssetDeleteDialog();
            var mainWindow = await GetMainWindow();
            
            if (mainWindow != null)
            {
                await deleteDialog.ShowDialog<AssetDeleteDialog.DeleteOption>(mainWindow);
                
                switch (deleteDialog.Result)
                {
                    case AssetDeleteDialog.DeleteOption.DeleteAssetOnly:
                        // アセットのみを削除（中身は無視）
                        addonManager.DeleteAsset(Id);
                        await addonManager.SaveConfigurationAsync();
                        await ReloadAddons();
                        break;
                        
                    case AssetDeleteDialog.DeleteOption.MoveToOther:
                        // 一次確認
                        var moveConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 
                            L.Format("Confirm.MoveAssetsToOther", Name));
                        
                        if (moveConfirmed)
                        {
                            // 移動先のアセットを選択（通常の選択と同じロジック）
                            var assetListVm = ViewModelLocator.AssetListViewModel;
                            
                            if (assetListVm == null)
                            {
                                return;
                            }
                            
                            // 全アセットリストを作成（サブスクライブとジャンクションを含む）
                            var allAssets = new List<AssetItemViewModel>();
                            allAssets.AddRange(assetListVm.Assets);
                            allAssets.AddRange(assetListVm.JunctionAsset);
                            
                            // 現在のアセット以外をフィルタ
                            allAssets = allAssets.Where(a => a.Id != Id).ToList();
                            
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
                                await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoDestinationAssets"));
                                return;
                            }
                            
                            var assetSelectionDialog = new AssetSelectionDialog(sortedAssets);
                            var selectedAsset = await assetSelectionDialog.ShowDialog<AssetItemViewModel>(mainWindow);
                            
                            if (selectedAsset != null)
                            {
                                // 二次確認
                                var moveConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
                                    L.Format("Confirm.MoveAddonsFinal", GetAddonIds().Count, selectedAsset.Name));
                                
                                if (moveConfirmed2)
                                {
                                    // アドオンを別のアセットに移動（状態は保持しない）
                                    var addonIds = GetAddonIds();
                                    
                                    foreach (var addonId in addonIds)
                                    {
                                        selectedAsset.AddAddon(addonId); // デフォルト状態で追加
                                    }
                                    
                                    // 元のアセットを削除
                                    addonManager.DeleteAsset(Id);
                                    await addonManager.SaveConfigurationAsync();
                                    
                                    await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                        L.Format("Success.MovedAddonsToAsset", addonIds.Count, selectedAsset.Name));
                                    
                                    
                                    // リロード処理
                                    await ReloadAddons();
                                }
                            }
                        }
                        break;
                        
                    case AssetDeleteDialog.DeleteOption.DeleteWithContents:
                        // 一次確認
                        var deleteConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 
                            L.Format("Confirm.DeleteAssetWithContents", Name));
                        
                        if (deleteConfirmed)
                        {
                            // 二次確認
                            var deleteConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
                                L.Format("Confirm.DeleteAssetFinal", Name));
                            
                            if (deleteConfirmed2)
                            {
                                addonManager.DeleteAsset(Id);
                                await addonManager.SaveConfigurationAsync();
                                
                                // リロード処理
                                await ReloadAddons();
                            }
                        }
                        break;
                        
                    case AssetDeleteDialog.DeleteOption.DisableAddons:
                        // 一次確認
                        var disableConfirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 
                            L.Format("Confirm.DisableAllAddons", Name));
                        
                        if (disableConfirmed)
                        {
                            // 二次確認
                            var addonCount = GetAddonIds().Count;
                            var disableConfirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
                                L.Format("Confirm.DisableAddonsFinal", addonCount));
                            
                            if (disableConfirmed2)
                            {
                                // アセットを無効化（ジャンクションを削除）
                                if (processWatcher.IsGmodRunning)
                                {
                                    pendingChangeManager.AddPendingChange("disable", Id);
                                    await dialogService.ShowInfoAsync(L.Get("Info.Title"), 
                                        L.Get("Info.DisableAfterGmodExit"));
                                }
                                else
                                {
                                    await addonManager.DisableAssetAsync(Id);
                                    await addonManager.SaveConfigurationAsync();
                                    
                                    // アセットを削除
                                    addonManager.DeleteAsset(Id);
                                    await addonManager.SaveConfigurationAsync();
                                    
                                    await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                        L.Format("Success.DisabledAddons", addonCount));
                                    
                                    // リロード処理
                                    await ReloadAddons();
                                }
                                
                            }
                        }
                        break;
                        
                    case AssetDeleteDialog.DeleteOption.Cancel:
                    default:
                        // キャンセル
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetDeleteFailed"));
        }
    }

    public void AddAddon(string addonId, AddonState state = AddonState.Enabled)
    {
        try
        {
            addonManager.AddAddonToAsset(Id, addonId, state);
            UpdateAddonCount();
            
            // 自動更新がONの場合、コレクションを更新
            if (AutoUpdateEnabled && !string.IsNullOrEmpty(asset.WorkshopCollectionId))
            {
                _ = UpdateCollectionAsync();
            }
        }
        catch (Exception)
        {
            throw;
        }
    }

    public void RemoveAddon(string addonId)
    {
        try
        {
            addonManager.RemoveAddonFromAsset(Id, addonId);
            UpdateAddonCount();
            
            // 自動更新がONの場合、コレクションを更新
            if (AutoUpdateEnabled && !string.IsNullOrEmpty(asset.WorkshopCollectionId))
            {
                _ = UpdateCollectionAsync();
            }
        }
        catch (Exception)
        {
            throw;
        }
    }

    public List<string> GetAddonIds()
    {
        // ContainsAllAddons の場合は実際の全アドオンIDを返す
        if (asset.ContainsAllAddons())
        {
            var allAddons = addonManager.GetAllAddons();
            if (allAddons != null)
            {
                return allAddons.Keys.ToList();
            }
            else
            {
                return new List<string>();
            }
        }
        else
        {
            // *を除外して返す（念のため）
            return asset.Addons.Where(id => id != "*").ToList();
        }
    }
    
    public Dictionary<string, AddonState> GetAddonStates()
    {
        return new Dictionary<string, AddonState>(asset.AddonStates);
    }
    
    public AddonState GetAddonState(string addonId)
    {
        return asset.GetAddonState(addonId);
    }
    
    public void SetAddonState(string addonId, AddonState state)
    {
        try
        {
            addonManager.SetAddonState(Id, addonId, state);
        }
        catch (Exception)
        {
            throw;
        }
    }

    private void UpdateAddonCount()
    {
        if (asset.ContainsAllAddons())
        {
            // 全アドオンを含む場合は、実際の全アドオン数を表示
            var allAddons = addonManager.GetAllAddons();
            if (allAddons != null)
            {
                AddonCount = allAddons.Count;
            }
            else
            {
                AddonCount = 0;
            }
        }
        else
        {
            AddonCount = asset.Addons.Count;
        }
    }

    public void RefreshFromModel(Asset updatedAsset)
    {
        // アセットモデルを更新
        this.asset = updatedAsset;
        
        IsEnabled = updatedAsset.Enabled;
        assetState = (AssetState)updatedAsset.DefaultAddonState;
        UpdateAddonCount();
        
        // 状態プロパティの変更を通知
        this.RaisePropertyChanged(nameof(IsEnabledState));
        this.RaisePropertyChanged(nameof(IsDisabledState));
        this.RaisePropertyChanged(nameof(IsExcludedState));
        this.RaisePropertyChanged(nameof(AssetStateColor));
    }

    private async Task ShowAddAddonsDialogAsync()
    {
        try
        {
            // 直接URL入力ダイアログを表示
            await ShowUrlInputDialog();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.DialogShowFailed"));
        }
    }

    private async Task ShowUrlInputDialog()
    {
        var dialogService = new DialogService();
        var urlInputDialog = new UrlInputDialog();
        var mainWindow = await GetMainWindow();
        
        if (mainWindow != null)
        {
            var urls = await urlInputDialog.ShowDialog<List<string>>(mainWindow);
            
            if (urls != null && urls.Count > 0)
            {
                var workshopIds = new List<string>();
                foreach (var url in urls)
                {
                    var workshopId = SteamUrlParser.ExtractWorkshopId(url);
                    if (!string.IsNullOrEmpty(workshopId))
                    {
                        workshopIds.Add(workshopId);
                    }
                }
                
                if (workshopIds.Count > 0)
                {
                    // Steamworks Managerを取得
                    var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
                    
                    // Steamworksが初期化されていない場合はエラー
                    if (steamworksManager == null || !steamworksManager.IsInitialized)
                    {
                        var errorMessage = "Steamworks APIが初期化されていません。\n\n" +
                                         "Workshopアドオンのサブスクライブ機能を使用するには、以下の手順で実行してください：\n\n" +
                                         "1. Steamが起動していることを確認\n" +
                                         "2. Garry's ModがSteamにインストールされていることを確認\n" +
                                         "3. GAMを以下のいずれかの方法で起動：\n" +
                                         "   - launch_gam_with_steam.bat を使用（推奨）\n" +
                                         "   - GAMを非Steamゲームとして追加し、起動オプションに +app_id 4000 を設定\n\n" +
                                         "詳細はコンソールログを確認してください。";
                        await dialogService.ShowErrorAsync("Steamworks初期化エラー", errorMessage);
                        return;
                    }
                    
                    
                    // サブスクライブが必要なアドオンをチェック
                    var itemsToSubscribe = new List<string>();
                    var existingItems = new List<string>();
                    
                    
                    foreach (var workshopId in workshopIds)
                    {
                        // ローカルに存在するかチェック
                        var allAddons = addonManager.GetAllAddons();
                        var exists = false;
                        
                        // 既にアセットに含まれているかチェック
                        if (GetAddonIds().Contains(workshopId))
                        {
                            continue; // 既にアセットに含まれている場合はスキップ
                        }
                        
                        if (allAddons != null)
                        {
                            foreach (var kvp in allAddons)
                            {
                                if (kvp.Value.Id == workshopId)
                                {
                                    exists = true;
                                    existingItems.Add(workshopId);
                                    break;
                                }
                            }
                        }
                        
                        if (!exists)
                        {
                            itemsToSubscribe.Add(workshopId);
                        }
                    }
                    
                    
                    // サブスクライブが必要な場合
                    if (itemsToSubscribe.Count > 0)
                    {
                        var progressDialog = new Avalonia.Controls.Window
                        {
                            Title = L.Get("Progress.Subscribing"),
                            Width = 400,
                            Height = 150,
                            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                            CanResize = false
                        };
                        
                        var progressPanel = new Avalonia.Controls.StackPanel
                        {
                            Margin = new Avalonia.Thickness(20),
                            Spacing = 10
                        };
                        
                        var progressText = new Avalonia.Controls.TextBlock
                        {
                            Text = L.Get("Progress.SubscribingAddons")
                        };
                        progressPanel.Children.Add(progressText);
                        
                        var progressBar = new Avalonia.Controls.ProgressBar
                        {
                            Minimum = 0,
                            Maximum = itemsToSubscribe.Count,
                            Height = 20
                        };
                        progressPanel.Children.Add(progressBar);
                        
                        progressDialog.Content = progressPanel;
                        
                        _ = progressDialog.ShowDialog(mainWindow);
                        
                        // プログレス報告付きでサブスクライブ
                        var progress = new System.Progress<(int current, int total)>(p =>
                        {
                            progressBar.Value = p.current;
                            progressText.Text = L.Format("Progress.SubscribingProgress", p.current, p.total);
                        });
                        
                        var subscribeResults = await steamworksManager.SubscribeItemsBatchAsync(itemsToSubscribe, progress);
                        
                        
                        progressDialog.Close();
                        
                        // サブスクライブ成功したアイテムを既存リストに追加
                        foreach (var kvp in subscribeResults)
                        {
                            if (kvp.Value)
                            {
                                existingItems.Add(kvp.Key);
                            }
                        }
                        
                        // 新規アドオンチェックウィンドウを表示して更新を待つ
                        if (subscribeResults.Any(r => r.Value))
                        {
                            var checkWindow = new NewAddonCheckWindow(addonManager);
                            await checkWindow.ShowDialog(mainWindow);
                            
                            // チェックウィンドウが閉じた後、再度アドオンリストを更新
                            await addonManager.ScanForNewAddonsAsync();
                            
                            // サブスクライブしたアドオンを再度チェック
                            foreach (var workshopId in subscribeResults.Where(r => r.Value).Select(r => r.Key))
                            {
                                var allAddons = addonManager.GetAllAddons();
                                if (allAddons != null)
                                {
                                    foreach (var kvp in allAddons)
                                    {
                                        if (kvp.Value.Id == workshopId && !existingItems.Contains(workshopId))
                                        {
                                            existingItems.Add(workshopId);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // アセットにアドオンを追加（既存のアドオンも含む）
                    var addedCount = 0;
                    foreach (var workshopId in existingItems)
                    {
                        try
                        {
                            AddAddon(workshopId);
                            addedCount++;
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                    
                    // サブスクライブ対象がなくても、既存のアドオンがあれば処理を続行
                    if (itemsToSubscribe.Count == 0 && existingItems.Count > 0)
                    {
                    }
                    
                    if (addedCount > 0)
                    {
                        
                        await addonManager.SaveConfigurationAsync();
                        RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == Id));
                        
                        await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                            addedCount == 1 
                                ? L.Get("Success.AddedOneAddon") 
                                : L.Format("Success.AddedAddons", addedCount));
                        
                        // リロード処理
                        await ReloadAddons();
                    }
                    else
                    {
                        // デバッグ情報を含めたメッセージ
                        var message = "追加可能なアドオンが見つかりませんでした。\n\n";
                        if (existingItems.Count > 0)
                        {
                            message += $"既にローカルに存在するアドオン: {existingItems.Count}個\n";
                        }
                        if (itemsToSubscribe.Count > 0)
                        {
                            message += $"サブスクライブが必要なアドオン: {itemsToSubscribe.Count}個\n";
                            message += "サブスクライブ処理に失敗したか、ダウンロードが完了していない可能性があります。\n";
                            message += "Steamのダウンロードページで進行状況を確認してください。";
                        }
                        else if (workshopIds.Count > 0)
                        {
                            message += "入力されたURLのアドオンは既にこのアセットに含まれているか、無効なIDです。";
                        }
                        
                        await dialogService.ShowWarningAsync(L.Get("Warning.Title"), message);
                    }
                }
            }
        }
    }


    private async Task<Avalonia.Controls.Window?> GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }
    
    private async Task SetEnabledAsync()
    {
        try
        {
            if (assetState != AssetState.Enabled)
            {
                // サブスクライブアセットの場合は2段チェック
                if (Id == "subscribe-system-asset")
                {
                    var dialogService = new DialogService();
                    
                    // 1段目確認
                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),
                        L.Get("Confirm.EnableSubscribeAsset"));
                    
                    if (!confirmed1)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                    
                    // 2段目確認
                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),
                        L.Get("Confirm.EnableSubscribeAssetFinal"));
                    
                    if (!confirmed2)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                }
                
                assetState = AssetState.Enabled;
                
                // アセット自体を有効化（これが内部で全アドオンの状態を更新する）
                await addonManager.EnableAssetAsync(Id);
                
                // DefaultAddonStateを更新
                asset.DefaultAddonState = AddonState.Enabled;
                
                // 設定を保存
                await addonManager.SaveConfigurationAsync();
                
                // バックグラウンドで個別のアドオン状態を設定
                var addonIds = GetAddonIds();
                await Task.Run(() =>
                {
                    foreach (var addonId in addonIds)
                    {
                        addonManager.SetAddonState(Id, addonId, AddonState.Enabled);
                    }
                });
                
                // 状態プロパティを更新
                this.RaisePropertyChanged(nameof(IsEnabledState));
                this.RaisePropertyChanged(nameof(IsDisabledState));
                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));
                
                
                // アドオン一覧を更新
                await UpdateAddonGridAsync();
            }
        }
        catch (Exception ex)
        {
        }
    }
    
    private async Task SetDisabledAsync()
    {
        try
        {
            if (assetState != AssetState.Disabled)
            {
                // サブスクライブアセットの場合は2段チェック
                if (Id == "subscribe-system-asset")
                {
                    var dialogService = new DialogService();
                    
                    // 1段目確認
                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),
                        L.Get("Confirm.DisableSubscribeAsset"));
                    
                    if (!confirmed1)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                    
                    // 2段目確認
                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),
                        L.Get("Confirm.DisableSubscribeAssetFinal"));
                    
                    if (!confirmed2)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                }
                
                assetState = AssetState.Disabled;
                
                // アセット自体を無効化（これが内部で全アドオンの状態を更新する）
                await addonManager.DisableAssetAsync(Id);
                
                // DefaultAddonStateを更新
                asset.DefaultAddonState = AddonState.Disabled;
                
                // 設定を保存
                await addonManager.SaveConfigurationAsync();
                
                // バックグラウンドで個別のアドオン状態を設定
                var addonIds = GetAddonIds();
                await Task.Run(() =>
                {
                    foreach (var addonId in addonIds)
                    {
                        addonManager.SetAddonState(Id, addonId, AddonState.Disabled);
                    }
                });
                
                // 状態プロパティを更新
                this.RaisePropertyChanged(nameof(IsEnabledState));
                this.RaisePropertyChanged(nameof(IsDisabledState));
                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));
                
                
                // アドオン一覧を更新
                await UpdateAddonGridAsync();
            }
        }
        catch (Exception ex)
        {
        }
    }
    
    private async Task SetExcludedAsync()
    {
        try
        {
            if (assetState != AssetState.Excluded)
            {
                // サブスクライブアセットの場合は2段チェック
                if (Id == "subscribe-system-asset")
                {
                    var dialogService = new DialogService();
                    
                    // 1段目確認
                    var confirmed1 = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"),
                        L.Get("Confirm.ExcludeSubscribeAsset"));
                    
                    if (!confirmed1)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                    
                    // 2段目確認
                    var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"),
                        L.Get("Confirm.ExcludeSubscribeAssetFinal"));
                    
                    if (!confirmed2)
                    {
                        // 状態プロパティを強制的に更新して元に戻す
                        this.RaisePropertyChanged(nameof(IsEnabledState));
                        this.RaisePropertyChanged(nameof(IsDisabledState));
                        this.RaisePropertyChanged(nameof(IsExcludedState));
                        return;
                    }
                }
                
                assetState = AssetState.Excluded;
                
                // アセット自体は有効化（除外状態でも有効）
                await addonManager.EnableAssetAsync(Id);
                
                // DefaultAddonStateを更新
                asset.DefaultAddonState = AddonState.Excluded;
                
                // 設定を保存
                await addonManager.SaveConfigurationAsync();
                
                // バックグラウンドで個別のアドオン状態を設定
                var addonIds = GetAddonIds();
                await Task.Run(() =>
                {
                    foreach (var addonId in addonIds)
                    {
                        addonManager.SetAddonState(Id, addonId, AddonState.Excluded);
                    }
                });
                
                // 状態プロパティを更新
                this.RaisePropertyChanged(nameof(IsEnabledState));
                this.RaisePropertyChanged(nameof(IsDisabledState));
                this.RaisePropertyChanged(nameof(IsExcludedState));
                this.RaisePropertyChanged(nameof(AssetStateColor));
                
                
                // アドオン一覧を更新
                await UpdateAddonGridAsync();
            }
        }
        catch (Exception ex)
        {
        }
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
        }
    }
    
    private async Task UpdateAddonGridAsync()
    {
        try
        {
            // AddonGridViewModelのフィルタを再適用
            var addonGridVm = ViewModelLocator.AddonGridViewModel;
            if (addonGridVm != null)
            {
                addonGridVm.ApplyFilter();
            }
        }
        catch (Exception ex)
        {
        }
    }
    
    private async Task ShowDetailsDialogAsync()
    {
        try
        {
            var detailsDialog = new AssetDetailsDialog();
            detailsDialog.SetAsset(this, addonManager);
            
            var mainWindow = await GetMainWindow();
            if (mainWindow != null)
            {
                await detailsDialog.ShowDialog(mainWindow);
                
                // ダイアログを閉じた後、変更を反映
                RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == Id));
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.DetailsDialogFailed"));
        }
    }
    
    private async Task ShareAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            if (!IsPublished)
            {
                // 新規公開
                var addonIds = GetAddonIds();
                if (addonIds.Count == 0)
                {
                    await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoAddonsToShare"));
                    return;
                }
                
                // 100個以下はWorkshop+GAM、101個以上はGAMのみ
                if (addonIds.Count <= 100)
                {
                    // ShareCollectionDialogを表示
                    var shareDialog = new ShareCollectionDialog();
                    shareDialog.SetAddonCount(addonIds.Count);
                    
                    var mainWindow = await GetMainWindow();
                    if (mainWindow != null)
                    {
                        await shareDialog.ShowDialog(mainWindow);
                        
                        if (shareDialog.DialogResult)
                        {
                            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
                            if (steamworksManager != null && steamworksManager.IsInitialized)
                            {
                                // Workshopコレクション作成
                                await CreateSingleCollection(steamworksManager, shareDialog.CollectionTitle, 
                                    shareDialog.CollectionDescription, addonIds, shareDialog.OpenLinkAfterCreation);
                                
                                // GAM形式でも保存
                                await ExportToGamFormatAsync(shareDialog.CollectionTitle, shareDialog.CollectionDescription, addonIds);
                            }
                            else
                            {
                                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.SteamworksNotInitialized"));
                            }
                        }
                    }
                }
                else
                {
                    // 101個以上はGAM形式のみ
                    await ShowGamExportDialogAsync(addonIds);
                }
            }
            else
            {
                // 公開中 - 公開ページを開くか確認
                var confirmed = await dialogService.ShowConfirmAsync(
                    L.Get("Asset.OpenWorkshopPage.Title"), 
                    L.Get("Asset.OpenWorkshopPage.Message"));
                
                if (confirmed)
                {
                    var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
                    if (steamworksManager != null && asset.WorkshopCollectionId != null)
                    {
                        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={asset.WorkshopCollectionId}";
                        steamworksManager.OpenWorkshopPage(url);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.ShareFailed"));
        }
    }
    
    private async Task ToggleAutoUpdateAsync()
    {
        try
        {
            AutoUpdateEnabled = !AutoUpdateEnabled;
            asset.AutoUpdateCollection = AutoUpdateEnabled;
            
            await addonManager.SaveConfigurationAsync();
            
            this.RaisePropertyChanged(nameof(BorderColor));
            
            // 自動更新がONになった場合、即座に更新
            if (AutoUpdateEnabled && !string.IsNullOrEmpty(asset.WorkshopCollectionId))
            {
                await UpdateCollectionAsync();
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AutoUpdateToggleFailed"));
        }
    }
    
    public async Task UpdateCollectionAsync()
    {
        if (string.IsNullOrEmpty(asset.WorkshopCollectionId)) return;
        
        try
        {
            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
            if (steamworksManager != null && steamworksManager.IsInitialized)
            {
                var addonIds = GetAddonIds();
                var success = await steamworksManager.UpdateCollectionAsync(asset.WorkshopCollectionId, addonIds, "Automatic update from GAM");
                
                if (!success)
                {
                    // コレクションが削除されている可能性
                    var exists = await steamworksManager.CheckCollectionExistsAsync(asset.WorkshopCollectionId);
                    if (!exists)
                    {
                        // コレクションが削除されている
                        asset.WorkshopCollectionId = null;
                        asset.AutoUpdateCollection = false;
                        IsPublished = false;
                        AutoUpdateEnabled = false;
                        
                        await addonManager.SaveConfigurationAsync();
                        
                        this.RaisePropertyChanged(nameof(ShareButtonText));
                        this.RaisePropertyChanged(nameof(BorderColor));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // エラーはログに記録するが、UIには表示しない（自動更新のため）
        }
    }
    
    // 単一コレクション作成（1000個以下）
    private async Task CreateSingleCollection(SteamworksManager steamworksManager, string title, string description, 
        List<string> addonIds, bool openLink)
    {
        var dialogService = new DialogService();
        var progressDialog = new CollectionUploadProgressDialog();
        var cts = new CancellationTokenSource();
        progressDialog.SetCancellationTokenSource(cts);
        
        var mainWindow = await GetMainWindow();
        if (mainWindow == null) return;
        
        progressDialog.Show(mainWindow);
        
        try
        {
            progressDialog.UpdateStatus("コレクションを作成中...");
            var collectionId = await steamworksManager.CreateCollectionAsync(title, description);
            
            if (!string.IsNullOrEmpty(collectionId))
            {
                progressDialog.UpdateStatus("初回のアドオンを追加中...");
                
                // プログレス報告用
                var progress = new Progress<(int current, int total)>((p) =>
                {
                    progressDialog.UpdateBatchProgress(1, 1, p.current, p.total);
                    progressDialog.UpdateDetail($"{p.current}/{p.total} 個のアドオンを追加中...");
                });
                
                // 全てのアドオンを一度に追加
                var success = await steamworksManager.UpdateCollectionAsync(collectionId, addonIds, 
                    "Initial collection creation", progress, cts.Token, true);
                
                if (success)
                {
                    asset.WorkshopCollectionId = collectionId;
                    asset.AutoUpdateCollection = true;
                    IsPublished = true;
                    AutoUpdateEnabled = true;
                    
                    await addonManager.SaveConfigurationAsync();
                    
                    this.RaisePropertyChanged(nameof(ShareButtonText));
                    this.RaisePropertyChanged(nameof(BorderColor));
                    
                    progressDialog.Close();
                    
                    if (openLink)
                    {
                        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={collectionId}";
                        steamworksManager.OpenWorkshopPage(url);
                    }
                }
                else
                {
                    progressDialog.ShowError("コレクションの更新に失敗しました。");
                }
            }
            else
            {
                progressDialog.ShowError("コレクションの作成に失敗しました。");
            }
        }
        catch (Exception ex)
        {
            progressDialog.ShowError("エラーが発生しました。詳細はログを確認してください。");
        }
    }
    
    // GAMエクスポートダイアログを表示
    private async Task ShowGamExportDialogAsync(List<string> addonIds)
    {
        var gamExportDialog = new GamExportDialog();
        gamExportDialog.SetAddonIds(addonIds);
        
        var mainWindow = await GetMainWindow();
        if (mainWindow != null)
        {
            await gamExportDialog.ShowDialog(mainWindow);
            
            if (gamExportDialog.DialogResult)
            {
                var dialogService = new DialogService();
                await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                    $"GAM形式でエクスポートしました。\n保存先: {gamExportDialog.SavePath}");
            }
        }
    }
    
    // GAM形式でエクスポート（Workshop作成後の追加保存用）
    private async Task ExportToGamFormatAsync(string title, string description, List<string> addonIds)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var fileName = $"collection_{DateTime.Now:yyyyMMdd_HHmmss}.gam";
            var savePath = Path.Combine(desktopPath, fileName);
            
            var lines = new List<string>
            {
                "# GAM Collection Export v1",
                $"# Title: {title}",
                $"# Description: {description}",
                $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"# Count: {addonIds.Count}",
                ""
            };
            
            lines.AddRange(addonIds);
            
            await File.WriteAllLinesAsync(savePath, lines);
        }
        catch (Exception ex)
        {
            // エラーが発生してもWorkshop作成は成功しているのでログのみ記録
        }
    }
    
    // 複数コレクション作成（1000個超）
    private async Task CreateMultipleCollections(SteamworksManager steamworksManager, string baseTitle, string description, 
        List<string> allAddonIds, bool openLink)
    {
        var dialogService = new DialogService();
        var progressDialog = new CollectionUploadProgressDialog();
        var cts = new CancellationTokenSource();
        progressDialog.SetCancellationTokenSource(cts);
        
        var mainWindow = await GetMainWindow();
        if (mainWindow == null) return;
        
        progressDialog.Show(mainWindow);
        
        try
        {
            const int maxPerCollection = 1000;
            int collectionCount = (allAddonIds.Count + maxPerCollection - 1) / maxPerCollection;
            var createdCollectionIds = new List<string>();
            
            progressDialog.UpdateTotalProgress(0, allAddonIds.Count);
            
            for (int collectionIndex = 0; collectionIndex < collectionCount; collectionIndex++)
            {
                if (cts.Token.IsCancellationRequested) break;
                
                // このコレクション用のアドオンを取得
                var startIdx = collectionIndex * maxPerCollection;
                var addonIds = allAddonIds.Skip(startIdx).Take(maxPerCollection).ToList();
                
                // コレクション名を生成
                var collectionTitle = collectionCount > 1 ? $"{baseTitle} ({collectionIndex + 1})" : baseTitle;
                
                progressDialog.UpdateStatus($"コレクション {collectionIndex + 1}/{collectionCount} を作成中...");
                var collectionId = await steamworksManager.CreateCollectionAsync(collectionTitle, description);
                
                if (!string.IsNullOrEmpty(collectionId))
                {
                    progressDialog.UpdateStatus($"コレクション {collectionIndex + 1}/{collectionCount} にアドオンを追加中...");
                    
                    // プログレス報告用
                    var progress = new Progress<(int current, int total)>((p) =>
                    {
                        int batchNumber = (p.current - 1) / 100 + 1;
                        int totalBatches = (p.total + 99) / 100;
                        int itemsInBatch = p.current % 100;
                        if (itemsInBatch == 0) itemsInBatch = 100;
                        
                        progressDialog.UpdateBatchProgress(batchNumber, totalBatches, itemsInBatch, 100);
                        progressDialog.UpdateTotalProgress(startIdx + p.current, allAddonIds.Count);
                        progressDialog.UpdateDetail($"全体: {startIdx + p.current}/{allAddonIds.Count} 個のアドオンを追加中...");
                    });
                    
                    var success = await steamworksManager.UpdateCollectionAsync(collectionId, addonIds, null, progress, cts.Token);
                    
                    if (success)
                    {
                        createdCollectionIds.Add(collectionId);
                        
                        // 最初のコレクションIDを保存
                        if (collectionIndex == 0)
                        {
                            asset.WorkshopCollectionId = collectionId;
                            asset.AutoUpdateCollection = false; // 複数コレクションの場合は自動更新OFF
                            IsPublished = true;
                            AutoUpdateEnabled = false;
                            
                            await addonManager.SaveConfigurationAsync();
                            
                            this.RaisePropertyChanged(nameof(ShareButtonText));
                            this.RaisePropertyChanged(nameof(BorderColor));
                        }
                    }
                    else
                    {
                        progressDialog.ShowError($"コレクション {collectionIndex + 1} の更新に失敗しました。");
                        return;
                    }
                }
                else
                {
                    progressDialog.ShowError($"コレクション {collectionIndex + 1} の作成に失敗しました。");
                    return;
                }
            }
            
            progressDialog.Close();
            
            // 作成されたコレクションのリンクを表示
            if (openLink && createdCollectionIds.Count > 0)
            {
                var message = $"{createdCollectionIds.Count}個のコレクションが作成されました。\n\n";
                for (int i = 0; i < createdCollectionIds.Count; i++)
                {
                    message += $"• {baseTitle} ({i + 1})\n";
                }
                message += "\n最初のコレクションを開きますか？";
                
                var confirmed = await dialogService.ShowConfirmAsync("コレクション作成完了", message);
                if (confirmed)
                {
                    var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={createdCollectionIds[0]}";
                    steamworksManager.OpenWorkshopPage(url);
                }
            }
        }
        catch (Exception ex)
        {
            progressDialog.ShowError("エラーが発生しました。詳細はログを確認してください。");
        }
    }
    
    // バージョン管理
    private async Task VersionManageAsync()
    {
        try
        {
            var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            if (window?.MainWindow != null)
            {
                // v0の場合は保存ダイアログを表示（インポートベースラインがある場合を除く）
                if (asset.CurrentVersion == 0 && !asset.HasImportBaseline)
                {
                    var saveDialog = new SaveVersionDialog
                    {
                        AssetName = asset.Name
                    };
                    
                    await saveDialog.ShowDialog(window.MainWindow);
                    
                    if (saveDialog.IsSaved)
                    {
                        // v1を作成
                        var newVersion = new AssetVersion
                        {
                            Version = 1,
                            CreatedAt = DateTime.Now,
                            AddonIds = new List<string>(asset.Addons),
                            IncludeAddonStates = saveDialog.IncludeAddonStates
                        };
                        
                        // GAM形式のコンテンツを生成
                        var gamLines = new List<string>
                        {
                            "# GAM Collection Export v1",
                            $"# Title: {asset.Name} v1",
                            $"# Description: Version 1 of {asset.Name}",
                            $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                            $"# Count: {asset.Addons.Count}",
                            ""
                        };
                        gamLines.AddRange(asset.Addons);
                        newVersion.GamContent = string.Join("\n", gamLines);
                        
                        // アドオン状態を保存する場合
                        if (saveDialog.IncludeAddonStates)
                        {
                            newVersion.AddonStates = new Dictionary<string, AddonState>(asset.AddonStates);
                        }
                        
                        // バージョン履歴に追加
                        asset.VersionHistory.Add(newVersion);
                        asset.CurrentVersion = 1;
                        
                        // 設定を保存
                        await addonManager.SaveConfigurationAsync();
                        
                        // UIを更新
                        RefreshFromModel(asset);
                        
                        var dialogService = new DialogService();
                        await dialogService.ShowInfoAsync("保存完了", "v1として保存しました。");
                        
                        // メインウィンドウを再読み込み
                        await ReloadAddons();
                    }
                }
                else
                {
                    // v1以降の場合は通常通りバージョン管理画面を開く
                    await ShowVersionManagementWindowAsync();
                }
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync("エラー", "バージョン管理でエラーが発生しました。");
        }
    }
    
    private async Task CreateNewVersionAsync(bool includeAddonStates)
    {
        try
        {
            // 新しいバージョンを作成
            var newVersionNumber = asset.CurrentVersion + 1;
            var newVersion = new AssetVersion
            {
                Version = newVersionNumber,
                CreatedAt = DateTime.Now,
                AddonIds = new List<string>(GetAddonIds()),
                IncludeAddonStates = includeAddonStates
            };
            
            // アドオン状態を保存する場合
            if (includeAddonStates)
            {
                newVersion.AddonStates = new Dictionary<string, AddonState>(asset.AddonStates);
            }
            
            // バージョン履歴に追加
            asset.VersionHistory.Add(newVersion);
            asset.CurrentVersion = newVersionNumber;
            
            // 設定を保存
            await addonManager.SaveConfigurationAsync();
            
            // UIを更新
            this.RaisePropertyChanged(nameof(VersionDisplay));
            
            var dialogService = new DialogService();
            await dialogService.ShowInfoAsync(
                "バージョン作成完了",
                $"v{newVersionNumber}として保存しました。"
            );
        }
        catch (Exception ex)
        {
            throw;
        }
    }
    
    private async Task ShowVersionManagementWindowAsync()
    {
        var mainWindow = await GetMainWindow();
        if (mainWindow != null)
        {
            // 最新のアセット状態を取得（アドオン追加・削除後の状態を反映）
            var config = addonManager.GetConfiguration();
            var latestAsset = config.Assets.FirstOrDefault(a => a.Id == asset.Id);
            if (latestAsset != null)
            {
                asset = latestAsset; // 最新のアセット情報に更新
            }
            
            var dialog = new VersionManagementDialog(asset, addonManager);
            await dialog.ShowDialog(mainWindow);
            
            // ダイアログが閉じられたらUIを更新
            this.RaisePropertyChanged(nameof(VersionDisplay));
        }
    }
    
    private async Task ShowCleanupDialogAsync()
    {
        try
        {
            var mainWindow = await GetMainWindow();
            if (mainWindow != null)
            {
                var dialog = new AssetCleanupDialog(asset, addonManager);
                await dialog.ShowDialog(mainWindow);
                
                // クリーンアップ後はアドオンリストを再読み込み
                if (dialog.HasChanges)
                {
                    await ReloadAddons();
                }
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.CleanupFailed"));
        }
    }
    
    #region IDisposable Support
    private bool disposedValue = false;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // Unsubscribe from event handler to prevent memory leak
                LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    #endregion
}
