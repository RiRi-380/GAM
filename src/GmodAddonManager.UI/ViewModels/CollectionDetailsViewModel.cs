using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels;

public sealed class CollectionDetailsViewModel : ViewModelBase
{
    private readonly WorkshopCollectionInfo _collectionInfo;
    private readonly SteamWorkshopService? _workshopService;
    private readonly HybridWorkshopService? _hybridService;
    private HashSet<string> _subscribedIds = new(StringComparer.Ordinal);
    private bool _isLoading;
    private Bitmap? _collectionImageBitmap;
    private int _currentOffset;
    private const int PageSize = 50;
    private bool _hasMoreItems;
    private bool _isLoadingMore;
    private bool _disposed;

    public CollectionDetailsViewModel(WorkshopCollectionInfo collectionInfo)
    {
        _collectionInfo = collectionInfo;
        _workshopService = ViewModelLocator.SteamWorkshopService;
        _hybridService = ViewModelLocator.HybridWorkshopService;

        CollectionTitle = collectionInfo.Title;
        CollectionDescription = collectionInfo.Description;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;

        _ = LoadCollectionImageAsync();
    }

    public ObservableCollection<CollectionAddonViewModel> Addons { get; } = new();

    public string CollectionTitle { get; }
    public string CollectionDescription { get; }
    public string AddonCountText => L.Format("Collection.AddonCountFormat", _collectionInfo.AddonIds.Count);
    // Current release policy: collection URL/ID import path remains disabled.
    public bool ShowSubscribeActions => false;

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
        set
        {
            if (ReferenceEquals(_collectionImageBitmap, value))
            {
                return;
            }

            _collectionImageBitmap?.Dispose();
            this.RaiseAndSetIfChanged(ref _collectionImageBitmap, value);
        }
    }

    public bool CanImport => Addons.Count > 0 && !IsLoading && !IsLoadingMore;

    private async Task LoadCollectionImageAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_collectionInfo.PreviewUrl) &&
                Uri.TryCreate(_collectionInfo.PreviewUrl, UriKind.Absolute, out var uri))
            {
                var bitmap = await RemoteImageLoader.LoadFromUrlAsync(uri);
                if (_disposed)
                {
                    bitmap?.Dispose();
                    return;
                }

                CollectionImageBitmap = bitmap;
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionDetailsViewModel.LoadCollectionImageAsync", ex);
        }
    }

    public async Task LoadAddonsAsync()
    {
        if (_workshopService == null)
        {
            return;
        }

        try
        {
            IsLoading = true;
            DisposeCollectionAddonItems();
            Addons.Clear();
            _currentOffset = 0;

            if (_hybridService != null)
            {
                var subscribed = await _hybridService.GetSubscribedItemsAsync();
                _subscribedIds = new HashSet<string>(subscribed, StringComparer.Ordinal);
            }

            await LoadMoreAddonsAsync();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionDetailsViewModel.LoadAddonsAsync", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadMoreAddonsAsync()
    {
        if (_workshopService == null)
        {
            return;
        }

        if (IsLoadingMore || _currentOffset >= _collectionInfo.AddonIds.Count)
        {
            return;
        }

        try
        {
            IsLoadingMore = true;

            var pageIds = _collectionInfo.AddonIds
                .Skip(_currentOffset)
                .Take(PageSize)
                .ToList();

            var detailsMap = await _workshopService.GetWorkshopDetailsBatchAsync(pageIds);
            foreach (var id in pageIds)
            {
                detailsMap.TryGetValue(id, out var details);
                var vm = new CollectionAddonViewModel
                {
                    Id = id,
                    Title = details?.Title ?? AddonTitleHelper.BuildPlaceholderTitle(id),
                    Author = details?.Creator ?? string.Empty,
                    FileSize = details?.FileSize ?? 0,
                    PreviewUrl = details?.PreviewUrl ?? string.Empty
                };

                vm.IsSubscribed = _subscribedIds.Contains(id);

                if (_disposed)
                {
                    vm.Release();
                    continue;
                }

                Addons.Add(vm);
                _ = vm.LoadThumbnailAsync();
            }

            _currentOffset += pageIds.Count;
            HasMoreItems = _currentOffset < _collectionInfo.AddonIds.Count;

            this.RaisePropertyChanged(nameof(CanImport));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionDetailsViewModel.LoadMoreAddonsAsync", ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
        {
            this.RaisePropertyChanged(nameof(AddonCountText));
            foreach (var addon in Addons)
            {
                addon.NotifyLanguageChanged();
            }
        }
    }

    public void Release()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        DisposeCollectionAddonItems();
        Addons.Clear();
        CollectionImageBitmap = null;
    }

    private void DisposeCollectionAddonItems()
    {
        foreach (var addon in Addons)
        {
            addon.Release();
        }
    }
}

public sealed class CollectionAddonViewModel : ViewModelBase
{
    private Bitmap? _thumbnailBitmap;
    private bool _isSubscribed;
    private bool _disposed;

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public ulong FileSize { get; set; }
    public string PreviewUrl { get; set; } = string.Empty;

    public Bitmap? ThumbnailBitmap
    {
        get => _thumbnailBitmap;
        set
        {
            if (ReferenceEquals(_thumbnailBitmap, value))
            {
                return;
            }

            _thumbnailBitmap?.Dispose();
            this.RaiseAndSetIfChanged(ref _thumbnailBitmap, value);
        }
    }

    public bool IsSubscribed
    {
        get => _isSubscribed;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSubscribed, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public string SizeText => FormatFileSize(FileSize);

    public string StatusText => IsSubscribed
        ? L.Get("Collection.StatusSubscribed")
        : L.Get("Collection.StatusNotSubscribed");

    public IBrush StatusColor => IsSubscribed
        ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
        : new SolidColorBrush(Color.FromRgb(107, 107, 107));

    public async Task LoadThumbnailAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!string.IsNullOrEmpty(PreviewUrl) &&
                Uri.TryCreate(PreviewUrl, UriKind.Absolute, out var uri))
            {
                var bitmap = await RemoteImageLoader.LoadFromUrlAsync(uri);
                if (_disposed)
                {
                    bitmap?.Dispose();
                    return;
                }

                ThumbnailBitmap = bitmap;
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionAddonViewModel.LoadThumbnailAsync", ex);
        }
    }

    public void Release()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThumbnailBitmap = null;
    }

    public void NotifyLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private string FormatFileSize(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
