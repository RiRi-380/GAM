using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Models;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace GmodAddonManager.UI.ViewModels;

public class AddonItemViewModel : ViewModelBase
{
    private readonly WorkshopAddon addon;
    private readonly AddonManager addonManager;
    // private readonly ILogger logger; // Removed logging
    

    private bool isSelected;
    private bool isDetailsLoaded;
    private string? thumbnailPath;
    private long fileSize;
    private DateTime lastModified;
    private string? thumbnailUrl;
    private bool isThumbnailLoading = true;
    private AddonState? currentAddonState;
    private AssetItemViewModel? currentAsset;
    private string? notes;
    private bool isFavorite;
    private Bitmap? thumbnailBitmap;

    public AddonItemViewModel(
        WorkshopAddon addon,
        AddonManager addonManager,
        object? logger = null) // logger parameter kept for compatibility but not used
    {
        this.addon = addon;
        this.addonManager = addonManager;
        // this.logger = logger; // Removed logging

        // 基本情報の設定
        AddonId = addon.Id;
        // NeedsTitleUpdateがtrueの場合は"Loading..."を表示
        if (addon.NeedsTitleUpdate)
        {
            title = "Loading...";
        }
        else
        {
            title = string.IsNullOrWhiteSpace(addon.Title) ? $"Workshop-{addon.Id}" : addon.Title;
        }
        FolderPath = addon.FolderPath;
        
        // ファイル情報の初期化
        if (Directory.Exists(FolderPath))
        {
            var dirInfo = new DirectoryInfo(FolderPath);
            LastModified = dirInfo.LastWriteTime;
            CalculateFileSize();
        }

        // コマンドの初期化
        LoadDetailsCommand = ReactiveCommand.CreateFromTask(LoadDetailsAsync);
        OpenFolderCommand = ReactiveCommand.Create(OpenFolder);
        LoadThumbnailCommand = ReactiveCommand.CreateFromTask(LoadThumbnailAsync);
        OpenWorkshopCommand = ReactiveCommand.Create(OpenWorkshopPage);
        CopyWorkshopUrlCommand = ReactiveCommand.Create(CopyWorkshopUrl);
        SaveNotesCommand = ReactiveCommand.Create(SaveNotes);
        ToggleFavoriteCommand = ReactiveCommand.Create(ToggleFavorite);
        
        // サムネイルURLの初期読み込みを開始
        _ = LoadThumbnailUrlAsync();
        
        // メモの読み込み
        LoadNotes();
        
        // お気に入り状態の初期化
        isFavorite = addon.IsFavorite;
    }

    public string AddonId { get; }
    public string Title 
    { 
        get => title;
        private set => SetAndRaise(ref title, value);
    }
    private string title = "";
    
    // タイトルを外部から更新するためのメソッド
    public void UpdateTitle(string newTitle)
    {
        Title = newTitle;
    }
    
    // WorkshopAddonから情報を更新するメソッド
    public void UpdateFromWorkshopAddon(WorkshopAddon workshopAddon)
    {
        // タイトルの更新
        if (!string.IsNullOrEmpty(workshopAddon.Title))
        {
            Title = workshopAddon.Title;
        }
        
        // 内部のaddonオブジェクトを更新
        addon.Size = workshopAddon.Size;
        addon.LastUpdated = workshopAddon.LastUpdated;
        addon.Description = workshopAddon.Description;
        addon.Author = workshopAddon.Author;
        addon.ThumbnailUrl = workshopAddon.ThumbnailUrl;
        addon.Tags = workshopAddon.Tags;
        
        // プロパティ変更通知
        this.RaisePropertyChanged(nameof(FileSizeText));
        this.RaisePropertyChanged(nameof(LastModifiedText));
        this.RaisePropertyChanged(nameof(TimeCreatedText));
        this.RaisePropertyChanged(nameof(TimeUpdatedText));
        this.RaisePropertyChanged(nameof(Tags));
        
        // サムネイルURLが変更された場合は再読み込み
        if (!string.IsNullOrEmpty(workshopAddon.ThumbnailUrl) && workshopAddon.ThumbnailUrl != addon.ThumbnailUrl)
        {
            ThumbnailUrl = workshopAddon.ThumbnailUrl;
            _ = LoadThumbnailAsync();
        }
    }
    public string? FolderPath { get; }
    
