using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Views;

public partial class AssetDetailsDialog : Window, INotifyPropertyChanged
{
    private AssetItemViewModel? assetViewModel;
    private AddonManager? addonManager;
    private IReadOnlySet<string> availableAddonIds = new HashSet<string>(StringComparer.Ordinal);
    
    public ObservableCollection<AssetAddonMembershipItem> Addons { get; } = new();
    private ObservableCollection<AssetAddonMembershipItem> AllAddons { get; } = new();
    
    private string assetName = "";
    public string AssetName
    {
        get => assetName;
        set
        {
            assetName = value;
            OnPropertyChanged();
        }
    }
    
    private int addonFilterIndex = 0;
    public int AddonFilterIndex
    {
        get => addonFilterIndex;
        set
        {
            if (addonFilterIndex != value)
            {
                addonFilterIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AddonCountText));
                UpdateFilterFromIndex();
            }
        }
    }
    
    private bool showNormalAddons = true;
    private bool showCacheAddons = true;
    
    private int normalAddonCount = 0;
    private int cacheAddonCount = 0;
    private int totalAddonCount => normalAddonCount + cacheAddonCount;
    
    public string AddonCountText
    {
        get
        {
            return addonFilterIndex switch
            {
                0 => L.Format("AssetDetails.AddonCountFormat", totalAddonCount),  // 全て表示
                1 => L.Format("AssetDetails.AddonCountFormat", normalAddonCount),  // 通常のみ
                2 => L.Format("AssetDetails.AddonCountFormat", cacheAddonCount),   // キャッシュのみ
                _ => L.Format("AssetDetails.AddonCountFormat", totalAddonCount)
            };
        }
    }
    
    private void UpdateFilterFromIndex()
    {
        switch (addonFilterIndex)
        {
            case 0: // 全て表示
                showNormalAddons = true;
                showCacheAddons = true;
                break;
            case 1: // 通常のみ
                showNormalAddons = true;
                showCacheAddons = false;
                break;
            case 2: // キャッシュのみ
                showNormalAddons = false;
                showCacheAddons = true;
                break;
        }
        FilterAddons();
    }
    
    public AssetDetailsDialog()
    {
        InitializeComponent();
        DataContext = this;
        Closed += OnClosed;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }
    
    public void SetAsset(
        AssetItemViewModel asset,
        AddonManager manager,
        IReadOnlySet<string>? availableAddonIds = null)
    {
        assetViewModel = asset;
        addonManager = manager;
        AssetName = asset.Name;

        this.availableAddonIds = availableAddonIds ?? manager.GetAllAddons()
            .Where(entry => entry.Value.IsAvailable && !entry.Value.IsLocal)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        LoadAddons();
        
    }
    
    public void UpdateAddonFilter(int filterIndex)
    {
        AddonFilterIndex = filterIndex;
    }
    
    private void LoadAddons()
    {
        if (assetViewModel == null || addonManager == null) return;
        
        AllAddons.Clear();
        normalAddonCount = 0;
        cacheAddonCount = 0;
        
        var addonIds = assetViewModel.GetAddonIds();
        var allAddons = addonManager.GetAllAddons();

        foreach (var item in BuildMembershipItems(
                     addonIds,
                     allAddons,
                     availableAddonIds,
                     assetViewModel.IsSubscribeAsset || assetViewModel.IsGmodDisabledAsset))
        {
            AllAddons.Add(item);

            if (item.IsGmaFile)
            {
                cacheAddonCount++;
            }
            else
            {
                normalAddonCount++;
            }
        }
        
        OnPropertyChanged(nameof(AddonCountText));
        
        // 初期フィルタリング
        FilterAddons();
    }

    private static List<AssetAddonMembershipItem> BuildMembershipItems(
        IReadOnlyCollection<string> addonIds,
        IReadOnlyDictionary<string, WorkshopAddon> addonMetadata,
        IReadOnlySet<string> availableAddonIds,
        bool includeUnavailableMembership)
    {
        var results = new List<AssetAddonMembershipItem>(addonIds.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var addonId in addonIds)
        {
            if (string.IsNullOrWhiteSpace(addonId) ||
                addonId == "*" ||
                !seenIds.Add(addonId))
            {
                continue;
            }

            addonMetadata.TryGetValue(addonId, out var metadata);
            if (metadata?.IsLocal == true || (!includeUnavailableMembership && metadata == null))
            {
                continue;
            }

            var isUnavailable = includeUnavailableMembership && !availableAddonIds.Contains(addonId);
            results.Add(new AssetAddonMembershipItem
            {
                AddonId = addonId,
                Title = includeUnavailableMembership && string.IsNullOrWhiteSpace(metadata?.Title)
                    ? AddonTitleHelper.BuildPlaceholderTitle(addonId)
                    : metadata?.Title ?? string.Empty,
                IsGmaFile = metadata?.IsGmaFile == true,
                IsMissing =
                    !includeUnavailableMembership &&
                    metadata != null &&
                    !metadata.IsAvailable &&
                    !metadata.IsDownloadPending,
                IsUnavailable = isUnavailable,
                AvailabilityText = isUnavailable
                    ? L.Get("Addon.Unavailable")
                    : string.Empty
            });
        }

        return results;
    }

    private void FilterAddons()
    {
        Addons.Clear();

        foreach (var item in AllAddons)
        {
            if ((showNormalAddons && !item.IsGmaFile) || (showCacheAddons && item.IsGmaFile))
            {
                Addons.Add(item);
            }
        }
    }
    
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
        {
            if (assetViewModel != null)
            {
                AssetName = assetViewModel.Name;
            }
            LoadAddons();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
    }
    
    public new event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AssetAddonMembershipItem
{
    public string AddonId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsGmaFile { get; set; } = false;
    public bool IsMissing { get; set; }
    public bool IsUnavailable { get; set; }
    public string AvailabilityText { get; set; } = string.Empty;
    public double RowOpacity => IsUnavailable ? 0.55 : 1.0;
}
