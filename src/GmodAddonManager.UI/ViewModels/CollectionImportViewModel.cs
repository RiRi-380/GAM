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
using System.Collections.Generic;
using System.IO;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.ViewModels;

public class CollectionImportViewModel : ViewModelBase
{
    private string _assetName = "";
    private string _collectionUrl = "";
    private string _errorMessage = "";
    private bool _hasError;
    private bool _isLoading;
    private SteamworksManager.CollectionInfo? _loadedCollection;
    private bool _collectionLoaded;
    private bool _showImportDetails;
    
    // GAM file properties
    private string _gamFilePath = "";
    private string _gamFileInfo = "";
    private string _gamErrorMessage = "";
    private bool _hasGamFileInfo;
    private bool _hasGamError;
    private List<string> _gamAddonIds = new();
    
    public CollectionImportViewModel()
    {
        // URLが変更されたらエラーをクリア
        this.WhenAnyValue(x => x.CollectionUrl)
            .Subscribe(_ => 
            {
                HasError = false;
                ErrorMessage = "";
            });
        
        // コレクション読み込みコマンド - 常に有効化
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
    
    public SteamworksManager.CollectionInfo? LoadedCollection => _loadedCollection;
    
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
            
            // URLが空の場合はエラー
            if (string.IsNullOrWhiteSpace(CollectionUrl))
            {
                ErrorMessage = "コレクションURLを入力してください。";
                HasError = true;
                return;
            }
            
            // URLからコレクションIDを抽出
            var collectionId = ExtractCollectionId(CollectionUrl);
            if (string.IsNullOrEmpty(collectionId))
            {
                ErrorMessage = "無効なコレクションURLです。Steam WorkshopのコレクションURLを入力してください。";
                HasError = true;
                return;
            }
            
            // Steamworks Managerを取得
            var steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
            if (steamworksManager == null || !steamworksManager.IsInitialized)
            {
                ErrorMessage = "Steamworks SDKが初期化されていません。";
                HasError = true;
                return;
            }
            
            // コレクション情報を取得
            var collectionInfo = await steamworksManager.GetCollectionInfoAsync(collectionId);
            if (collectionInfo == null)
            {
                ErrorMessage = "コレクションが見つかりません。URLが正しいか確認してください。";
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
            ErrorMessage = $"エラーが発生しました: {ex.Message}";
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
        
        // 直接IDが入力された場合
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
            
            // インポートが確認された場合
            if (dialog.ImportConfirmed)
            {
                // 名前入力ダイアログを表示
                var nameDialog = new Window
                {
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Title = "アセット名を入力"
                };
                
                var panel = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 10
                };
                
                panel.Children.Add(new TextBlock { Text = "アセット名を入力してください：" });
                
                var nameTextBox = new TextBox 
                { 
                    Text = collection.Title,
                    Watermark = "アセット名"
                };
                panel.Children.Add(nameTextBox);
                
                var buttonPanel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Avalonia.Thickness(0, 20, 0, 0)
                };
                
                var cancelButton = new Button { Content = "キャンセル" };
                cancelButton.Click += (s, e) => nameDialog.Close();
                
                var createButton = new Button 
                { 
                    Content = "作成", 
                    Classes = { "accent" },
                    IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text)
                };
                createButton.Click += async (s, e) =>
                {
                    var assetName = nameTextBox.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(assetName) && callback != null)
                    {
                        try
                        {
                            // コールバックを先に実行
                            var callbackTask = callback(assetName, collection.AddonIds);
                            
                            // ダイアログを閉じる
                            nameDialog.Close();
                            
                            // コールバックの完了を待つ
                            await callbackTask;
                        }
                        catch (Exception ex)
                        {
                            nameDialog.Close();
                        }
                    }
                };
                
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
        }
    }
    
    public List<string> GetSelectedAddonIds()
    {
        // GAMファイルからの読み込みの場合はGAMのアドオンIDを返す
        if (_gamAddonIds.Count > 0)
        {
            return _gamAddonIds;
        }
        
        return _loadedCollection?.AddonIds ?? new List<string>();
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
            
            // ファイルパスが空の場合
            if (string.IsNullOrWhiteSpace(filePath))
            {
                GamErrorMessage = "ファイルパスが指定されていません。";
                HasGamError = true;
                return;
            }
            
            // ファイルが存在しない場合
            if (!File.Exists(filePath))
            {
                GamErrorMessage = $"ファイルが見つかりません: {filePath}";
                HasGamError = true;
                return;
            }
            
            // 拡張子チェック（警告のみ）
            if (!filePath.EndsWith(".gam", StringComparison.OrdinalIgnoreCase))
            {
                // 警告は出すが続行
                GamFileInfo = "警告: GAMファイル(.gam)ではありません。\n";
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
                
                // コメント行の解析
                if (line.StartsWith("#"))
                {
                    if (line.StartsWith("# Title:"))
                        title = line.Substring("# Title:".Length).Trim();
                    else if (line.StartsWith("# Description:"))
                        description = line.Substring("# Description:".Length).Trim();
                    else if (line.StartsWith("# Count:"))
                    {
                        var countStr = line.Substring("# Count:".Length).Trim();
                        if (int.TryParse(countStr, out var c))
                            count = c;
                    }
                }
                else
                {
                    // アドオンID行
                    var addonId = line.Trim();
                    if (!string.IsNullOrEmpty(addonId) && Regex.IsMatch(addonId, @"^\d+$"))
                    {
                        _gamAddonIds.Add(addonId);
                    }
                }
            }
            
            // 情報を表示
            var info = HasGamFileInfo ? GamFileInfo : ""; // 既存の警告を保持
            if (!string.IsNullOrEmpty(title))
            {
                info += $"タイトル: {title}\n";
                // アセット名が空の場合はタイトルをセット
                if (string.IsNullOrWhiteSpace(AssetName))
                {
                    AssetName = title;
                }
            }
            if (!string.IsNullOrEmpty(description))
                info += $"説明: {description}\n";
            
            info += $"アドオン数: {_gamAddonIds.Count}個";
            
            if (count.HasValue && count.Value != _gamAddonIds.Count)
            {
                info += $" (記録されている数: {count}個)";
            }
            
            GamFileInfo = info;
            HasGamFileInfo = true;
            
            // 作成ボタンを有効にするため
            this.RaisePropertyChanged(nameof(CanCreate));
        }
        catch (Exception ex)
        {
            GamErrorMessage = $"ファイルの読み込みに失敗しました: {ex.Message}";
            HasGamError = true;
        }
    }
}