    public bool IsGmaFile => addon.IsGmaFile;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            SetAndRaise(ref isSelected, value);
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }

    public bool IsDetailsLoaded
    {
        get => isDetailsLoaded;
        private set => SetAndRaise(ref isDetailsLoaded, value);
    }

    public string? ThumbnailPath
    {
        get => thumbnailPath;
        private set => SetAndRaise(ref thumbnailPath, value);
    }

    public long FileSize
    {
        get => fileSize;
        private set => SetAndRaise(ref fileSize, value);
    }

    public DateTime LastModified
    {
        get => lastModified;
        private set => SetAndRaise(ref lastModified, value);
    }

    public string? ThumbnailUrl
    {
        get => thumbnailUrl;
        private set
        {
            SetAndRaise(ref thumbnailUrl, value);
            
            // URLが設定されたら画像をダウンロード
            if (!string.IsNullOrEmpty(value))
            {
                _ = LoadBitmapFromUrlAsync(value);
            }
        }
    }
    
    public Bitmap? ThumbnailBitmap
    {
        get => thumbnailBitmap;
        private set => SetAndRaise(ref thumbnailBitmap, value);
    }

    public bool IsThumbnailLoading
    {
        get => isThumbnailLoading;
        private set => SetAndRaise(ref isThumbnailLoading, value);
    }

    public string FileSizeText => FormatFileSize(FileSize);
    public string LastModifiedText => LastModified.ToString("yyyy/MM/dd HH:mm");

    public ReactiveCommand<Unit, Unit> LoadDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadThumbnailCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenWorkshopCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyWorkshopUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveNotesCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }
    
    // Workshop関連プロパティ
    public bool HasWorkshopId => !string.IsNullOrEmpty(AddonId) && AddonId != "0";
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={AddonId}";
    public string? WorkshopId => HasWorkshopId ? AddonId : null;
    
    // 詳細情報プロパティ
    public string? Author => addon.Author;
    public string? Description => addon.Description;
    public string? Type => addon.Type;
    public string[]? Tags => addon.Tags;
    public DateTime? TimeCreated { get; private set; }
    public DateTime? TimeUpdated { get; private set; }
    public string? TimeCreatedText => TimeCreated?.ToString("yyyy/MM/dd HH:mm");
    public string? TimeUpdatedText => TimeUpdated?.ToString("yyyy/MM/dd HH:mm");
    
    // アセット情報
    public ObservableCollection<string> AssetMemberships { get; } = new();
    
    // 技術情報
    public ObservableCollection<string> FileList { get; } = new();
    public ObservableCollection<FileTreeNode> FileTree { get; } = new();
    public string StateText => currentAddonState?.ToString() ?? "Unknown";
    public string IsGmaFileText => IsGmaFile ? L.Get("Common.Yes") : L.Get("Common.No");
    
    // メモ
    public string? Notes
    {
        get => notes;
        set => SetAndRaise(ref notes, value);
    }
    
    // お気に入り
    public bool IsFavorite
    {
        get => isFavorite;
        set => SetAndRaise(ref isFavorite, value);
    }
    
    // 現在のアセットを設定
    public void SetCurrentAsset(AssetItemViewModel? asset)
    {
        currentAsset = asset;
        UpdateAddonState();
        
        // 詳細が読み込まれている場合は、アセット情報を更新
        if (IsDetailsLoaded)
        {
            UpdateAssetMemberships();
        }
    }
    
    // アドオンの状態を更新
    public void UpdateAddonState()
    {
        if (currentAsset == null) return;
        
        // 現在のアセットでの状態を取得
        currentAddonState = currentAsset.GetAddonState(AddonId);
        
        // プロパティ変更通知
        this.RaisePropertyChanged(nameof(BorderColor));
        this.RaisePropertyChanged(nameof(IsExcludedAnywhere));
    }
    
    // 枠線の色を決定
    public string BorderColor
    {
        get
        {
            // 選択されている場合は青色
            if (IsSelected)
            {
                return "#4A90E2"; // アクセントカラー（青）
            }
            
            // どこかで除外されているかチェック
            if (IsExcludedAnywhere)
            {
                return "#F44336"; // 赤
            }
            
            // 現在のアセットでの状態
            if (currentAddonState == AddonState.Disabled)
            {
                return "#FF9800"; // オレンジ
            }
            
            return "#303030"; // デフォルトはダークグレー
        }
    }
    
    // どこかのアセットで除外されているかチェック
    public bool IsExcludedAnywhere
    {
        get
        {
            var config = addonManager.GetConfiguration();
            foreach (var asset in config.Assets)
            {
                if (asset.Addons.Contains(AddonId) || asset.ContainsAllAddons())
                {
                    if (asset.GetAddonState(AddonId) == AddonState.Excluded)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    private async Task LoadDetailsAsync()
    {
        if (IsDetailsLoaded) return;

        try
        {
            // GMAファイルから詳細情報を読み込み
            if (FolderPath != null)
            {
                var details = await addonManager.ScanAddonAsync(FolderPath);
                if (details != null)
                {
                    if (!string.IsNullOrWhiteSpace(details.Title))
                    {
                        Title = details.Title;
                        addon.Title = details.Title;
                    }
                    addon.Author = details.Author;
                    addon.Description = details.Description;
                    addon.Type = details.Type;
                    addon.Tags = details.Tags;

                    // サムネイルのパスを探す（将来的な実装用）
                    var thumbnailFile = Path.Combine(FolderPath, "addon.jpg");
                    if (File.Exists(thumbnailFile))
                    {
                        ThumbnailPath = thumbnailFile;
                    }
                }
            }
            
            // タイトルがまだWorkshop形式の場合、HybridWorkshopServiceから取得を試みる
            if (Title.StartsWith("Workshop-") && ViewModelLocator.HybridWorkshopService != null)
            {
                try
                {
                    var workshopDetails = await ViewModelLocator.HybridWorkshopService.GetWorkshopDetailsAsync(AddonId);
                    if (workshopDetails != null && !string.IsNullOrWhiteSpace(workshopDetails.Title))
                    {
                        Title = workshopDetails.Title;
                        addon.Title = workshopDetails.Title;
                        if (!string.IsNullOrWhiteSpace(workshopDetails.Description))
                        {
                            addon.Description = workshopDetails.Description;
                        }
                        // logger.LogInformation($"Retrieved title from Steam API for addon {AddonId}: {Title}"); // Removed logging
                    }
                }
                catch (Exception)
                {
                    // logger.LogWarning($"Failed to get title from Steam API for addon {AddonId}: {apiEx.Message}"); // Removed logging
                }
            }

            // Workshop情報を取得
            await LoadWorkshopDetailsAsync();
            
            // ファイルツリーを作成
            LoadFileTree();
            
            // アセット情報を更新
            UpdateAssetMemberships();
            
            IsDetailsLoaded = true;
            // logger.LogDebug($"Loaded details for addon {AddonId}"); // Removed logging
        }
        catch (Exception)
        {
            // logger.LogError($"Failed to load addon details for {AddonId}", ex); // Removed logging
        }
    }
    
    private async Task LoadWorkshopDetailsAsync()
    {
        try
        {
            var workshopService = addonManager.GetSteamWorkshopService();
            var details = await workshopService.GetWorkshopDetailsAsync(AddonId);
            
            if (details != null)
            {
                TimeCreated = DateTimeOffset.FromUnixTimeSeconds(details.TimeCreated).DateTime;
                TimeUpdated = DateTimeOffset.FromUnixTimeSeconds(details.TimeUpdated).DateTime;
                this.RaisePropertyChanged(nameof(TimeCreated));
                this.RaisePropertyChanged(nameof(TimeUpdated));
                this.RaisePropertyChanged(nameof(TimeCreatedText));
                this.RaisePropertyChanged(nameof(TimeUpdatedText));
            }
        }
        catch
        {
            // Workshop情報の取得に失敗しても継続
        }
    }
    
    private void LoadFileTree()
    {
        try
        {
            FileTree.Clear();
            
            if (Directory.Exists(FolderPath))
            {
                var dirInfo = new DirectoryInfo(FolderPath);
                var rootNode = CreateFileTreeNode(dirInfo, dirInfo.FullName.Length + 1);
                
                // ルートノードの子要素を直接追加
                foreach (var child in rootNode.Children)
                {
                    FileTree.Add(child);
                }
            }
        }
        catch
        {
            // ファイルツリーの取得に失敗
        }
    }
    
    private FileTreeNode CreateFileTreeNode(DirectoryInfo directory, int basePathLength, int depth = 0)
    {
        var node = new FileTreeNode
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true
        };
        
        // 深さ制限（3階層まで）
        if (depth >= 3)
        {
            node.Name += " ...";
            return node;
        }
        
        try
        {
            // サブディレクトリ
            foreach (var subDir in directory.GetDirectories().OrderBy(d => d.Name))
            {
                node.Children.Add(CreateFileTreeNode(subDir, basePathLength, depth + 1));
            }
            
            // ファイル（最大20ファイル）
            var files = directory.GetFiles().OrderBy(f => f.Name).Take(20).ToList();
            foreach (var file in files)
            {
                node.Children.Add(new FileTreeNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length
                });
            }
            
            if (directory.GetFiles().Count() > 20)
            {
                node.Children.Add(new FileTreeNode
                {
                    Name = "... and more files",
                    IsDirectory = false
                });
            }
        }
        catch
        {
            // アクセスエラーは無視
        }
        
        return node;
    }
    
    private void UpdateAssetMemberships()
    {
        try
        {
            AssetMemberships.Clear();
            
            var config = addonManager.GetConfiguration();
            var addedAssets = new HashSet<string>(); // 重複チェック用
            
            foreach (var asset in config.Assets)
            {
                // 現在のアセットは表示しない（重複を避けるため）
                if (currentAsset != null && asset.Name == currentAsset.Name)
                {
                    continue;
                }
                
                // アセットがすべてのアドオンを含む場合、または個別にこのアドオンを含む場合
                if (asset.ContainsAllAddons() || asset.Addons.Contains(AddonId))
                {
                    // 重複を防ぐ
                    if (!addedAssets.Contains(asset.Name))
                    {
                        AssetMemberships.Add(asset.Name);
                        addedAssets.Add(asset.Name);
                    }
                }
            }
        }
        catch
        {
            // アセット情報の取得に失敗
        }
    }

    private void CalculateFileSize()
    {
        try
        {
            long totalSize = 0;
            var dirInfo = new DirectoryInfo(FolderPath);
            
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                totalSize += file.Length;
            }
            
            FileSize = totalSize;
        }
        catch (Exception ex)
        {
            // logger.LogError($"Failed to calculate file size for {AddonId}", ex); // Removed logging
            FileSize = 0;
        }
    }

    private void OpenFolder()
    {
        try
        {
            if (FolderPath != null && Directory.Exists(FolderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = FolderPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            // logger.LogError($"Failed to open folder for {AddonId}", ex); // Removed logging
        }
    }

    private static string FormatFileSize(long bytes)
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

    private async Task LoadThumbnailAsync()
    {
        // gmpublisherと同じ方式：LoadThumbnailUrlAsyncを呼び出すだけ
        await LoadThumbnailUrlAsync();
    }
    
    private async Task LoadThumbnailUrlAsync()
    {
        try
        {
            
            // 1. ローカルファイルをチェック (addon.jpg)
            if (!string.IsNullOrEmpty(FolderPath))
            {
                try
                {
                    var jpgPath = Path.Combine(FolderPath, "addon.jpg");
                    if (File.Exists(jpgPath))
                    {
                        // file://スキームで設定（gmpublisherと同じ）
                        ThumbnailUrl = new Uri(jpgPath).AbsoluteUri;
                        IsThumbnailLoading = false;
                        return;
                    }
                    
                    // addon.pngもチェック
                    var pngPath = Path.Combine(FolderPath, "addon.png");
                    if (File.Exists(pngPath))
                    {
                        ThumbnailUrl = new Uri(pngPath).AbsoluteUri;
                        IsThumbnailLoading = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                }
            }
            
            // 2. WorkshopからプレビューURLを取得（Steamworks SDK優先）
            if (HasWorkshopId)
            {
                try
                {
                    var hybridService = ViewModelLocator.HybridWorkshopService;
                    if (hybridService != null)
                    {
                        
                        var details = await hybridService.GetWorkshopDetailsAsync(AddonId);
                        if (details != null)
                        {
                            if (!string.IsNullOrEmpty(details.PreviewUrl))
                            {
                                // CDN直リンクを設定（gmpublisherと同じ）
                                ThumbnailUrl = details.PreviewUrl;
                                IsThumbnailLoading = false;
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                }
            }
            
            // 画像なし
            ThumbnailUrl = null;
            IsThumbnailLoading = false;
        }
        catch (Exception ex)
        {
            // エラー時は画像なし
            ThumbnailUrl = null;
            IsThumbnailLoading = false;
        }
    }

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var lowerFilter = filter.ToLower();
        
        // タイトルが"Loading..."の場合は、元のアドオンタイトルも検索対象にする
        bool titleMatches = Title.ToLower().Contains(lowerFilter);
        if (!titleMatches && addon.Title != null && addon.Title != Title)
        {
            titleMatches = addon.Title.ToLower().Contains(lowerFilter);
        }
        
        // GMAファイルの場合、詳細が読み込まれていなければIDとタイトルのみでフィルタ
        if (IsGmaFile && !IsDetailsLoaded)
        {
            return titleMatches || AddonId.Contains(lowerFilter);
        }
        
        return titleMatches ||
               AddonId.Contains(lowerFilter) ||
               (addon.Author?.ToLower().Contains(lowerFilter) ?? false) ||
               (addon.Description?.ToLower().Contains(lowerFilter) ?? false);
    }
    
    private void OpenWorkshopPage()
    {
        try
        {
            if (HasWorkshopId)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WorkshopUrl,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // ブラウザの起動に失敗
        }
    }
    
    private void CopyWorkshopUrl()
    {
        try
        {
            if (HasWorkshopId && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                clipboard?.SetTextAsync(WorkshopUrl).Wait();
                
                var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
                errorHandler?.HandleInfo(L.Get("AddonDetails.UrlCopied"), "AddonDetails");
            }
        }
        catch
        {
            // クリップボード操作に失敗
        }
    }
    
    
    private void LoadNotes()
    {
        try
        {
            var notesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "notes"
            );
            
            if (!Directory.Exists(notesDir))
                Directory.CreateDirectory(notesDir);
            
            var notesFile = Path.Combine(notesDir, $"{AddonId}.txt");
            if (File.Exists(notesFile))
            {
                Notes = File.ReadAllText(notesFile);
            }
        }
        catch
        {
            // メモの読み込みに失敗
        }
    }
    
    private void SaveNotes()
    {
        try
        {
            var notesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "notes"
            );
            
            if (!Directory.Exists(notesDir))
                Directory.CreateDirectory(notesDir);
            
            var notesFile = Path.Combine(notesDir, $"{AddonId}.txt");
            
            if (string.IsNullOrWhiteSpace(Notes))
            {
                if (File.Exists(notesFile))
                    File.Delete(notesFile);
            }
            else
            {
                File.WriteAllText(notesFile, Notes);
            }
            
            var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
            errorHandler?.HandleInfo(L.Get("AddonDetails.NotesSaved"), "AddonDetails");
        }
        catch
        {
            // メモの保存に失敗
        }
    }
    
    private void ToggleFavorite()
    {
        try
        {
            IsFavorite = !IsFavorite;
            addon.IsFavorite = IsFavorite;
            
            // 設定を保存
            Task.Run(async () => await addonManager.SaveConfigurationAsync());
            
            var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
            if (IsFavorite)
            {
                errorHandler?.HandleInfo(L.Get("AddonDetails.AddedToFavorites"), "AddonDetails");
            }
            else
            {
                errorHandler?.HandleInfo(L.Get("AddonDetails.RemovedFromFavorites"), "AddonDetails");
            }
        }
        catch
        {
            // お気に入り切り替えに失敗
        }
    }
    
    private async Task LoadBitmapFromUrlAsync(string url)
    {
        try
        {
            var bitmap = await RemoteImageLoader.LoadFromUrlAsync(url);
            if (bitmap != null)
            {
                ThumbnailBitmap = bitmap;
            }
        }
        catch
        {
            // 画像読み込みエラーは無視
        }
    }
}