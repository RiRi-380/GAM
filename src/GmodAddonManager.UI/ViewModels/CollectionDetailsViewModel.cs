using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels;

public class CollectionDetailsViewModel : ViewModelBase
{
    private readonly SteamworksManager.CollectionInfo _collectionInfo;
    private readonly SteamworksManager? _steamworksManager;
    private bool _isLoading;
    private Bitmap? _collectionImageBitmap;
    private int _currentOffset = 0;
    private const int PageSize = 50;
    private bool _hasMoreItems;
    private bool _isLoadingMore;
    
    public CollectionDetailsViewModel(SteamworksManager.CollectionInfo collectionInfo)
    {
        _collectionInfo = collectionInfo;
        _steamworksManager = (Avalonia.Application.Current as App)?.SteamworksManager;
        
        CollectionTitle = collectionInfo.Title;
        CollectionDescription = collectionInfo.Description;
        AddonCountText = $"{collectionInfo.AddonIds.Count} 個のアドオン";
        
        _ = LoadCollectionImageAsync();
    }
    
    public ObservableCollection<CollectionAddonViewModel> Addons { get; } = new();
    
    public string CollectionTitle { get; }
    public string CollectionDescription { get; }
    public string AddonCountText { get; }
    
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(CanImport));
        }
    }
    
    public bool HasMoreItems
    {
        get => _hasMoreItems;
        set => this.RaiseAndSetIfChanged(ref _hasMoreItems, value);
    }
    
    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLoadingMore, value);
            this.RaisePropertyChanged(nameof(CanImport));
        }
    }
    
    public Bitmap? CollectionImageBitmap
    {
        get => _collectionImageBitmap;
        set => this.RaiseAndSetIfChanged(ref _collectionImageBitmap, value);
    }
    
    public bool CanImport => Addons.Count > 0 && !IsLoading && !IsLoadingMore;
    
    private async Task LoadCollectionImageAsync()
    {
        if (!string.IsNullOrEmpty(_collectionInfo.PreviewUrl))
        {
            CollectionImageBitmap = await RemoteImageLoader.LoadFromUrlAsync(_collectionInfo.PreviewUrl);
        }
    }
    
    public async Task LoadAddonsAsync()
    {
        if (_steamworksManager == null || !_steamworksManager.IsInitialized)
            return;
        
        try
        {
            IsLoading = true;
            Addons.Clear();
            _currentOffset = 0;
            
            // 最初の50件を取得
            await LoadMoreAddonsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    public async Task LoadMoreAddonsAsync()
    {
        if (_steamworksManager == null || !_steamworksManager.IsInitialized)
            return;
        
        if (IsLoadingMore || _currentOffset >= _collectionInfo.AddonIds.Count)
            return;
        
        try
        {
            IsLoadingMore = true;
            
            // 次の50件を取得
            var addonInfos = await _steamworksManager.GetWorkshopItemsBatchAsync(
                _collectionInfo.AddonIds, 
                _currentOffset, 
                PageSize
            );
            
            var subscribedItems = _steamworksManager.GetSubscribedItems();
            
            foreach (var addonInfo in addonInfos)
            {
                var vm = new CollectionAddonViewModel
                {
                    Id = addonInfo.Id,
                    Title = addonInfo.Title,
                    Author = addonInfo.Author,
                    FileSize = addonInfo.FileSize,
                    PreviewUrl = addonInfo.PreviewUrl
                };
                
                // サブスクライブ状態を確認
                vm.IsSubscribed = subscribedItems.Contains(addonInfo.Id);
                
                Addons.Add(vm);
                
                // 画像を非同期で読み込み
                _ = vm.LoadThumbnailAsync();
            }
            
            _currentOffset += addonInfos.Count;
            HasMoreItems = _currentOffset < _collectionInfo.AddonIds.Count;
            
            this.RaisePropertyChanged(nameof(CanImport));
        }
        catch (Exception ex)
        {
        }
        finally
        {
            IsLoadingMore = false;
        }
    }
}

public class CollectionAddonViewModel : ViewModelBase
{
    private Bitmap? _thumbnailBitmap;
    private bool _isSubscribed;
    
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public ulong FileSize { get; set; }
    public string PreviewUrl { get; set; } = "";
    
    public Bitmap? ThumbnailBitmap
    {
        get => _thumbnailBitmap;
        set => this.RaiseAndSetIfChanged(ref _thumbnailBitmap, value);
    }
    
    public bool IsSubscribed
    {
        get => _isSubscribed;
        set => this.RaiseAndSetIfChanged(ref _isSubscribed, value);
    }
    
    public string SizeText => FormatFileSize(FileSize);
    
    public string StatusText => IsSubscribed ? "サブスクライブ済み" : "未サブスクライブ";
    
    public IBrush StatusColor => IsSubscribed 
        ? new SolidColorBrush(Color.FromRgb(0, 120, 212))  // Blue
        : new SolidColorBrush(Color.FromRgb(107, 107, 107)); // Gray
    
    public async Task LoadThumbnailAsync()
    {
        if (!string.IsNullOrEmpty(PreviewUrl))
        {
            ThumbnailBitmap = await RemoteImageLoader.LoadFromUrlAsync(PreviewUrl);
        }
    }
    
    private string FormatFileSize(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}