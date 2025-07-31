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

namespace GmodAddonManager.UI.ViewModels;

public class AssetListViewModel : ViewModelBase
{
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly IDialogService dialogService;

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

        // コマンドの初期化
        CreateAssetCommand = ReactiveCommand.Create(CreateAsset);
        DeleteSelectedAssetCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedAssetAsync,
            this.WhenAnyValue(x => x.SelectedAsset)
                .Select(asset => asset != null && !asset.IsSystem)
        );
        RefreshCommand = ReactiveCommand.Create(LoadAssets);

        // 選択変更の監視
        this.WhenAnyValue(x => x.SelectedAsset)
            .Subscribe(asset =>
            {
                // 以前の選択とIsCurrent状態を解除
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
                
                // 新しい選択を設定
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

    public ReactiveCommand<Unit, Unit> CreateAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public void LoadAssets()
    {
        try
        {
            // 現在の選択を記憶
            var previousSelectedId = SelectedAsset?.Id;
            
            
            Assets.Clear();
            JunctionAsset.Clear();
            
            var configuration = addonManager.GetConfiguration();
            
            foreach (var asset in configuration.Assets)
            {
                var assetVm = new AssetItemViewModel(
                    asset,
                    addonManager,
                    pendingChangeManager,
                    processWatcher
                );
                
                // ジャンクションアセットは別扱い
                if (asset.Id == "junction-system-asset")
                {
                    JunctionAsset.Add(assetVm);
                }
                else
                {
                    Assets.Add(assetVm);
                }
            }

            // 以前の選択を復元
            if (!string.IsNullOrEmpty(previousSelectedId))
            {
                var previousAsset = Assets.FirstOrDefault(a => a.Id == previousSelectedId) 
                                   ?? JunctionAsset.FirstOrDefault(a => a.Id == previousSelectedId);
                if (previousAsset != null)
                {
                    SelectedAsset = previousAsset;
                    return;
                }
            }
            
            // 以前の選択が見つからない場合のみ、最初のアセットを選択
            if (Assets.Count > 0)
            {
                SelectedAsset = Assets[0];
            }

        }
        catch (Exception)
        {
        }
    }

    private async void CreateAsset()
    {
        try
        {
            // ダイアログを表示
            await dialogService.ShowCreateAssetDialogAsync(async (name, addonIds) =>
            {
                
                try
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        // アセットを作成
                        addonManager.CreateAsset(name);
                    
                    // 作成したアセットを取得
                    var config = addonManager.GetConfiguration();
                    if (config?.Assets == null)
                    {
                        await dialogService.ShowErrorAsync("エラー", "設定の取得に失敗しました。");
                        return;
                    }
                    
                    var asset = config.Assets.FirstOrDefault(a => a.Name == name);
                    if (asset == null)
                    {
                        await dialogService.ShowErrorAsync("エラー", $"アセット '{name}' の作成に失敗しました。");
                        return; // アセットの作成に失敗
                    }
                    
                    // コレクションまたはGAMファイルからのインポートの場合
                    if (addonIds != null && addonIds.Count > 0)
                    {
                        var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
                        if (steamworksManager != null && steamworksManager.IsInitialized)
                        {
                            // サブスクライブ状態を確認
                            var subscribedItems = steamworksManager.GetSubscribedItems();
                            var itemsToSubscribe = addonIds.Where(id => !subscribedItems.Contains(id)).ToList();
                            var successfulSubscribes = new List<string>(); // スコープを外に移動
                            var subscribeResults = new Dictionary<string, bool>(); // スコープを外に移動
                            
                            // サブスクライブが必要なアイテムがある場合
                            if (itemsToSubscribe.Count > 0)
                            {
                                var progressDialog = new Window
                                {
                                    Title = "サブスクライブ中...",
                                    Width = 400,
                                    Height = 150,
                                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                    CanResize = false
                                };
                                
                                var progressPanel = new StackPanel
                                {
                                    Margin = new Avalonia.Thickness(20),
                                    Spacing = 10
                                };
                                
                                var progressText = new TextBlock
                                {
                                    Text = "アドオンをサブスクライブしています..."
                                };
                                progressPanel.Children.Add(progressText);
                                
                                var progressBar = new ProgressBar
                                {
                                    Minimum = 0,
                                    Maximum = itemsToSubscribe.Count,
                                    Height = 20
                                };
                                progressPanel.Children.Add(progressBar);
                                
                                progressDialog.Content = progressPanel;
                                
                                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                                    ? desktop.MainWindow
                                    : null;
                                
                                if (mainWindow != null)
                                {
                                    _ = progressDialog.ShowDialog(mainWindow);
                                    
                                    // プログレス報告付きでサブスクライブ
                                    var progress = new Progress<(int current, int total)>(p =>
                                    {
                                        progressBar.Value = p.current;
                                        progressText.Text = $"アドオンをサブスクライブしています... ({p.current}/{p.total})";
                                    });
                                    
                                    subscribeResults = await steamworksManager.SubscribeItemsBatchAsync(itemsToSubscribe, progress);
                                    
                                    // サブスクライブ成功したアイテムのメタデータを取得
                                    successfulSubscribes = subscribeResults.Where(r => r.Value).Select(r => r.Key).ToList();
                                    if (successfulSubscribes.Count > 0)
                                    {
                                        progressText.Text = "アドオン情報を取得しています...";
                                        progressBar.IsIndeterminate = false;
                                        progressBar.Maximum = successfulSubscribes.Count;
                                        progressBar.Value = 0;
                                        
                                        var metadataProgress = new Progress<(int current, int total)>(p =>
                                        {
                                            progressBar.Value = p.current;
                                            progressText.Text = $"アドオン情報を取得しています... ({p.current}/{p.total})";
                                        });
                                        
                                        // メタデータとサムネイルを取得
                                        var metadataResults = await steamworksManager.FetchMetadataForAddonsAsync(successfulSubscribes, metadataProgress);
                                        
                                        // メタデータをAddonManagerに保存
                                        var addonConfig = addonManager.GetConfiguration();
                                        foreach (var kvp in metadataResults)
                                        {
                                            if (addonConfig.AddonMetadata.ContainsKey(kvp.Key))
                                            {
                                                var addon = addonConfig.AddonMetadata[kvp.Key];
                                                addon.Title = kvp.Value.Title;
                                                addon.Description = kvp.Value.Description;
                                                addon.Author = kvp.Value.Author;
                                                addon.Size = (long)kvp.Value.FileSize;
                                                addon.LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)kvp.Value.TimeUpdated).DateTime;
                                                addon.NeedsTitleUpdate = false;
                                            }
                                            else
                                            {
                                                // 新規アドオンの場合
                                                var addon = new WorkshopAddon(kvp.Key, "")
                                                {
                                                    Title = kvp.Value.Title,
                                                    Description = kvp.Value.Description,
                                                    Author = kvp.Value.Author,
                                                    Size = (long)kvp.Value.FileSize,
                                                    LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)kvp.Value.TimeUpdated).DateTime,
                                                    NeedsTitleUpdate = false
                                                };
                                                addonConfig.AddonMetadata[kvp.Key] = addon;
                                            }
                                            
                                            // サムネイル画像をダウンロード
                                            if (!string.IsNullOrEmpty(kvp.Value.PreviewUrl) && ulong.TryParse(kvp.Key, out var workshopId))
                                            {
                                                var iconResolver = (Avalonia.Application.Current as App)?.WorkshopIconResolver;
                                                if (iconResolver != null)
                                                {
                                                    _ = iconResolver.GetIconAsync(workshopId); // 非同期でダウンロード開始
                                                }
                                            }
                                        }
                                        
                                        // 設定を保存
                                        await addonManager.SaveConfigurationAsync();
                                    }
                                    
                                    progressDialog.Close();
                                    
                                    // 新規アドオンチェックウィンドウを表示
                                    if (subscribeResults.Any(r => r.Value))
                                    {
                                        var checkWindow = new NewAddonCheckWindow(addonManager);
                                        await checkWindow.ShowDialog(mainWindow);
                                        
                                        // 再度スキャンして新規アドオンを検出
                                        await addonManager.ScanForNewAddonsAsync();
                                    }
                                }
                            }
                            else
                            {
                                // すべてのアドオンがすでにサブスクライブ済みの場合もメタデータを取得
                                var existingConfig = addonManager.GetConfiguration();
                                if (existingConfig != null)
                                {
                                    var needsMetadataUpdate = addonIds.Where(id => 
                                    {
                                        if (existingConfig.AddonMetadata.TryGetValue(id, out var addon))
                                        {
                                            return addon.NeedsTitleUpdate || string.IsNullOrEmpty(addon.Title) || addon.Title.StartsWith("Workshop-");
                                        }
                                        return true;
                                    }).ToList();
                                    
                                    if (needsMetadataUpdate.Count > 0)
                                    {
                                        var progressDialog = new Window
                                        {
                                            Title = "アドオン情報取得中...",
                                            Width = 400,
                                            Height = 150,
                                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                            CanResize = false
                                        };
                                        
                                        var progressPanel = new StackPanel
                                        {
                                            Margin = new Avalonia.Thickness(20),
                                            Spacing = 10
                                        };
                                        
                                        var progressText = new TextBlock
                                        {
                                            Text = "アドオン情報を取得しています..."
                                        };
                                        progressPanel.Children.Add(progressText);
                                        
                                        var progressBar = new ProgressBar
                                        {
                                            Minimum = 0,
                                            Maximum = needsMetadataUpdate.Count,
                                            Height = 20
                                        };
                                        progressPanel.Children.Add(progressBar);
                                        
                                        progressDialog.Content = progressPanel;
                                        
                                        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                                            ? desktop.MainWindow
                                            : null;
                                        
                                        if (mainWindow != null)
                                        {
                                            _ = progressDialog.ShowDialog(mainWindow);
                                            
                                            var metadataProgress = new Progress<(int current, int total)>(p =>
                                            {
                                                progressBar.Value = p.current;
                                                progressText.Text = $"アドオン情報を取得しています... ({p.current}/{p.total})";
                                            });
                                            
                                            var metadataResults = await steamworksManager.FetchMetadataForAddonsAsync(needsMetadataUpdate, metadataProgress);
                                            
                                            // メタデータを更新
                                            foreach (var kvp in metadataResults)
                                            {
                                                if (existingConfig.AddonMetadata.ContainsKey(kvp.Key))
                                                {
                                                    var addon = existingConfig.AddonMetadata[kvp.Key];
                                                    addon.Title = kvp.Value.Title;
                                                    addon.Description = kvp.Value.Description;
                                                    addon.Author = kvp.Value.Author;
                                                    addon.Size = (long)kvp.Value.FileSize;
                                                    addon.LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)kvp.Value.TimeUpdated).DateTime;
                                                    addon.NeedsTitleUpdate = false;
                                                }
                                                
                                                // サムネイル画像をダウンロード
                                                if (!string.IsNullOrEmpty(kvp.Value.PreviewUrl) && ulong.TryParse(kvp.Key, out var workshopId))
                                                {
                                                    var iconResolver = (Avalonia.Application.Current as App)?.WorkshopIconResolver;
                                                    if (iconResolver != null)
                                                    {
                                                        _ = iconResolver.GetIconAsync(workshopId); // 非同期でダウンロード開始
                                                    }
                                                }
                                            }
                                            
                                            // 設定を保存
                                            await addonManager.SaveConfigurationAsync();
                                            
                                            progressDialog.Close();
                                        }
                                    }
                                }
                            }
                            
                            // アドオンをアセットに追加（すべてのアドオンを追加）
                            var addonsToAdd = new List<string>(addonIds);
                            
                            // バッチ処理で一度に追加
                            if (addonsToAdd.Count > 0)
                            {
                                addonManager.AddAddonsToAssetBatch(asset.Id, addonsToAdd);
                                
                                // アセットのViewModelを取得して自動更新を確認
                                var assetVm = Assets.FirstOrDefault(a => a.Id == asset.Id);
                                if (assetVm != null && assetVm.AutoUpdateEnabled && !string.IsNullOrEmpty(asset.WorkshopCollectionId))
                                {
                                    _ = assetVm.UpdateCollectionAsync();
                                }
                                
                                // インポートベースラインバージョンを作成
                                var importBaselineVersion = new AssetVersion
                                {
                                    Version = -1, // 特別なバージョン番号
                                    CreatedAt = DateTime.Now,
                                    AddonIds = new List<string>(addonsToAdd),
                                    IncludeAddonStates = true,
                                    IsImportBaseline = true,
                                    NewlySubscribedAddonIds = itemsToSubscribe, // 新規サブスクライブしたアドオンID
                                    ImportType = itemsToSubscribe.Count > 0 ? ImportTypes.Collection : ImportTypes.GamFormat, // インポートタイプを判定
                                    Note = $"{name}のインポート時の初期状態"
                                };
                                
                                // [AssetListViewModel] Creating import baseline for asset '{name}'
                                // [AssetListViewModel] Total addons to add: {addonsToAdd.Count}
                                // [AssetListViewModel] Newly subscribed addons: {itemsToSubscribe.Count}
                                foreach (var addonId in itemsToSubscribe)
                                {
                                    // [AssetListViewModel] Newly subscribed addon ID: {addonId}
                                }
                                
                                // アドオンの状態を保存
                                importBaselineVersion.AddonStates = new Dictionary<string, AddonState>();
                                foreach (var addonId in addonsToAdd)
                                {
                                    importBaselineVersion.AddonStates[addonId] = AddonState.Enabled;
                                }
                                
                                // バージョン履歴に追加
                                asset.VersionHistory.Add(importBaselineVersion);
                                
                                // CurrentVersionは0のままにする（v-1と表示されるのを防ぐ）
                                // asset.CurrentVersion = 0; // 既にデフォルトで0なので不要
                            }
                            
                            if (addonsToAdd.Count < addonIds.Count)
                            {
                                var subscribedButNotDownloaded = addonIds.Count - addonsToAdd.Count;
                                var message = $"{addonIds.Count}個中{addonsToAdd.Count}個のアドオンを追加しました。\n\n";
                                
                                if (itemsToSubscribe.Count > 0 && subscribeResults.Any(r => r.Value))
                                {
                                    message += $"✓ {successfulSubscribes.Count}個のアドオンをサブスクライブしました。\n";
                                    message += "✓ アドオン情報を取得しました。\n\n";
                                    message += "ダウンロードはSteamクライアントで進行中です。\n";
                                    message += "完了後、「更新」ボタンで表示されます。";
                                }
                                else
                                {
                                    message += $"残りの{subscribedButNotDownloaded}個はサブスクライブされました。\n";
                                    message += "Steamでダウンロードが完了するまでお待ちください。";
                                }
                                
                                await dialogService.ShowInfoAsync("インポート結果", message);
                            }
                            else if (itemsToSubscribe.Count > 0 && successfulSubscribes.Count > 0)
                            {
                                // すべて追加できた場合でも、新規サブスクライブがあった場合はメッセージを表示
                                await dialogService.ShowInfoAsync("インポート完了", 
                                    $"✓ {addonIds.Count}個のアドオンをすべて追加しました。\n" +
                                    $"✓ {successfulSubscribes.Count}個のアドオンを新規サブスクライブしました。\n" +
                                    "✓ アドオン情報を取得しました。");
                            }
                        }
                    }
                    
                    // 即座に保存（デバウンスを無視）
                    await addonManager.SaveConfigurationImmediatelyAsync();
                    
                    // メインウィンドウ全体をリフレッシュ（新しいアドオンを含む）
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktopApp)
                    {
                        if (desktopApp.MainWindow?.DataContext is MainWindowViewModel mainVm)
                        {
                            await mainVm.RefreshAddonsAsync();
                        }
                    }
                    
                    // 新しく作成したアセットを選択
                    var newAsset = Assets.FirstOrDefault(a => a.Id == asset.Id);
                    if (newAsset != null)
                    {
                        SelectedAsset = newAsset;
                    }
                }
                }
                catch (Exception ex)
                {
                    await dialogService.ShowErrorAsync("エラー", "アセットの作成に失敗しました。");
                }
            });
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync("エラー", "ダイアログの表示に失敗しました。");
        }
    }

    private async Task DeleteSelectedAssetAsync()
    {
        if (SelectedAsset == null || SelectedAsset.IsSystem) return;

        var confirmed = await dialogService.ShowConfirmAsync(
            "確認",
            $"アセット「{SelectedAsset.Name}」を削除してもよろしいですか？"
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
                await dialogService.ShowErrorAsync("エラー", "アセットの削除に失敗しました。");
            }
        }
    }

    public void EnableAllAssets()
    {
        foreach (var asset in Assets.Where(a => !a.IsEnabled))
        {
            _ = asset.ToggleEnabledCommand.Execute();
        }
    }

    public void DisableAllAssets()
    {
        foreach (var asset in Assets.Where(a => a.IsEnabled && !a.IsSystem))
        {
            _ = asset.ToggleEnabledCommand.Execute();
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