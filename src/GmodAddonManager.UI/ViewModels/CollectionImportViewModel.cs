using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using ReactiveUI;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using System.Collections.Generic;
using System.IO;
using GmodAddonManager.UI.Views;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels;

public sealed class CollectionImportViewModel : ViewModelBase
{
    private string _assetName = "";
    private string _collectionUrl = "";
    private string _errorMessage = "";
    private bool _hasError;
    private bool _isLoading;
    private WorkshopCollectionInfo? _loadedCollection;
    private bool _collectionLoaded;
    private bool _showImportDetails;
    // Current release policy: collection URL/ID import is disabled.
    public bool ShowSubscribeActions => false;
    
    // GAM file properties
    private string _gamFilePath = "";
    private string _gamFileInfo = "";
    private string _gamErrorMessage = "";
    private bool _hasGamFileInfo;
    private bool _hasGamError;
    private List<string> _gamAddonIds = new();
    private IDisposable? _collectionUrlSubscription;
    private bool _disposed;
    
    public CollectionImportViewModel()
    {
        // URLが変更されたらエラーをクリア
        _collectionUrlSubscription = this.WhenAnyValue(x => x.CollectionUrl)
            .Subscribe(_ => 
            {
                HasError = false;
                ErrorMessage = "";
            });
        
        // コレクション読み込みコマンチE- 常に有効匁E
        LoadCollectionCommand = ReactiveCommand.CreateFromTask(LoadCollectionAsync);
    }
    
    public string AssetName
    {
        get => _assetName;
        set
        {
            this.RaiseAndSetIfChanged(ref _assetName, value);
            this.RaisePropertyChanged(nameof(CanCreate));
        }
    }
    
