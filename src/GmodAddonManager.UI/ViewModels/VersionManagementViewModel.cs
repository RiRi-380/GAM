using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.IO;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GmodAddonManager.UI.ViewModels
{
    public class VersionManagementViewModel : ViewModelBase
    {
        // ウィンドウを閉じるためのイベント
        public event EventHandler? CloseRequested;
        private readonly Asset _asset;
        private readonly AddonManager _addonManager;
        private readonly SteamworksManager _steamworksManager;
        private readonly HybridWorkshopService _workshopService;
        private bool _includeAddonStates = true;
        private bool _isNewestFirst = true;
        private ObservableCollection<VersionItemViewModel> _versions;
        private VersionItemViewModel? _selectedVersion;
        private ObservableCollection<VersionAddonItemViewModel> _selectedVersionAddons;
        private bool _showDiff = true;
        
        // キャッシュ用フィールド
        private readonly Dictionary<string, AddonItemViewModel> _addonViewModelCache = new();
        private List<WorkshopAddon>? _cachedAddonList;
        private DateTime _lastScanTime = DateTime.MinValue;
        
        public VersionManagementViewModel(Asset asset, AddonManager addonManager)
        {
            _asset = asset;
            _addonManager = addonManager;
            _steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager!;
            var iconResolver = new WorkshopIconResolver(
                new SteamPathDetector(), 
                null, 
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GmodAddonManager"
                )
            );
            var steamWorkshopService = new SteamWorkshopService(iconResolver);
            _workshopService = new HybridWorkshopService(_steamworksManager, steamWorkshopService);
            _versions = new ObservableCollection<VersionItemViewModel>();
            _selectedVersionAddons = new ObservableCollection<VersionAddonItemViewModel>();
            
            CreateNewVersionCommand = ReactiveCommand.CreateFromTask(CreateNewVersionAsync);
            ShowVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(ShowVersionAsync);
            RestoreVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(RestoreVersionAsync);
            RestoreSelectedVersionCommand = ReactiveCommand.CreateFromTask(RestoreSelectedVersionAsync, 
                this.WhenAnyValue(x => x.SelectedVersion).Select(v => v != null && !v.IsCurrent));
            DeleteVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(DeleteVersionAsync);
            ClearVersionHistoryCommand = ReactiveCommand.CreateFromTask(ClearVersionHistoryAsync);
            
            LoadVersions();
            
            // デフォルトで選択するバージョンを決定
            if (_asset.CurrentVersion == 0 && _asset.HasImportBaseline)
            {
                // v0でインポートベースラインがある場合は、インポート前バージョンを選択
                SelectedVersion = _versions.FirstOrDefault(v => v.IsImportBaseline);
            }
            else
            {
                // それ以外の場合は現在のバージョンを選択
                SelectedVersion = _versions.FirstOrDefault(v => v.IsCurrent);
            }
        }
        
        public string AssetName => _asset.Name;
        
        public bool IncludeAddonStates
        {
            get => _includeAddonStates;
            set => this.RaiseAndSetIfChanged(ref _includeAddonStates, value);
        }
        
        public bool IsNewestFirst
        {
            get => _isNewestFirst;
            set
            {
                this.RaiseAndSetIfChanged(ref _isNewestFirst, value);
                this.RaisePropertyChanged(nameof(SortedVersions));
            }
        }
        
        public bool ShowDiff
        {
            get => _showDiff;
            set
            {
                this.RaiseAndSetIfChanged(ref _showDiff, value);
                // 差分表示の切り替え時に再読み込み
                if (SelectedVersion != null)
                {
                    _ = LoadSelectedVersionAddonsAsync(SelectedVersion);
                }
            }
        }
        
        public ObservableCollection<VersionItemViewModel> Versions => _versions;
        
        public IEnumerable<VersionItemViewModel> SortedVersions
        {
            get
            {
                if (IsNewestFirst)
                    return _versions.OrderByDescending(v => v.IsImportBaseline ? int.MinValue : v.Version);
                else
                    return _versions.OrderBy(v => v.IsImportBaseline ? int.MinValue : v.Version);
            }
        }
        
        public VersionItemViewModel? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                // Debug.WriteLine($"[VersionManagement] SelectedVersion setter called");
                // Debug.WriteLine($"[VersionManagement] New value: {value?.VersionDisplay} (v{value?.Version}), IsCurrent: {value?.IsCurrent}");
                
                // 以前の選択を解除
                if (_selectedVersion != null)
                {
                    _selectedVersion.IsSelected = false;
                }
                
                this.RaiseAndSetIfChanged(ref _selectedVersion, value);
                
                // 新しい選択を設定
                if (value != null)
                {
                    value.IsSelected = true;
                    _ = LoadSelectedVersionAddonsAsync(value);
                }
                
                this.RaisePropertyChanged(nameof(SelectedVersionTitle));
                this.RaisePropertyChanged(nameof(CanRestore));
                
                // Debug.WriteLine($"[VersionManagement] CanRestore: {CanRestore}");
            }
        }
        
        public ObservableCollection<VersionAddonItemViewModel> SelectedVersionAddons => _selectedVersionAddons;
        
        public string SelectedVersionTitle => SelectedVersion != null 
            ? $"{SelectedVersion.VersionDisplay} - {SelectedVersion.CreatedAtDisplay}"
            : "バージョンを選択してください";
            
        public bool CanRestore => SelectedVersion != null;
        
        public ReactiveCommand<Unit, Unit> CreateNewVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> ShowVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> RestoreVersionCommand { get; }
        public ReactiveCommand<Unit, Unit> RestoreSelectedVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> DeleteVersionCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearVersionHistoryCommand { get; }
        
        private void LoadVersions()
        {
            // Debug.WriteLine($"[VersionManagement] LoadVersions called for asset '{_asset.Name}'");
            // Debug.WriteLine($"[VersionManagement] CurrentVersion: {_asset.CurrentVersion}");
            // Debug.WriteLine($"[VersionManagement] VersionHistory count: {_asset.VersionHistory.Count}");
            
            _versions.Clear();
            
            // 履歴からバージョンを追加
            foreach (var version in _asset.VersionHistory)
            {
                var isCurrent = version.Version == _asset.CurrentVersion;
                // Debug.WriteLine($"[VersionManagement] Adding version {version.Version} (IsImportBaseline: {version.IsImportBaseline}, IsCurrent: {isCurrent})");
                
                var vm = new VersionItemViewModel
                {
                    Version = version.Version,
                    CreatedAt = version.CreatedAt,
                    AddonCount = version.AddonIds.Count,
                    IsCurrent = isCurrent && !version.IsImportBaseline,  // インポートベースラインは決して現在のバージョンにならない
                    IncludesStates = version.IncludeAddonStates,
                    IsImportBaseline = version.IsImportBaseline
                };
                _versions.Add(vm);
            }
            
            // v0は表示しない（インポートベースラインがある場合を除く）
            if (!_asset.VersionHistory.Any(v => v.Version == _asset.CurrentVersion) && _asset.CurrentVersion != 0)
            {
                var currentVersion = new VersionItemViewModel
                {
                    Version = _asset.CurrentVersion,
                    CreatedAt = DateTime.Now,
                    AddonCount = _asset.Addons.Count,
                    IsCurrent = true,
                    IncludesStates = false
                };
                _versions.Add(currentVersion);
            }
            
            // 削除可能フラグを更新
            UpdateCanDeleteFlags();
            
            // 各バージョンのプロパティ変更を通知（背景色の更新など）
            foreach (var version in _versions)
            {
                version.RaisePropertyChanged(nameof(version.IsCurrent));
                version.RaisePropertyChanged(nameof(version.BackgroundColor));
            }
        }
        
        private void UpdateCanDeleteFlags()
        {
            // バージョンが1つしかない場合、または v0 の場合は削除不可
            if (_versions.Count == 1)
            {
                foreach (var version in _versions)
                {
                    version.CanDelete = false;
                    version.RaisePropertyChanged(nameof(version.CanDelete));
                }
            }
            else
            {
                foreach (var version in _versions)
                {
                    // v0とインポートベースラインは削除不可
                    version.CanDelete = version.Version != 0 && !version.IsImportBaseline;
                    version.RaisePropertyChanged(nameof(version.CanDelete));
                }
            }
        }
        
        private async Task CreateNewVersionAsync()
        {
            try
            {
                var dialogService = new DialogService();
                var confirmed = await dialogService.ShowConfirmAsync(
                    "新規バージョン作成",
                    $"新規バージョン(v{_asset.CurrentVersion + 1})を作成しますか？"
                );
                
                if (!confirmed) return;
                
                // 新しいバージョンを作成
                var newVersionNumber = _asset.CurrentVersion + 1;
                var newVersion = new AssetVersion
                {
                    Version = newVersionNumber,
                    CreatedAt = DateTime.Now,
                    AddonIds = new List<string>(_asset.Addons),
                    IncludeAddonStates = IncludeAddonStates
                };
                
                // GAM形式のコンテンツを生成
                var gamLines = new List<string>
                {
                    "# GAM Collection Export v1",
                    $"# Title: {_asset.Name} v{newVersionNumber}",
                    $"# Description: Version {newVersionNumber} of {_asset.Name}",
                    $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"# Count: {_asset.Addons.Count}",
                    ""
                };
                gamLines.AddRange(_asset.Addons);
                newVersion.GamContent = string.Join("\n", gamLines);
                
                // アドオン状態を保存する場合
                if (IncludeAddonStates)
                {
                    newVersion.AddonStates = new Dictionary<string, AddonState>(_asset.AddonStates);
                }
                
                // バージョン履歴に追加
                _asset.VersionHistory.Add(newVersion);
                _asset.CurrentVersion = newVersionNumber;
                
                // 設定を保存
                await _addonManager.SaveConfigurationAsync();
                
                // UIを更新
                LoadVersions();
                
                // 新しく作成したバージョンを選択
                SelectedVersion = _versions.FirstOrDefault(v => v.Version == newVersionNumber);
                
                // 選択されたバージョンのアドオンを読み込む（これにより右側のアドオン表示が更新される）
                if (SelectedVersion != null)
                {
                    await LoadSelectedVersionAddonsAsync(SelectedVersion);
                }
                
                // バージョン一覧を強制的に更新
                this.RaisePropertyChanged(nameof(SortedVersions));
                
                await dialogService.ShowInfoAsync(
                    "バージョン作成完了",
                    $"v{newVersionNumber}として保存しました。"
                );
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "バージョン作成に失敗しました。");
            }
        }
        
        private async Task ShowVersionAsync(VersionItemViewModel version)
        {
            try
            {
                // 選択されたバージョンの詳細を表示
                var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                if (window?.MainWindow != null)
                {
                    var dialog = new VersionDetailsDialog(_asset, version.Version, _asset.VersionHistory);
                    await dialog.ShowDialog(window.MainWindow);
                }
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "バージョン詳細の表示に失敗しました。");
            }
        }
        
        private async Task RestoreVersionAsync(VersionItemViewModel versionVm)
        {
            // Debug.WriteLine($"[VersionManagement] RestoreVersionAsync called with version: {versionVm.VersionDisplay} (v{versionVm.Version})");
            // Debug.WriteLine($"[VersionManagement] IsImportBaseline: {versionVm.IsImportBaseline}");
            
            try
            {
                var dialogService = new DialogService();
                
                // 履歴から指定バージョンを検索
                var targetVersion = _asset.VersionHistory.FirstOrDefault(v => v.Version == versionVm.Version);
                if (targetVersion == null)
                {
                    await dialogService.ShowErrorAsync("エラー", "指定されたバージョンが見つかりません。");
                    return;
                }
                
                // 復元の詳細を計算
                List<string> addonsToSubscribe = new List<string>();
                List<string> addonsToUnsubscribe = new List<string>();
                bool isSteamworksAvailable = false;
                
                // サブスクライブアセットの場合、差分を計算
                if (_asset.Id == "subscribe-system-asset")
                {
                    // 実際のSteamのサブスクライブ状態を取得
                    HashSet<string> currentAddons;
                    if (_steamworksManager != null && _steamworksManager.IsInitialized)
                    {
                        isSteamworksAvailable = true;
                        // Steamから現在の実際のサブスクライブ状態を取得
                        var actualSubscribedItems = _steamworksManager.GetSubscribedItems();
                        currentAddons = new HashSet<string>(actualSubscribedItems);
                        currentAddons.Remove("*"); // *を除外
                        // Debug.WriteLine($"Found {currentAddons.Count} actually subscribed items in Steam (after removing *)");
                    }
                    else
                    {
                        // Steamworksが利用できない場合は、GAM内部の状態を使用（フォールバック）
                        currentAddons = new HashSet<string>(_asset.Addons);
                        currentAddons.Remove("*"); // *を除外
                        // Debug.WriteLine($"Using GAM internal state: {currentAddons.Count} items (after removing *)");
                    }
                    
                    // インポートベースラインバージョンの場合
                    if (targetVersion.IsImportBaseline && targetVersion.NewlySubscribedAddonIds != null)
                    {
                        // 新規サブスクライブしたアドオンのみをアンサブスクライブ
                        addonsToSubscribe = new List<string>(); // 追加なし
                        addonsToUnsubscribe = targetVersion.NewlySubscribedAddonIds
                            .Where(id => currentAddons.Contains(id))
                            .ToList();
                        
                        // Debug.WriteLine($"Import baseline restore: unsubscribing {addonsToUnsubscribe.Count} newly subscribed addons");
                    }
                    else
                    {
                        // 通常のバージョン復元
                        var targetAddons = new HashSet<string>(targetVersion.AddonIds);
                        
                        // デバッグ: targetAddonsの内容を確認
                        // Debug.WriteLine($"Target addons for v{targetVersion.Version}: {string.Join(", ", targetAddons)}");
                        if (targetAddons.Contains("*"))
                        {
                            // Debug.WriteLine("WARNING: Target addons contains '*' - removing it");
                            targetAddons.Remove("*");
                        }
                        
                        // 追加すべきアドオン（復元先にあって現在にない）
                        addonsToSubscribe = targetAddons.Except(currentAddons).ToList();
                        
                        // 削除すべきアドオン（現在あって復元先にない）
                        addonsToUnsubscribe = currentAddons.Except(targetAddons).ToList();
                    }
                }
                
                // 確認ダイアログを表示
                var confirmMessage = versionVm.IsImportBaseline 
                    ? $"{_asset.Name}のインポート前に戻しますか？\n新規サブスクライブしたアドオンは解除されます。"
                    : $"v{versionVm.Version}に復元しますか？\n現在のアドオン構成は失われます。";
                var confirmed = await dialogService.ShowVersionRestoreConfirmAsync(
                    confirmMessage,
                    addonsToSubscribe,
                    addonsToUnsubscribe,
                    isSteamworksAvailable);
                
                // Debug.WriteLine($"[VersionManagement] Restore confirmation result: {confirmed}");
                
                if (!confirmed) 
                {
                    // Debug.WriteLine("[VersionManagement] Restore cancelled by user");
                    return;
                }
                
                // サブスクライブアセットの場合、Workshop操作を実行
                if (_asset.Id == "subscribe-system-asset")
                {
                    // Debug.WriteLine($"[VersionManagement] Subscribe asset detected");
                    
                    // アドオンリストを復元（*を除外）
                    _asset.Addons.Clear();
                    _asset.Addons.AddRange(targetVersion.AddonIds.Where(id => id != "*"));
                    
                    // アドオン状態を復元（保存されている場合）
                    if (targetVersion.IncludeAddonStates && targetVersion.AddonStates != null)
                    {
                        _asset.AddonStates.Clear();
                        foreach (var kvp in targetVersion.AddonStates)
                        {
                            _asset.AddonStates[kvp.Key] = kvp.Value;
                        }
                    }
                    else
                    {
                        // 状態が保存されていない場合、新規追加アドオンはEnabled、削除アドオンはExcludedに
                        foreach (var addonId in addonsToSubscribe)
                        {
                            _asset.AddonStates[addonId] = AddonState.Enabled;
                        }
                    }
                    
                    // 削除されるアドオンはアセットから完全に削除（Excludedにはしない）
                    foreach (var addonId in addonsToUnsubscribe)
                    {
                        _asset.AddonStates.Remove(addonId);
                    }
                    
                    // 現在のバージョンを更新
                    // Debug.WriteLine($"[VersionManagement] Updating CurrentVersion from {_asset.CurrentVersion} to {targetVersion.Version}");
                    _asset.CurrentVersion = targetVersion.Version;
                    
                    // 設定を保存
                    await _addonManager.SaveConfigurationAsync();
                    
                    // Workshop操作を実行
                    if (addonsToSubscribe.Any() || addonsToUnsubscribe.Any())
                    {
                        // 進行状況ダイアログを表示
                        var progressDialog = new Window
                        {
                            Title = "復元中...",
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
                            Text = "復元処理を実行中..."
                        };
                        progressPanel.Children.Add(progressText);
                        
                        var progressBar = new ProgressBar
                        {
                            Minimum = 0,
                            Maximum = addonsToSubscribe.Count + addonsToUnsubscribe.Count,
                            Height = 20,
                            Value = 0
                        };
                        progressPanel.Children.Add(progressBar);
                        
                        progressDialog.Content = progressPanel;
                        
                        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                            ? desktop.MainWindow
                            : null;
                        
                        if (mainWindow != null)
                        {
                            _ = progressDialog.ShowDialog(mainWindow);
                            
                            int currentProgress = 0;
                            
                            // サブスクライブ処理
                            if (addonsToSubscribe.Any())
                            {
                                progressText.Text = $"アドオンをサブスクライブ中... (0/{addonsToSubscribe.Count})";
                                
                                for (int i = 0; i < addonsToSubscribe.Count; i++)
                                {
                                    await SubscribeToWorkshopAsync(addonsToSubscribe[i]);
                                    currentProgress++;
                                    progressBar.Value = currentProgress;
                                    progressText.Text = $"アドオンをサブスクライブ中... ({i + 1}/{addonsToSubscribe.Count})";
                                }
                            }
                            
                            // アンサブスクライブ処理
                            if (addonsToUnsubscribe.Any())
                            {
                                progressText.Text = $"アドオンをアンサブスクライブ中... (0/{addonsToUnsubscribe.Count})";
                                
                                for (int i = 0; i < addonsToUnsubscribe.Count; i++)
                                {
                                    await UnsubscribeFromWorkshopAsync(addonsToUnsubscribe[i]);
                                    currentProgress++;
                                    progressBar.Value = currentProgress;
                                    progressText.Text = $"アドオンをアンサブスクライブ中... ({i + 1}/{addonsToUnsubscribe.Count})";
                                }
                            }
                            
                            progressText.Text = "シンボリックリンクを更新中...";
                            progressBar.IsIndeterminate = true;
                            
                            // Steam APIの反映を待つ
                            if (_steamworksManager != null && _steamworksManager.IsInitialized)
                            {
                                await Task.Delay(2000);
                            }
                            
                            // シンボリックリンクの状態を更新
                            await _addonManager.UpdateAddonStatesAsync();
                            
                            progressDialog.Close();
                        }
                    }
                }
                else
                {
                    // 通常のアセットの場合
                    if (targetVersion.IsImportBaseline)
                    {
                        // インポートベースラインの場合、新規サブスクライブしたアドオンをアンサブスクライブ
                        // Debug.WriteLine($"[VersionManagement] Import baseline restore for asset '{_asset.Name}' (ID: {_asset.Id})");
                        if (targetVersion.NewlySubscribedAddonIds != null && targetVersion.NewlySubscribedAddonIds.Any())
                        {
                            // 進行状況ダイアログを表示
                            var progressDialog = new Window
                            {
                                Title = "インポート前に復元中...",
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
                                Text = "新規サブスクライブしたアドオンを解除中..."
                            };
                            progressPanel.Children.Add(progressText);
                            
                            var progressBar = new ProgressBar
                            {
                                Minimum = 0,
                                Maximum = targetVersion.NewlySubscribedAddonIds.Count,
                                Height = 20,
                                Value = 0
                            };
                            progressPanel.Children.Add(progressBar);
                            
                            progressDialog.Content = progressPanel;
                            
                            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop2
                                ? desktop2.MainWindow
                                : null;
                            
                            if (mainWindow != null)
                            {
                                _ = progressDialog.ShowDialog(mainWindow);
                                
                                // Debug.WriteLine($"[VersionManagement] Unsubscribing {targetVersion.NewlySubscribedAddonIds.Count} newly subscribed addons");
                                // 新規サブスクライブしたアドオンをアンサブスクライブ
                                for (int i = 0; i < targetVersion.NewlySubscribedAddonIds.Count; i++)
                                {
                                    var addonId = targetVersion.NewlySubscribedAddonIds[i];
                                    progressText.Text = $"アドオンをアンサブスクライブ中... ({i + 1}/{targetVersion.NewlySubscribedAddonIds.Count})";
                                    progressBar.Value = i + 1;
                                    
                                    // Debug.WriteLine($"[VersionManagement] Unsubscribing addon ID: {addonId}");
                                    await UnsubscribeFromWorkshopAsync(addonId);
                                }
                                
                                progressText.Text = "Steam APIの反映を待機中...";
                                progressBar.IsIndeterminate = true;
                                
                                // Steam APIの反映を待つ
                                if (_steamworksManager != null && _steamworksManager.IsInitialized)
                                {
                                    // Debug.WriteLine("[VersionManagement] Waiting for Steam API to reflect changes");
                                    await Task.Delay(2000);
                                }
                                
                                progressDialog.Close();
                            }
                        }
                        else
                        {
                            // Debug.WriteLine("[VersionManagement] No newly subscribed addon IDs found");
                        }
                        
                        // アセットを削除する準備
                        var deleteAssetId = _asset.Id;
                        // Debug.WriteLine($"[VersionManagement] Preparing to delete asset with ID: {deleteAssetId}");
                        // Debug.WriteLine($"[VersionManagement] Asset name: {_asset.Name}");
                        // Debug.WriteLine($"[VersionManagement] Current version: {_asset.CurrentVersion}");
                        // Debug.WriteLine($"[VersionManagement] Has import baseline: {_asset.HasImportBaseline}");
                        
                        // インポートベースラインの復元ではCurrentVersionを変更しない（アセットは削除されるため）
                        // _asset.CurrentVersion = targetVersion.Version; // これを削除
                        
                        // 設定を保存
                        // Debug.WriteLine("[VersionManagement] Saving configuration before asset deletion");
                        await _addonManager.SaveConfigurationAsync();
                        
                        // メインウィンドウを取得
                        MainWindowViewModel? mainViewModel = null;
                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            mainViewModel = desktop.MainWindow?.DataContext as MainWindowViewModel;
                        }
                        
                        // 復元成功メッセージを表示
                        await dialogService.ShowInfoAsync(
                            "復元完了", 
                            "インポート前の状態に復元しました。\nアセットは削除されます。"
                        );
                        
                        // ダイアログを閉じる処理を改善
                        // Debug.WriteLine("[VersionManagement] Attempting to close dialog");
                        // Debug.WriteLine($"[VersionManagement] CloseRequested is null: {CloseRequested == null}");
                        
                        if (CloseRequested != null)
                        {
                            // UIスレッドで実行
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                // Debug.WriteLine("[VersionManagement] Invoking CloseRequested on UI thread");
                                
                                // 先にアセットを削除
                                // Debug.WriteLine($"[VersionManagement] Deleting asset with ID: {deleteAssetId}");
                                _addonManager.DeleteAsset(deleteAssetId);
                                await _addonManager.SaveConfigurationAsync();
                                
                                // メインウィンドウを更新
                                // Debug.WriteLine("[VersionManagement] Updating main window");
                                if (mainViewModel != null)
                                {
                                    // Debug.WriteLine("[VersionManagement] MainViewModel found, refreshing");
                                    mainViewModel.AssetListViewModel?.LoadAssets();
                                    await mainViewModel.RefreshAddonsAsync();
                                }
                                else
                                {
                                    // Debug.WriteLine("[VersionManagement] MainViewModel is null!");
                                }
                                
                                // 最後にダイアログを閉じる
                                CloseRequested.Invoke(this, EventArgs.Empty);
                            });
                        }
                        else
                        {
                            // Debug.WriteLine("[VersionManagement] CloseRequested is null, cannot close dialog!");
                            
                            // CloseRequestedがnullでも、アセットの削除とメインウィンドウの更新を実行
                            // Debug.WriteLine($"[VersionManagement] Deleting asset with ID: {deleteAssetId}");
                            _addonManager.DeleteAsset(deleteAssetId);
                            await _addonManager.SaveConfigurationAsync();
                            
                            if (mainViewModel != null)
                            {
                                mainViewModel.AssetListViewModel?.LoadAssets();
                                await mainViewModel.RefreshAddonsAsync();
                            }
                        }
                        
                        return; // 処理を終了
                    }
                    else
                    {
                        // 通常のバージョン復元
                        _asset.Addons.Clear();
                        _asset.Addons.AddRange(targetVersion.AddonIds);
                        
                        // アドオン状態を復元（保存されている場合）
                        if (targetVersion.IncludeAddonStates && targetVersion.AddonStates != null)
                        {
                            _asset.AddonStates.Clear();
                            foreach (var kvp in targetVersion.AddonStates)
                            {
                                _asset.AddonStates[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    
                    // 現在のバージョンを更新
                    _asset.CurrentVersion = targetVersion.Version;
                    
                    // 設定を保存
                    await _addonManager.SaveConfigurationAsync();
                    
                    // 通常アセットの場合もアドオン状態を更新
                    await _addonManager.UpdateAddonStatesAsync();
                }
                
                // インポートベースラインから復元した場合は、アセットが削除されているため
                // UIの更新をスキップする
                if (!targetVersion.IsImportBaseline)
                {
                    // UIを更新
                    LoadVersions();
                    
                    // 復元したバージョンを選択状態にする
                    SelectedVersion = _versions.FirstOrDefault(v => v.Version == targetVersion.Version);
                    
                    // バージョン一覧を強制的に更新
                    this.RaisePropertyChanged(nameof(SortedVersions));
                    this.RaisePropertyChanged(nameof(Versions));
                    
                    await dialogService.ShowInfoAsync(
                        "復元完了",
                        $"v{targetVersion.Version}に復元しました。"
                    );
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"[VersionManagement] Error in RestoreVersionAsync: {ex}");
                // Debug.WriteLine($"[VersionManagement] StackTrace: {ex.StackTrace}");
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "バージョン復元に失敗しました。");
            }
        }
        
        private async Task RestoreSelectedVersionAsync()
        {
            // Debug.WriteLine("[VersionManagement] RestoreSelectedVersionAsync called");
            if (SelectedVersion != null)
            {
                // Debug.WriteLine($"[VersionManagement] SelectedVersion: {SelectedVersion.VersionDisplay} (v{SelectedVersion.Version})");
                await RestoreVersionAsync(SelectedVersion);
            }
            else
            {
                // Debug.WriteLine("[VersionManagement] SelectedVersion is null!");
            }
        }
        
        private async Task LoadSelectedVersionAddonsAsync(VersionItemViewModel version)
        {
            try
            {
                _selectedVersionAddons.Clear();
                
                // 選択されたバージョンのアドオンIDを取得
                List<string> addonIds;
                // 履歴から取得
                var versionData = _asset.VersionHistory.FirstOrDefault(v => v.Version == version.Version);
                addonIds = versionData?.AddonIds ?? new List<string>();
                
                // 重複を除去
                addonIds = addonIds.Distinct().ToList();
                
                // Debug.WriteLine($"Loading v{version.Version}: {addonIds.Count} addons");
                
                // 前のバージョンのアドオンIDを取得（差分表示用）
                List<string> previousAddonIds = new List<string>();
                
                // インポートベースラインまたはv1は比較対象なし
                if (version.IsImportBaseline || version.Version == 1)
                {
                    // 比較しない（全て変更なしとして表示）
                    previousAddonIds = new List<string>(addonIds);
                }
                else
                {
                    // v2以降は前のバージョンと比較
                    var previousVersion = version.Version - 1;
                    var prevVersion = _asset.VersionHistory.FirstOrDefault(v => v.Version == previousVersion);
                    previousAddonIds = prevVersion?.AddonIds ?? new List<string>();
                }
                
                // Debug.WriteLine($"Current version: v{version.Version} with {addonIds.Count} addons");
                // Debug.WriteLine($"Previous addons: {previousAddonIds.Count} addons");
                // Debug.WriteLine($"ShowDiff: {ShowDiff}");
                
                // メインウィンドウと完全に同じ方法でアドオンを読み込む
                // 新しいコレクションを作成
                var versionAddonItems = new ObservableCollection<AddonItemViewModel>();
                
                // キャッシュされたアドオンリストを使用、または5分以上経過していれば再スキャン
                List<WorkshopAddon> addonList;
                var timeSinceLastScan = DateTime.Now - _lastScanTime;
                if (_cachedAddonList == null || timeSinceLastScan > TimeSpan.FromMinutes(5))
                {
                    // ScanWorkshopFolderAsyncを使ってWorkshopAddonオブジェクトを取得
                    addonList = await _addonManager.ScanWorkshopFolderAsync();
                    _cachedAddonList = addonList;
                    _lastScanTime = DateTime.Now;
                }
                else
                {
                    addonList = _cachedAddonList;
                }
                
                // 一時的なリストに全アドオンを収集
                var tempAddonList = new List<VersionAddonItemViewModel>();
                var processedAddonIds = new HashSet<string>(); // 重複チェック用
                
                // バージョンに含まれるアドオンを処理
                foreach (var addonId in addonIds)
                {
                    // 重複チェック
                    if (processedAddonIds.Contains(addonId))
                    {
                        continue;
                    }
                    processedAddonIds.Add(addonId);
                    // WorkshopAddonオブジェクトを探す
                    var workshopAddon = addonList.FirstOrDefault(a => a.Id == addonId);
                    
                    WorkshopAddon addonToUse;
                    if (workshopAddon != null)
                    {
                        addonToUse = workshopAddon;
                    }
                    else
                    {
                        // 見つからない場合は新しく作成（削除されたアドオンの場合）
                        // まずAddonManagerの設定からメタデータを取得
                        var config = _addonManager.GetConfiguration();
                        if (config.AddonMetadata.TryGetValue(addonId, out var metadata))
                        {
                            // 保存されたメタデータから作成
                            addonToUse = metadata;
                        }
                        else
                        {
                            // SteamworksManagerを使ってメタデータを取得
                            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
                            if (steamworksManager != null && steamworksManager.IsInitialized)
                            {
                                var metadataResults = await steamworksManager.FetchMetadataForAddonsAsync(new List<string> { addonId });
                                if (metadataResults.TryGetValue(addonId, out var workshopInfo))
                                {
                                    addonToUse = new WorkshopAddon
                                    {
                                        Id = addonId,
                                        Title = workshopInfo.Title ?? $"Workshop-{addonId}",
                                        FolderPath = "",
                                        IsGmaFile = false,
                                        NeedsTitleUpdate = false,
                                        Size = (long)workshopInfo.FileSize,
                                        LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)workshopInfo.TimeUpdated).DateTime,
                                        Description = workshopInfo.Description,
                                        Author = workshopInfo.Author ?? "",
                                        ThumbnailUrl = workshopInfo.PreviewUrl,
                                        Tags = new string[0]
                                    };
                                    
                                    // メタデータを設定に保存
                                    config.AddonMetadata[addonId] = addonToUse;
                                    await _addonManager.SaveConfigurationAsync();
                                    
                                    // サムネイル画像をダウンロード
                                    if (!string.IsNullOrEmpty(workshopInfo.PreviewUrl) && ulong.TryParse(addonId, out var workshopId))
                                    {
                                        var iconResolver = (Avalonia.Application.Current as App)?.WorkshopIconResolver;
                                        if (iconResolver != null)
                                        {
                                            _ = iconResolver.GetIconAsync(workshopId); // 非同期でダウンロード開始
                                        }
                                    }
                                }
                                else
                                {
                                    // SteamworksManagerでも取得できない場合はWorkshop APIを試す
                                    var workshopDetails = await _workshopService.GetWorkshopDetailsAsync(addonId);
                                    if (workshopDetails != null)
                                    {
                                        addonToUse = new WorkshopAddon
                                        {
                                            Id = addonId,
                                            Title = workshopDetails.Title ?? $"Workshop ID: {addonId}",
                                            FolderPath = "",
                                            IsGmaFile = false,
                                            NeedsTitleUpdate = false,
                                            Size = 0,
                                            LastUpdated = DateTimeOffset.FromUnixTimeSeconds(workshopDetails.TimeUpdated).DateTime,
                                            Description = workshopDetails.Description,
                                            Author = workshopDetails.Creator ?? "",
                                            ThumbnailUrl = workshopDetails.PreviewUrl,
                                            Tags = new string[0]
                                        };
                                    }
                                    else
                                    {
                                        // どちらでも取得できない場合
                                        addonToUse = new WorkshopAddon
                                        {
                                            Id = addonId,
                                            Title = $"Workshop ID: {addonId} (削除済み)",
                                            FolderPath = "",
                                            IsGmaFile = false,
                                            NeedsTitleUpdate = false
                                        };
                                    }
                                }
                            }
                            else
                            {
                                // SteamworksManager が使用できない場合は Workshop API を使用
                                var workshopDetails = await _workshopService.GetWorkshopDetailsAsync(addonId);
                                if (workshopDetails != null)
                                {
                                    addonToUse = new WorkshopAddon
                                    {
                                        Id = addonId,
                                        Title = workshopDetails.Title ?? $"Workshop ID: {addonId}",
                                        FolderPath = "",
                                        IsGmaFile = false,
                                        NeedsTitleUpdate = false,
                                        Size = 0,
                                        LastUpdated = DateTimeOffset.FromUnixTimeSeconds(workshopDetails.TimeUpdated).DateTime,
                                        Description = workshopDetails.Description,
                                        Author = workshopDetails.Creator ?? "",
                                        ThumbnailUrl = workshopDetails.PreviewUrl,
                                        Tags = new string[0]
                                    };
                                }
                                else
                                {
                                    // Workshop APIからも取得できない場合
                                    addonToUse = new WorkshopAddon
                                    {
                                        Id = addonId,
                                        Title = $"Workshop ID: {addonId} (削除済み)",
                                        FolderPath = "",
                                        IsGmaFile = false,
                                        NeedsTitleUpdate = false
                                    };
                                }
                            }
                        }
                    }
                    
                    // キャッシュからAddonItemViewModelを取得、または新規作成
                    AddonItemViewModel addonItemVm;
                    if (_addonViewModelCache.TryGetValue(addonId, out var cachedVm))
                    {
                        // キャッシュされたViewModelを使用（情報を更新）
                        if (!addonToUse.NeedsTitleUpdate && addonToUse.Title != null)
                        {
                            cachedVm.UpdateTitle(addonToUse.Title);
                        }
                        // ファイルサイズなどその他の情報も更新
                        cachedVm.UpdateFromWorkshopAddon(addonToUse);
                        addonItemVm = cachedVm;
                    }
                    else
                    {
                        // 新規作成してキャッシュに追加
                        addonItemVm = new AddonItemViewModel(addonToUse, _addonManager, null);
                        _addonViewModelCache[addonId] = addonItemVm;
                    }
                    
                    // 差分ステータスを判定
                    var status = AddonDiffStatus.Unchanged;
                    if (!version.IsImportBaseline && version.Version > 1 && ShowDiff)  // インポートベースラインでなくv2以降かつ差分表示がONの場合
                    {
                        if (!previousAddonIds.Contains(addonId))
                        {
                            status = AddonDiffStatus.Added;
                            // Debug.WriteLine($"Added: {addonId} in v{version.Version}");
                        }
                    }
                    
                    // VersionAddonItemViewModelにラップして追加
                    var versionAddon = new VersionAddonItemViewModel
                    {
                        AddonItemViewModel = addonItemVm,
                        Status = status
                    };
                    
                    // インポートベースラインバージョンで新規サブスクライブされたアドオンは緑枠
                    if (version.IsImportBaseline)
                    {
                        // Debug.WriteLine($"[VersionManagement] Checking import baseline addon: {addonId}");
                        if (versionData != null)
                        {
                            // Debug.WriteLine($"[VersionManagement] versionData is not null");
                            if (versionData.NewlySubscribedAddonIds != null)
                            {
                                // Debug.WriteLine($"[VersionManagement] NewlySubscribedAddonIds count: {versionData.NewlySubscribedAddonIds.Count}");
                                if (versionData.NewlySubscribedAddonIds.Contains(addonId))
                                {
                                    versionAddon.Status = AddonDiffStatus.Added; // 緑枠
                                    // Debug.WriteLine($"[VersionManagement] Import baseline addon marked as newly subscribed: {addonId}");
                                }
                            }
                            else
                            {
                                // Debug.WriteLine($"[VersionManagement] NewlySubscribedAddonIds is null!");
                            }
                        }
                        else
                        {
                            // Debug.WriteLine($"[VersionManagement] versionData is null!");
                        }
                    }
                    
                    tempAddonList.Add(versionAddon);
                }
                
                // 削除されたアドオンも表示（インポートベースラインまたはv1でない場合のみ）
                if (!version.IsImportBaseline && version.Version > 1 && ShowDiff)
                {
                    foreach (var addonId in previousAddonIds.Except(addonIds))
                    {
                        // 削除されたアドオン用のWorkshopAddonを作成
                        var deletedAddon = new WorkshopAddon
                        {
                            Id = addonId,
                            Title = $"Workshop ID: {addonId} (削除済み)",
                            FolderPath = "",
                            IsGmaFile = false,
                            NeedsTitleUpdate = true
                        };
                        
                        // キャッシュからAddonItemViewModelを取得、または新規作成
                        AddonItemViewModel addonItemVm;
                        if (_addonViewModelCache.TryGetValue(addonId, out var cachedVm))
                        {
                            addonItemVm = cachedVm;
                        }
                        else
                        {
                            // 新規作成してキャッシュに追加
                            addonItemVm = new AddonItemViewModel(deletedAddon, _addonManager, null);
                            _addonViewModelCache[addonId] = addonItemVm;
                        }
                        
                        var versionAddon = new VersionAddonItemViewModel
                        {
                            AddonItemViewModel = addonItemVm,
                            Status = AddonDiffStatus.Removed
                        };
                        
                        tempAddonList.Add(versionAddon);
                    }
                }
                
                // ソート: 削除(赤) → 追加(緑) → 変更なし
                var sortedAddons = tempAddonList
                    .OrderBy(a => 
                    {
                        switch (a.Status)
                        {
                            case AddonDiffStatus.Removed: return 0;
                            case AddonDiffStatus.Added: return 1;
                            default: return 2;
                        }
                    })
                    .ThenBy(a => a.Title)
                    .ToList();
                
                // ShowDiffがオフの場合の処理
                if (!ShowDiff && !version.IsImportBaseline && version.Version > 1)
                {
                    // 削除されたアドオンを除外
                    sortedAddons = sortedAddons.Where(a => a.Status != AddonDiffStatus.Removed).ToList();
                    
                    // 追加されたアドオンのボーダーを通常に戻す
                    foreach (var addon in sortedAddons.Where(a => a.Status == AddonDiffStatus.Added))
                    {
                        addon.Status = AddonDiffStatus.Unchanged;
                    }
                }
                
                // ソート済みリストを追加
                foreach (var addon in sortedAddons)
                {
                    _selectedVersionAddons.Add(addon);
                }
                
                // 表示されているアドオンの詳細とサムネイルを並列で読み込み（メインウィンドウと同じ）
                var tasks = new List<Task>();
                foreach (var versionAddon in _selectedVersionAddons.Take(30))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await versionAddon.AddonItemViewModel.LoadDetailsCommand.Execute();
                        await versionAddon.AddonItemViewModel.LoadThumbnailCommand.Execute();
                    }));
                }
                
                await Task.WhenAll(tasks);
                
                // 残りのアドオンはバックグラウンドで読み込み
                _ = LoadRemainingVersionAddonsAsync(_selectedVersionAddons.Skip(30).ToList());
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "アドオン情報の読み込みに失敗しました。");
            }
        }
        
        private async Task LoadRemainingVersionAddonsAsync(List<VersionAddonItemViewModel> remainingAddons)
        {
            foreach (var versionAddon in remainingAddons)
            {
                await versionAddon.AddonItemViewModel.LoadDetailsCommand.Execute();
                await versionAddon.AddonItemViewModel.LoadThumbnailCommand.Execute();
                await Task.Delay(50); // 負荷分散のための遅延
            }
        }
        
        private async Task DeleteVersionAsync(VersionItemViewModel versionVm)
        {
            try
            {
                var dialogService = new DialogService();
                
                // v0とインポートベースラインは削除できない
                if (versionVm.Version == 0)
                {
                    await dialogService.ShowWarningAsync("警告", "v0は削除できません。");
                    return;
                }
                
                if (versionVm.IsImportBaseline)
                {
                    await dialogService.ShowWarningAsync("警告", "インポート前バージョンは削除できません。");
                    return;
                }
                
                // 最後のバージョンは削除できない
                if (_versions.Count <= 1)
                {
                    await dialogService.ShowWarningAsync("警告", "最後のバージョンは削除できません。");
                    return;
                }
                
                // 1段目確認
                var confirmed1 = await dialogService.ShowConfirmAsync(
                    "バージョン削除",
                    $"v{versionVm.Version}を削除しますか？\nこの操作は元に戻せません。"
                );
                
                if (!confirmed1) return;
                
                // 2段目確認
                var confirmed2 = await dialogService.ShowConfirmAsync(
                    "最終確認",
                    $"本当にv{versionVm.Version}を削除してもよろしいですか？"
                );
                
                if (!confirmed2) return;
                
                // バージョン履歴から削除
                var versionToDelete = _asset.VersionHistory.FirstOrDefault(v => v.Version == versionVm.Version);
                if (versionToDelete != null)
                {
                    _asset.VersionHistory.Remove(versionToDelete);
                    
                    // 削除したバージョンが現在のバージョンだった場合、現在のバージョンを更新
                    if (versionVm.Version == _asset.CurrentVersion)
                    {
                        // 削除したバージョンより1つ前のバージョンを現在のバージョンにする
                        var newCurrentVersion = _asset.VersionHistory
                            .Where(v => v.Version < versionVm.Version)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault();
                        
                        if (newCurrentVersion != null)
                        {
                            _asset.CurrentVersion = newCurrentVersion.Version;
                            
                            // アドオンリストを復元
                            _asset.Addons.Clear();
                            _asset.Addons.AddRange(newCurrentVersion.AddonIds);
                            
                            // アドオン状態を復元（保存されている場合）
                            if (newCurrentVersion.IncludeAddonStates && newCurrentVersion.AddonStates != null)
                            {
                                _asset.AddonStates.Clear();
                                foreach (var kvp in newCurrentVersion.AddonStates)
                                {
                                    _asset.AddonStates[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        else
                        {
                            // 前のバージョンがない場合はv0にする（ただし表示はされない）
                            _asset.CurrentVersion = 0;
                        }
                    }
                    
                    // 設定を保存
                    await _addonManager.SaveConfigurationAsync();
                    
                    // UIを更新（削除後の状態を正しく反映）
                    LoadVersions();
                    
                    // 削除後、前のバージョンを選択
                    if (_versions.Any())
                    {
                        // 削除したバージョンより1つ前のバージョンを探す
                        var previousVersion = _versions
                            .Where(v => v.Version < versionVm.Version)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault();
                        
                        // 前のバージョンがない場合は、次のバージョンを選択
                        if (previousVersion == null)
                        {
                            previousVersion = _versions.OrderBy(v => v.Version).FirstOrDefault();
                        }
                        
                        SelectedVersion = previousVersion;
                    }
                    
                    await dialogService.ShowInfoAsync(
                        "削除完了",
                        $"v{versionVm.Version}を削除しました。"
                    );
                }
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "バージョン削除に失敗しました。");
            }
        }
        
        private async Task ClearVersionHistoryAsync()
        {
            try
            {
                var dialogService = new DialogService();
                
                // 1段目確認
                var confirmed1 = await dialogService.ShowConfirmAsync(
                    "バージョン履歴削除",
                    "すべてのバージョン履歴を削除してv0に戻します。\n現在のアドオン構成は保持されます。\nこの操作は元に戻せません。"
                );
                
                if (!confirmed1) return;
                
                // 2段目確認
                var confirmed2 = await dialogService.ShowConfirmAsync(
                    "最終確認",
                    "本当にすべてのバージョン履歴を削除してもよろしいですか？"
                );
                
                if (!confirmed2) return;
                
                // バージョン履歴をクリア
                _asset.VersionHistory.Clear();
                _asset.CurrentVersion = 0;
                
                // 設定を保存
                await _addonManager.SaveConfigurationAsync();
                
                // UIを更新
                LoadVersions();
                SelectedVersion = _versions.FirstOrDefault();
                
                await dialogService.ShowInfoAsync(
                    "削除完了",
                    "バージョン履歴を削除し、v0に戻しました。"
                );
                
                // ウィンドウを閉じる
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "バージョン履歴の削除に失敗しました。");
            }
        }
        
        private long CalculateDirectorySize(DirectoryInfo dirInfo)
        {
            long size = 0;
            try
            {
                // ファイルのサイズを合計
                var files = dirInfo.GetFiles();
                foreach (var file in files)
                {
                    size += file.Length;
                }
                
                // サブディレクトリのサイズを再帰的に計算
                var subdirs = dirInfo.GetDirectories();
                foreach (var subdir in subdirs)
                {
                    size += CalculateDirectorySize(subdir);
                }
            }
            catch
            {
                // アクセス拒否などのエラーは無視
            }
            return size;
        }
        
        // キャッシュを強制的にリフレッシュする
        public void RefreshCache()
        {
            _cachedAddonList = null;
            _lastScanTime = DateTime.MinValue;
        }
        
        private async Task ShowAddonDetailsAsync(string addonId)
        {
            try
            {
                var dialogService = new DialogService();
                var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                await dialogService.ShowInfoAsync("アドオン詳細", $"アドオンID: {addonId}\nURL: {url}");
                
                // 実際にはブラウザで開く処理を実装
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync("エラー", "アドオン詳細の表示に失敗しました。");
            }
        }
        
        /// <summary>
        /// Workshopからアドオンをサブスクライブする
        /// </summary>
        private async Task SubscribeToWorkshopAsync(string addonId)
        {
            try
            {
                // *をスキップ
                if (addonId == "*")
                {
                    // Debug.WriteLine("Skipping subscribe for '*'");
                    return;
                }
                // SteamworksManagerを使用してサブスクライブ
                if (_steamworksManager != null && _steamworksManager.IsInitialized)
                {
                    // Debug.WriteLine($"Subscribing to workshop addon {addonId} using Steamworks API");
                    var success = await _steamworksManager.SubscribeItemAsync(addonId);
                    
                    if (success)
                    {
                        // Debug.WriteLine($"Successfully subscribed to workshop addon {addonId}");
                    }
                    else
                    {
                        // Debug.WriteLine($"Failed to subscribe to workshop addon {addonId}");
                        
                        // フォールバック: Steam URLスキームでワークショップページを開く
                        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    // Debug.WriteLine($"SteamworksManager not initialized, opening workshop page for manual subscribe");
                    
                    // SteamworksManagerが利用できない場合はワークショップページを開く
                    var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"Failed to subscribe to workshop addon {addonId}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// メインウィンドウをリロードする
        /// </summary>
        private async Task ReloadMainWindowAsync()
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
                // Debug.WriteLine($"Failed to reload main window: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Workshopからアドオンのサブスクライブを解除する
        /// </summary>
        private async Task UnsubscribeFromWorkshopAsync(string addonId)
        {
            try
            {
                // *をスキップ
                if (addonId == "*")
                {
                    // Debug.WriteLine("Skipping unsubscribe for '*'");
                    return;
                }
                // SteamworksManagerを使用してサブスクライブ解除
                if (_steamworksManager != null && _steamworksManager.IsInitialized)
                {
                    // Debug.WriteLine($"Unsubscribing from workshop addon {addonId} using Steamworks API");
                    var success = await _steamworksManager.UnsubscribeItemAsync(addonId);
                    
                    if (success)
                    {
                        // Debug.WriteLine($"Successfully unsubscribed from workshop addon {addonId}");
                    }
                    else
                    {
                        // Debug.WriteLine($"Failed to unsubscribe from workshop addon {addonId}");
                        
                        // フォールバック: Steam URLスキームでワークショップページを開く
                        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    // Debug.WriteLine($"SteamworksManager not initialized, opening workshop page for manual unsubscribe");
                    
                    // SteamworksManagerが利用できない場合はワークショップページを開く
                    var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"Failed to unsubscribe from workshop addon {addonId}: {ex.Message}");
            }
        }
    }
    
    public class VersionItemViewModel : ViewModelBase
    {
        private bool _isSelected;
        
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public int AddonCount { get; set; }
        public bool IsCurrent { get; set; }
        public bool IncludesStates { get; set; }
        public bool CanDelete { get; set; } = true; // 削除可能フラグ
        public bool IsImportBaseline { get; set; } // インポートベースラインかどうか
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                this.RaisePropertyChanged(nameof(BorderColor));
                this.RaisePropertyChanged(nameof(OuterBorderColor));
                this.RaisePropertyChanged(nameof(OuterBorderThickness));
                this.RaisePropertyChanged(nameof(OuterBorderPadding));
            }
        }
        
        public string VersionDisplay => IsImportBaseline ? "インポート前" : $"v{Version}";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy/MM/dd HH:mm:ss");
        public string AddonCountDisplay => $"アドオン数: {AddonCount}";
        public bool HasStateInfo => IncludesStates;
        public string StateInfoDisplay => IncludesStates ? "アドオン状態を含む" : "";
        
        public string BackgroundColor => IsCurrent ? "#1E3A5F" : "Transparent";
        
        // ボーダーカラー: 選択中は緑、現在使用中は青、それ以外はグレー
        public string BorderColor
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "#4A90E2"; // 内側は青（現在使用中）
                if (IsSelected)
                    return "#4CAF50"; // 緑
                if (IsCurrent)
                    return "#4A90E2"; // 青（UIで使われているアクセントカラー）
                return "#444444"; // デフォルトのグレー
            }
        }
        
        // 外側のボーダーカラー（選択中かつ現在使用中の場合のみ緑）
        public string OuterBorderColor
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "#4CAF50"; // 外側は緑（選択中）
                return "Transparent";
            }
        }
        
        // 外側のボーダーの太さ
        public string OuterBorderThickness
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "3";
                return "0";
            }
        }
        
        // 外側のボーダーのパディング
        public string OuterBorderPadding
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "3";
                return "0";
            }
        }
    }
    
    public class VersionAddonItemViewModel : ViewModelBase
    {
        private AddonItemViewModel _addonItemViewModel = null!;
        private AddonDiffStatus _status;
        
        public AddonItemViewModel AddonItemViewModel
        {
            get => _addonItemViewModel;
            set => this.RaiseAndSetIfChanged(ref _addonItemViewModel, value);
        }
        
        // AddonItemViewModelのプロパティをプロキシ
        public string AddonId => AddonItemViewModel?.AddonId ?? "";
        public string Title => AddonItemViewModel?.Title ?? "";
        public Bitmap? ThumbnailBitmap => AddonItemViewModel?.ThumbnailBitmap;
        public bool IsThumbnailLoading => AddonItemViewModel?.IsThumbnailLoading ?? false;
        public string FileSizeText => AddonItemViewModel?.FileSizeText ?? "";
        public string LastModifiedText => AddonItemViewModel?.LastModifiedText ?? "";
        
        public AddonDiffStatus Status
        {
            get => _status;
            set
            {
                this.RaiseAndSetIfChanged(ref _status, value);
                this.RaisePropertyChanged(nameof(BorderColor));
                this.RaisePropertyChanged(nameof(StatusColor));
                this.RaisePropertyChanged(nameof(StatusText));
            }
        }
        
        public string BorderColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "#666666"
        };
        
        public string StatusColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "#FFFFFF"
        };
        
        public string StatusText => Status switch
        {
            AddonDiffStatus.Added => "追加",
            AddonDiffStatus.Removed => "削除",
            _ => ""
        };
        
        public ReactiveCommand<Unit, Unit> ShowDetailsCommand
        {
            get
            {
                if (AddonItemViewModel != null)
                {
                    return AddonItemViewModel.OpenWorkshopCommand;
                }
                return ReactiveCommand.Create(() => { });
            }
        }
    }
}