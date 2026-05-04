using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using System.Timers;
using GmodAddonManager.UI.Models;

namespace GmodAddonManager.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly PendingChangeManager pendingChangeManager;

    private string searchText = "";
    private AssetListViewModel assetListViewModel;
    private AddonGridViewModel addonGridViewModel;
    private StatusBarViewModel statusBarViewModel;
    private bool canUndo;
    private string addonStatistics = "";
    private bool isDisableManifestImportEnabled;
    private bool isInitialized = false;
    private Timer? autoUpdateTimer;

    public MainWindowViewModel(
        AddonManager addonManager, 
        GmodProcessWatcher processWatcher,
        PendingChangeManager pendingChangeManager)
    {
        this.addonManager = addonManager;
        this.processWatcher = processWatcher;
        this.pendingChangeManager = pendingChangeManager;

        // ViewModelの初期化
        assetListViewModel = new AssetListViewModel(
            addonManager, pendingChangeManager, processWatcher);
        addonGridViewModel = new AddonGridViewModel(addonManager, pendingChangeManager, processWatcher);
        statusBarViewModel = new StatusBarViewModel(
            processWatcher, pendingChangeManager);
        
        // ViewModelLocatorに設定
        ViewModelLocator.AssetListViewModel = assetListViewModel;
        ViewModelLocator.AddonGridViewModel = addonGridViewModel;

        // コマンドの初期化
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAddonsAsync);
        UndoCommand = ReactiveCommand.CreateFromTask(UndoLastActionAsync);
        MigrateAddonsCommand = ReactiveCommand.CreateFromTask(MigrateAddonsAsync);
        ResetAllStatesCommand = ReactiveCommand.CreateFromTask(ResetAllStatesAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        ImportDisableManifestCommand = ReactiveCommand.CreateFromTask(ImportDisableManifestAsync);
        ResetManagerCommand = ReactiveCommand.CreateFromTask(ResetManagerAsync);
        RestoreOriginalCommand = ReactiveCommand.CreateFromTask(RestoreOriginalAsync);

        ReloadFeatureFlags();

        // 検索機能の実装
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .DistinctUntilChanged()
            .Subscribe(text => 
            {
                AddonGridViewModel.FilterText = text;
            });

        // アセット選択の監視
        AssetListViewModel.WhenAnyValue(x => x.SelectedAsset)
            .Subscribe(asset =>
            {
                // 初期化中は何もしない
                if (!isInitialized) return;
                
                AddonGridViewModel.SetCurrentAsset(asset);
            });

        // 初期データロードはMainWindowのOnOpenedイベントで行われる
        
        // Undo状態の監視
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
            .ObserveOn(AvaloniaScheduler.Instance)
            .Subscribe(_ => UpdateUndoState());
            
        // 保留中の変更が適用された時のリフレッシュ
        statusBarViewModel.PendingChangesApplied += async (s, e) =>
        {
            await RefreshAddonsAsync();
        };
    }
    
    private async void CheckForUpdatesAfterStartup()
    {
        // 起動5秒後にアップデートチェック
        await Task.Delay(5000);
        await CheckForUpdatesAsync();
    }
    
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var currentVersion = ApplicationVersionProvider.GetUpdateVersion();
            var updateService = new UpdateService(currentVersion);
            
            var updateInfo = await updateService.CheckForUpdateAsync();
            if (updateInfo != null)
            {
                // アップデートダイアログを表示
                var dialog = new UpdateDialog
                {
                    DataContext = new UpdateDialogViewModel(updateService, updateInfo)
                };
                
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                    
                if (mainWindow != null)
                {
                    await dialog.ShowDialog(mainWindow);
                }
            }
        }
        catch (Exception ex)
        {
            // アップデートチェックのエラーは無視
            // Update check failed: {ex.Message}
        }
    }

    public string SearchText
    {
        get => searchText;
        set => SetAndRaise(ref searchText, value);
    }

    public AssetListViewModel AssetListViewModel
    {
        get => assetListViewModel;
        private set => SetAndRaise(ref assetListViewModel, value);
    }

    public AddonGridViewModel AddonGridViewModel
    {
        get => addonGridViewModel;
        private set => SetAndRaise(ref addonGridViewModel, value);
    }

    public StatusBarViewModel StatusBarViewModel
    {
        get => statusBarViewModel;
        private set => SetAndRaise(ref statusBarViewModel, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> MigrateAddonsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetAllStatesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportDisableManifestCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetManagerCommand { get; }
    public ReactiveCommand<Unit, Unit> RestoreOriginalCommand { get; }
    
    public bool CanUndo
    {
        get => canUndo;
        private set => SetAndRaise(ref canUndo, value);
    }
    
    public string AddonStatistics
    {
        get => addonStatistics;
        private set => SetAndRaise(ref addonStatistics, value);
    }

    public bool IsDisableManifestImportEnabled
    {
        get => isDisableManifestImportEnabled;
        private set => SetAndRaise(ref isDisableManifestImportEnabled, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
#if DEBUG
            // MainWindowViewModel.InitializeAsync started
#endif
            
            // Check if this is first run (no config exists)
            var config = addonManager.GetConfiguration();
            bool isFirstRun = config.AddonMetadata.Count == 0;
            
            if (isFirstRun)
            {
                // Show initial loading window
                await ShowInitialLoadingWindow();
            }
            else
            {
                // Normal initialization
#if DEBUG
                // Calling ScanWorkshopFolderAsync from MainWindowViewModel
#endif
                await addonManager.ScanWorkshopFolderAsync();
                
                // Check for new addons after normal initialization
                await CheckForNewAddons();
            }
            
            // ViewModelを初期化
            AssetListViewModel.LoadAssets();
            
            // アドオンをロード（アセット選択前にデータを準備）
            await AddonGridViewModel.LoadAddonsAsync();
            
            // デフォルトアセットを選択（Subscribe Assetを選択）
            var subscribeAsset = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            if (subscribeAsset != null)
            {
                // 初期化フラグを一時的にtrueにして、アセット選択が反映されるようにする
                isInitialized = true;
                AssetListViewModel.SelectedAsset = subscribeAsset;
                isInitialized = false;
                
                // 念のため手動でも設定
                AddonGridViewModel.SetCurrentAsset(subscribeAsset);
            }
            
            // 統計情報を更新
            UpdateAddonStatistics();
            
            // 初期化完了フラグを設定
            isInitialized = true;
            
            // アップデートチェックを開始
            CheckForUpdatesAfterStartup();
        
        // 起動時にコレクションの存在確認
        _ = Task.Run(async () => await CheckCollectionExistenceAsync());
        
        // 自動更新タイマーの設定（5分ごと）
        autoUpdateTimer = new Timer(5 * 60 * 1000); // 5分
        autoUpdateTimer.Elapsed += async (sender, e) => await PerformAutoUpdateAsync();
        autoUpdateTimer.Start();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Format("Error.InitializationFailed", ex.Message));
        }
    }
    
    private async Task ShowInitialLoadingWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingWindow = new InitialLoadingWindow(addonManager);
            await loadingWindow.ShowDialog(desktop.MainWindow);
        }
    }
    
    private async Task CheckForNewAddons()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var checkWindow = new NewAddonCheckWindow(addonManager);
            await checkWindow.ShowDialog(desktop.MainWindow);
        }
    }

    public async Task RefreshAddonsAsync()
    {
        try
        {
            // 現在選択されているアセットのIDを保存
            var currentAssetId = AssetListViewModel.SelectedAsset?.Id;
            
            // コレクションの存在確認
            await CheckCollectionExistenceAsync();
            
            // UIとアドオンの状態を再読み込み
            AssetListViewModel.LoadAssets();
            
            // アセットが再読み込みされた後、選択を復元し、CurrentAssetを更新
            if (!string.IsNullOrEmpty(currentAssetId))
            {
                var asset = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == currentAssetId) 
                          ?? AssetListViewModel.JunctionAsset.FirstOrDefault(a => a.Id == currentAssetId);
                if (asset != null)
                {
                    AssetListViewModel.SelectedAsset = asset;
                    AddonGridViewModel.SetCurrentAsset(asset);
                }
            }
            
            await AddonGridViewModel.LoadAddonsAsync();
            
            // アドオン統計情報を更新
            UpdateAddonStatistics();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UpdateFailed"));
        }
    }
    
    private async Task CheckCollectionExistenceAsync()
    {
        try
        {
            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
            if (steamworksManager == null || !steamworksManager.IsInitialized)
                return;
                
            var config = addonManager.GetConfiguration();
            bool hasChanges = false;
            
            foreach (var asset in config.Assets)
            {
                if (!string.IsNullOrEmpty(asset.WorkshopCollectionId))
                {
                    var exists = await steamworksManager.CheckCollectionExistsAsync(asset.WorkshopCollectionId);
                    if (!exists)
                    {
                        // コレクションが存在しない場合は公開状態を解除
                        asset.WorkshopCollectionId = null;
                        asset.AutoUpdateCollection = false;
                        hasChanges = true;
                    }
                }
            }
            
            if (hasChanges)
            {
                await addonManager.SaveConfigurationAsync();
                
                // UIの更新をメインスレッドで実行
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AssetListViewModel.LoadAssets();
                });
            }
        }
        catch (Exception ex)
        {
            // Collection existence check failed: {ex.Message}
        }
    }
    
    private async Task PerformAutoUpdateAsync()
    {
        try
        {
            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
            if (steamworksManager == null || !steamworksManager.IsInitialized)
                return;
                
            var config = addonManager.GetConfiguration();
            
            foreach (var asset in config.Assets)
            {
                if (!string.IsNullOrEmpty(asset.WorkshopCollectionId) && asset.AutoUpdateCollection)
                {
                    // アセットのViewModelを見つける
                    var assetViewModel = AssetListViewModel.Assets.FirstOrDefault(a => a.Id == asset.Id);
                    if (assetViewModel != null)
                    {
                        await assetViewModel.UpdateCollectionAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Auto update failed: {ex.Message}
        }
    }
    
    private void UpdateAddonStatistics()
    {
        try
        {
            var config = addonManager.GetConfiguration();
            int totalAddons = config.AddonMetadata.Count;
            int enabledAddons = 0;
            int disabledAddons = 0;
            
            // 各アセットのアドオン状態を集計
            foreach (var asset in config.Assets)
            {
                if (asset.IsSystem && asset.Name == "Junction")
                {
                    // Junctionアセット内のアドオンは無効扱い
                    disabledAddons += asset.Addons.Count;
                }
                else if (asset.Enabled)
                {
                    // 有効なアセット内のアドオンで、Excludedでないものをカウント
                    foreach (var addonId in asset.Addons)
                    {
                        var state = asset.AddonStates.ContainsKey(addonId) 
                            ? asset.AddonStates[addonId] 
                            : asset.DefaultAddonState;
                        
                        if (state != AddonState.Excluded)
                        {
                            enabledAddons++;
                        }
                    }
                }
            }
            
            // 重複を除去（複数アセットに含まれるアドオンを考慮）
            enabledAddons = Math.Min(enabledAddons, totalAddons - disabledAddons);
            
            // ファイルサイズを計算
            long totalSize = 0;
            foreach (var addon in config.AddonMetadata.Values)
            {
                totalSize += addon.Size;
            }
            
            // サイズを人間が読みやすい形式に変換
            string sizeText = FormatFileSize(totalSize);
            
            AddonStatistics = $"{L.Get("Status.TotalAddons")}: {totalAddons} | {L.Get("Status.Enabled")}: {enabledAddons} | {L.Get("Status.Disabled")}: {disabledAddons} | {L.Get("Status.TotalSize")}: {sizeText}";
        }
        catch
        {
            AddonStatistics = "";
        }
    }
    
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public void Dispose()
    {
        StatusBarViewModel?.Dispose();
        autoUpdateTimer?.Stop();
        autoUpdateTimer?.Dispose();
    }
    
    private void UpdateUndoState()
    {
        CanUndo = addonManager.GetUndoManager().CanUndo;
    }

    private void ReloadFeatureFlags()
    {
        var settings = AppSettings.Load();
        IsDisableManifestImportEnabled = settings.EnableDisableManifestImport;
    }
    
    private async Task UndoLastActionAsync()
    {
        try
        {
            var undoManager = addonManager.GetUndoManager();
            var lastAction = undoManager.PeekLastAction();
            
            if (lastAction != null)
            {
                var dialogService = new DialogService();
                var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), 
                    L.Format("Confirm.Undo", lastAction.Description));
                
                if (confirmed)
                {
                    var success = await addonManager.UndoLastActionAsync();
                    if (success)
                    {
                        await RefreshAddonsAsync();
                        await dialogService.ShowInfoAsync(L.Get("Success.Title"), L.Get("Success.UndoComplete"));
                    }
                    else
                    {
                        await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UndoFailed"));
                    }
                }
            }
            
            UpdateUndoState();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.UndoOperationFailed"));
        }
    }
    
    private async Task MigrateAddonsAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 一次確認
            var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
                L.Get("Warning.ManualMigration"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 二次確認
            var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
                L.Get("Confirm.ManualMigrationFinal"));
            
            if (!confirmed2)
            {
                return;
            }
            
            // logger.LogInformation("Starting manual migration process"); // Removed logging
            
            // 移行処理を実行
            await addonManager.MigrateExistingAddonsAsync();
            
            // アドオン情報を再読み込み
            await RefreshAddonsAsync();
            
            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                L.Get("Success.MigrationComplete"));
                
            // logger.LogInformation("Manual migration process completed"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to run manual migration", ex); // Removed logging
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.MigrationFailed"));
        }
    }
    
    private async Task ResetAllStatesAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 選択ダイアログを表示
            var choiceDialog = new ResetChoiceDialog();
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
                
            if (mainWindow == null)
            {
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }
            
            await choiceDialog.ShowDialog(mainWindow);
            
            if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.Cancel)
            {
                return;
            }
            
            // 選択に応じて処理を分岐
            if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.ResetAll)
            {
                await ResetAllAddonsStatesAsync();
            }
            else if (choiceDialog.Result == ResetChoiceDialog.ResetChoice.ResetCurrentOnly)
            {
                await ResetCurrentAssetStatesAsync();
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.ResetFailed"));
        }
    }
    
    private async Task ResetAllAddonsStatesAsync()
    {
        var dialogService = new DialogService();
        
        // 一次確認
        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
            L.Get("Warning.ResetAllStates"));
        
        if (!confirmed)
        {
            return;
        }
        
        // 二次確認
        var confirmed2 = await dialogService.ShowConfirmAsync(L.Get("Confirm.FinalConfirmation"), 
            L.Get("Confirm.ResetAllStatesFinal"));
        
        if (!confirmed2)
        {
            return;
        }
        
        // リセット処理を実行
        var config = addonManager.GetConfiguration();
        var junctionAsset = config.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
        var junctionAddonIds = junctionAsset?.Addons ?? new List<string>();
        
        int resetCount = 0;
        
        // 全アセットの状態をリセット
        foreach (var asset in config.Assets)
        {
            // アセット内のアドオン状態をリセット
            var statesToReset = asset.AddonStates
                .Where(kvp => !junctionAddonIds.Contains(kvp.Key))
                .ToList();
            
            foreach (var kvp in statesToReset)
            {
                asset.AddonStates.Remove(kvp.Key);
                resetCount++;
            }
        }
        
        // 状態を更新（ジャンクションの作成/削除を実行）
        await addonManager.UpdateAddonStatesAsync();
        
        // 設定を保存
        await addonManager.SaveConfigurationAsync();
        
        // リロード
        await RefreshAddonsAsync();
        
        await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
            L.Format("Success.ResetComplete", resetCount));
    }
    
    private async Task ResetCurrentAssetStatesAsync()
    {
        var dialogService = new DialogService();
        var currentAsset = AssetListViewModel.SelectedAsset;
        
        if (currentAsset == null)
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), "現在のアセットが選択されていません。");
            return;
        }
        
        // ジャンクションアセットの場合は機能を制限
        if (currentAsset.Id == "junction-system-asset")
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), "ジャンクションアセットでこの機能は使えません。");
            return;
        }
        
        // 確認
        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Warning.Title"), 
            $"「{currentAsset.Name}」アセット内のアドオン状態をリセットしますか？");
        
        if (!confirmed)
        {
            return;
        }
        
        // 実際のAssetオブジェクトを取得
        var config = addonManager.GetConfiguration();
        var asset = config.Assets.FirstOrDefault(a => a.Id == currentAsset.Id);
        
        if (asset == null)
        {
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), "アセットが見つかりません。");
            return;
        }
        
        // 現在のアセットの状態のみリセット
        var statesToReset = asset.AddonStates.ToList();
        int resetCount = statesToReset.Count();
        
        foreach (var kvp in statesToReset)
        {
            asset.AddonStates.Remove(kvp.Key);
        }
        
        // 状態を更新
        await addonManager.UpdateAddonStatesAsync();
        
        // 設定を保存
        await addonManager.SaveConfigurationAsync();
        
        // リロード
        await RefreshAddonsAsync();
        
        await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
            $"「{currentAsset.Name}」アセット内の{resetCount}個のアドオン状態をリセットしました。");
    }
    
    private async Task OpenSettingsAsync()
    {
        try
        {
            var dialog = new SettingsDialog();
            dialog.ResetManagerRequested += async (s, e) => await ResetManagerAsync();
            dialog.RestoreOriginalRequested += async (s, e) => await RestoreOriginalAsync();
            
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
                
            if (mainWindow == null)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }
            
            await dialog.ShowDialog(mainWindow);

            // 設定変更を反映
            var updatedSettings = AppSettings.Load();
            addonManager.UnsubscribeOnHardDisable = updatedSettings.UnsubscribeOnHardDisable;
            IsDisableManifestImportEnabled = updatedSettings.EnableDisableManifestImport;
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), 
                L.Get("Error.SettingsDialogFailed"));
        }
    }

    private async Task ImportDisableManifestAsync()
    {
        try
        {
            ReloadFeatureFlags();
            if (!IsDisableManifestImportEnabled)
            {
                return;
            }

            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.MainWindowNotFound"));
                return;
            }

            var importService = new DisableManifestImportService(
                addonManager,
                pendingChangeManager,
                () => processWatcher.IsGmodRunning);
            var dialog = new DisableManifestImportDialog(new DisableManifestImportViewModel(importService));

            await dialog.ShowDialog(mainWindow);

            AssetListViewModel.LoadAssets();
            await AddonGridViewModel.LoadAddonsAsync();
            UpdateAddonStatistics();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), ex.Message);
        }
    }

    private async Task ResetManagerAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 一次確認
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("Warning.Title"), 
                L.Get("Warning.ResetManager"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 二次確認（より強い警告）
            var confirmed2 = await dialogService.ShowConfirmAsync(
                L.Get("Confirm.FinalConfirmation"), 
                L.Get("Confirm.ResetManagerFinal"));
            
            if (!confirmed2)
            {
                return;
            }
            
            // Reset処理を実行
            await addonManager.ResetManagerAsync();
            
            // 初期読み込み画面を表示
            await ShowInitialLoadingWindow();
            
            // UIを再読み込み
            AssetListViewModel.LoadAssets();
            await AddonGridViewModel.LoadAddonsAsync();
            
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"), 
                L.Get("Success.ResetComplete"));
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"), 
                L.Get("Error.ResetFailed"));
        }
    }
    
    private async Task RestoreOriginalAsync()
    {
        try
        {
            var dialogService = new DialogService();
            
            // 一次確認
            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("Warning.Title"), 
                L.Get("Confirm.RestoreOriginal"));
            
            if (!confirmed)
            {
                return;
            }
            
            // 二次確認（より強い警告）
            var confirmed2 = await dialogService.ShowConfirmAsync(
                L.Get("Confirm.FinalConfirmation"), 
                L.Get("Confirm.RestoreOriginalFinal"));
            
            if (!confirmed2)
            {
                return;
            }
            
            // Restore処理を実行
            await addonManager.RestoreOriginalStateAsync();
            
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"), 
                L.Get("Success.RestoreComplete"));
                
            // アプリケーションを終了
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            desktop?.Shutdown();
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"), 
                L.Get("Error.RestoreFailed"));
        }
    }
}