    public string CollectionUrl
    {
        get => _collectionUrl;
        set => this.RaiseAndSetIfChanged(ref _collectionUrl, value);
    }
    
    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }
    
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }
    
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }
    
    public bool CanLoadCollection => !string.IsNullOrWhiteSpace(CollectionUrl) && !IsLoading;
    
    public bool CanCreate
    {
        get
        {
            // アセット名が入力されていれば作成可能（URLやGAMファイルは任意）
        return !string.IsNullOrWhiteSpace(AssetName);
        }
    }
    
    public ReactiveCommand<Unit, Unit> LoadCollectionCommand { get; }
    
    public WorkshopCollectionInfo? LoadedCollection => _loadedCollection;
    
    public bool CollectionLoaded
    {
        get => _collectionLoaded;
        set => this.RaiseAndSetIfChanged(ref _collectionLoaded, value);
    }
    
    public bool ShowImportDetails
    {
        get => _showImportDetails;
        set => this.RaiseAndSetIfChanged(ref _showImportDetails, value);
    }
    
    private async Task LoadCollectionAsync()
    {
        try
        {
            IsLoading = true;
            HasError = false;
            if (!ShowSubscribeActions)
            {
                ErrorMessage = L.Get("CollectionImport.Error.ModeNotSupported");
                HasError = true;
                return;
            }
            // URLが空の場合はエラー
            if (string.IsNullOrWhiteSpace(CollectionUrl))
            {
                ErrorMessage = L.Get("CollectionImport.Error.EmptyUrl");
                HasError = true;
                return;
            }
            
            // URLからコレクションIDを抽出
            var collectionId = ExtractCollectionId(CollectionUrl);
            if (string.IsNullOrEmpty(collectionId))
            {
                ErrorMessage = L.Get("CollectionImport.Error.InvalidUrl");
                HasError = true;
                return;
            }
            
            var workshopService = ViewModelLocator.SteamWorkshopService;
            if (workshopService == null)
            {
                ErrorMessage = L.Get("CollectionImport.Error.SteamworksNotInitialized");
                HasError = true;
                return;
            }

            // コレクション情報を取得
            var lookupResult = await workshopService.GetCollectionDetailsWithStatusAsync(collectionId);
            if (lookupResult.Status == WorkshopCollectionLookupStatus.NotFound)
            {
                ErrorMessage = L.Get("CollectionImport.Error.CollectionNotFound");
                HasError = true;
                return;
            }

            if (lookupResult.Status == WorkshopCollectionLookupStatus.Unavailable)
            {
                ErrorMessage = L.Get("Error.SteamworksUnavailableMessage");
                HasError = true;
                return;
            }

            var collectionInfo = lookupResult.CollectionInfo;
            if (collectionInfo == null)
            {
                ErrorMessage = L.Format("CollectionImport.Error.Generic", "lookup result was empty");
                HasError = true;
                return;
            }
            
            _loadedCollection = collectionInfo;
            
            // コレクション名をアセット名にセット
            if (string.IsNullOrWhiteSpace(AssetName))
            {
                AssetName = collectionInfo.Title;
            }
            
            // コレクション詳細ダイアログを表示
            await ShowCollectionDetailsAsync();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionImportViewModel.LoadCollectionAsync", ex);
            ErrorMessage = L.Format("CollectionImport.Error.Generic", ex.Message);
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    private string? ExtractCollectionId(string url)
    {
        // Steam Workshop URLからIDを抽出
        // https://steamcommunity.com/sharedfiles/filedetails/?id=XXXXXXXXX
        // https://steamcommunity.com/workshop/filedetails/?id=XXXXXXXXX
        var match = Regex.Match(url, @"[?&]id=(\d+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        
        // 直接IDが?E力された場吁E
        if (Regex.IsMatch(url.Trim(), @"^\d+$"))
        {
            return url.Trim();
        }
        
        return null;
    }
    
    private async Task ShowCollectionDetailsAsync()
    {
        if (_loadedCollection == null) return;
        
        try
        {
            // 現在のCollectionImportDialogを取得してキャッシュ
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var currentDialog = desktop?.Windows.FirstOrDefault(w => w is CollectionImportDialog) as CollectionImportDialog;
            
            if (currentDialog == null || desktop?.MainWindow == null)
                return;
            
            // コレクション情報とコールバックを保存
            var collection = _loadedCollection;
            var callback = currentDialog.OnConfirmWithAddons;
            
            // 現在のダイアログを閉じる
            currentDialog.Close();
            
            // コレクション詳細ダイアログを表示
            var dialog = new CollectionDetailsDialog(collection);
            await dialog.ShowDialog(desktop.MainWindow);
            
            // インポ?Eトが確認された場吁E
            if (dialog.ImportConfirmed)
            {
                // 名前入力ダイアログを表示
                var nameDialog = new Window
                {
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Title = L.Get("CollectionImport.NameDialogTitle")
                };
                
                var panel = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 10
                };
                
                panel.Children.Add(new TextBlock { Text = L.Get("CollectionImport.NameDialogPrompt") });
                
                var nameTextBox = new TextBox 
                { 
                    Text = collection.Title,
                    Watermark = L.Get("CollectionImport.NameDialogPlaceholder")
                };
                panel.Children.Add(nameTextBox);
                
                var buttonPanel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Avalonia.Thickness(0, 20, 0, 0)
                };
                
                var cancelButton = new Button { Content = L.Get("Dialog.Cancel") };
                cancelButton.Click += (s, e) => nameDialog.Close();
                
                var createButton = new Button 
                { 
                    Content = L.Get("Dialog.Create"), 
                    Classes = { "accent" },
                    IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text)
                };
                createButton.Click += (_, __) => _ = HandleCreateButtonClickAsync();

                async Task HandleCreateButtonClickAsync()
                {
                    var assetName = nameTextBox.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(assetName) || callback == null)
                    {
                        return;
                    }

                    try
                    {
                        // コールバックを同期的に実行
                        var callbackTask = callback(assetName, collection.AddonIds);

                        // ダイアログを閉じる
                        nameDialog.Close();

                        // コールバックの完了を待つ
                        await callbackTask;
                    }
                    catch (Exception ex)
                    {
                        SafeFileLogger.TryLogException(
                            "CollectionImportViewModel.ShowCollectionDetailsAsync.CreateAssetCallback",
                            ex);

                        try
                        {
                            nameDialog.Close();
                        }
                        catch (Exception closeEx)
                        {
                            SafeFileLogger.TryLogException(
                                "CollectionImportViewModel.ShowCollectionDetailsAsync.CreateAssetCallback.CloseDialog",
                                closeEx);
                        }

                        var dialogService = new DialogService();
                        await dialogService.ShowErrorAsync(
                            L.Get("Error.Title"),
                            L.Get("Error.AssetCreateFailedGeneric"));
                    }
                }
                
                nameTextBox.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(TextBox.Text))
                    {
                        createButton.IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text);
                    }
                };
                
                buttonPanel.Children.Add(cancelButton);
                buttonPanel.Children.Add(createButton);
                panel.Children.Add(buttonPanel);
                
                nameDialog.Content = panel;
                await nameDialog.ShowDialog(desktop.MainWindow);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionImportViewModel.ShowCollectionDetailsAsync", ex);
            ErrorMessage = L.Format("CollectionImport.Error.Generic", ex.Message);
            HasError = true;
        }
    }
    
    public List<string> SelectedAddonIds
    {
        get
        {
            // GAM??????????????????????????EGAM?????????ID?????
            if (_gamAddonIds.Count > 0)
            {
                return _gamAddonIds;
            }
            
            return _loadedCollection?.AddonIds ?? new List<string>();
        }
    }
    
    // GAM file properties
    public string GamFilePath
    {
        get => _gamFilePath;
        set => this.RaiseAndSetIfChanged(ref _gamFilePath, value);
    }
    
    public string GamFileInfo
    {
        get => _gamFileInfo;
        set => this.RaiseAndSetIfChanged(ref _gamFileInfo, value);
    }
    
    public string GamErrorMessage
    {
        get => _gamErrorMessage;
        set => this.RaiseAndSetIfChanged(ref _gamErrorMessage, value);
    }
    
    public bool HasGamFileInfo
    {
        get => _hasGamFileInfo;
        set => this.RaiseAndSetIfChanged(ref _hasGamFileInfo, value);
    }
    
    public bool HasGamError
    {
        get => _hasGamError;
        set => this.RaiseAndSetIfChanged(ref _hasGamError, value);
    }
    
    public async Task LoadGamFileAsync(string filePath)
    {
        try
        {
            HasGamError = false;
            HasGamFileInfo = false;
            _gamAddonIds.Clear();
            
            // ファイルパスが空の場吁E
            if (string.IsNullOrWhiteSpace(filePath))
            {
                GamErrorMessage = L.Get("CollectionImport.GamError.NoFilePath");
                HasGamError = true;
                return;
            }
            
            // ファイルが存在しない場合
            if (!File.Exists(filePath))
            {
                GamErrorMessage = L.Format("CollectionImport.GamError.FileNotFound", filePath);
                HasGamError = true;
                return;
            }
            
            // 拡張子チェック（警告のみ）
            if (!filePath.EndsWith(".gam", StringComparison.OrdinalIgnoreCase))
            {
                // 警告?E出すが続?E
                GamFileInfo = L.Get("CollectionImport.GamWarning.NotGam");
                HasGamFileInfo = true;
            }
            
            GamFilePath = filePath;
            
            // GAMファイルを読み込む
            var lines = await File.ReadAllLinesAsync(filePath);
            
            string? title = null;
            string? description = null;
            int? count = null;
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                // コメント行?E解?E
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith("# Title:", StringComparison.Ordinal))
                        title = line.Substring("# Title:".Length).Trim();
                    else if (line.StartsWith("# Description:", StringComparison.Ordinal))
                        description = line.Substring("# Description:".Length).Trim();
                    else if (line.StartsWith("# Count:", StringComparison.Ordinal))
                    {
                        var countStr = line.Substring("# Count:".Length).Trim();
                        if (int.TryParse(countStr, out var c))
                            count = c;
                    }
                }
                else
                {
                    // アドオンID?E
                    var addonId = line.Trim();
                    if (!string.IsNullOrEmpty(addonId) && Regex.IsMatch(addonId, @"^\d+$"))
                    {
                        _gamAddonIds.Add(addonId);
                    }
                }
            }
            
            // 情報を表示
            var info = HasGamFileInfo ? GamFileInfo : ""; // 既存?E警告を保持
            if (!string.IsNullOrEmpty(title))
            {
                info += L.Format("CollectionImport.GamInfo.Title", title);
                // アセット名が空の場合はタイトルをセット
                if (string.IsNullOrWhiteSpace(AssetName))
                {
                    AssetName = title;
                }
            }
            if (!string.IsNullOrEmpty(description))
                info += L.Format("CollectionImport.GamInfo.Description", description);
            
            info += L.Format("CollectionImport.GamInfo.AddonCount", _gamAddonIds.Count);
            
            if (count.HasValue && count.Value != _gamAddonIds.Count)
            {
                info += L.Format("CollectionImport.GamInfo.RecordedCount", count.Value);
            }
            
            GamFileInfo = info;
            HasGamFileInfo = true;
            
            // 作?Eボタンを有効にするため
            this.RaisePropertyChanged(nameof(CanCreate));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionImportViewModel.LoadGamFileAsync", ex);
            GamErrorMessage = L.Format("CollectionImport.GamError.ReadFailed", ex.Message);
            HasGamError = true;
        }
    }

    public void Release()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _collectionUrlSubscription?.Dispose();
        _collectionUrlSubscription = null;
    }
}

