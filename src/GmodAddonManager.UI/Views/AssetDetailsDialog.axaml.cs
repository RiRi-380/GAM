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
    public static AssetDetailsDialog? CurrentDialog { get; private set; }
    
    private AssetItemViewModel? assetViewModel;
    private AddonManager? addonManager;
    
    public AssetItemViewModel? Asset => assetViewModel;
    
    public ObservableCollection<AddonStateItem> Addons { get; } = new();
    private ObservableCollection<AddonStateItem> AllAddons { get; } = new();
    
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
    private bool showLocalAddons = true;
    
    private int normalAddonCount = 0;
    private int cacheAddonCount = 0;
    private int localAddonCount = 0;
    private int totalAddonCount => normalAddonCount + cacheAddonCount + localAddonCount;
    
    public string AddonCountText
    {
        get
        {
            return addonFilterIndex switch
            {
                0 => L.Format("AssetDetails.AddonCountFormat", totalAddonCount),  // 全て表示
                1 => L.Format("AssetDetails.AddonCountFormat", normalAddonCount),  // 通常のみ
                2 => L.Format("AssetDetails.AddonCountFormat", cacheAddonCount),   // キャッシュのみ
                3 => L.Format("AssetDetails.AddonCountFormat", localAddonCount),   // ローカルのみ
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
                showLocalAddons = true;
                break;
            case 1: // 通常のみ
                showNormalAddons = true;
                showCacheAddons = false;
                showLocalAddons = false;
                break;
            case 2: // キャッシュのみ
                showNormalAddons = false;
                showCacheAddons = true;
                showLocalAddons = false;
                break;
            case 3: // ローカルのみ
                showNormalAddons = false;
                showCacheAddons = false;
                showLocalAddons = true;
                break;
        }
        FilterAddons();
    }
    
    public AssetDetailsDialog()
    {
        InitializeComponent();
        DataContext = this;
        CurrentDialog = this;
        Closed += OnClosed;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }
    
    public void SetAsset(AssetItemViewModel asset, AddonManager manager)
    {
        assetViewModel = asset;
        addonManager = manager;
        AssetName = asset.Name;
        
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
        localAddonCount = 0;
        
        var addonIds = assetViewModel.GetAddonIds();
        var addonStates = assetViewModel.AddonStates;
        var allAddons = addonManager.GetAllAddons();
        
        foreach (var addonId in addonIds)
        {
            if (allAddons.TryGetValue(addonId, out var addon))
            {
                var item = new AddonStateItem
                {
                    AddonId = addonId,
                    Title = addon.Title,
                    IsGmaFile = addon.IsGmaFile,
                    IsLocal = addon.IsLocal,
                    State = addonStates.ContainsKey(addonId) ? addonStates[addonId] : AddonState.Enabled
                };
                
                // 状態変更時の処理
                item.StateChanged += OnAddonStateChanged;
                
                AllAddons.Add(item);
                
                // カウントを更新
                if (addon.IsLocal)
                {
                    localAddonCount++;
                }
                else if (addon.IsGmaFile)
                {
                    cacheAddonCount++;
                }
                else
                {
                    normalAddonCount++;
                }
            }
        }
        
        OnPropertyChanged(nameof(AddonCountText));
        
        // 初期フィルタリング
        FilterAddons();
    }
    
    private void FilterAddons()
    {
        Addons.Clear();
        
        foreach (var item in AllAddons)
        {
            if (item.IsLocal)
            {
                if (showLocalAddons)
                {
                    Addons.Add(item);
                }
                continue;
            }

            if ((showNormalAddons && !item.IsGmaFile) || (showCacheAddons && item.IsGmaFile))
            {
                Addons.Add(item);
            }
        }
    }
    
    private async void OnAddonStateChanged(object? sender, EventArgs e)
    {
        if (sender is AddonStateItem item && assetViewModel != null && addonManager != null)
        {
            try
            {
                // 状態を保存
                assetViewModel.SetAddonState(item.AddonId, item.State);
                await addonManager.SaveConfigurationAsync();
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AssetDetailsDialog.OnAddonStateChanged", ex);
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
            OnPropertyChanged(nameof(AddonCountText));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        CurrentDialog = null;
    }
    
    public new event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AddonStateItem : INotifyPropertyChanged
{
    private AddonState state;
    
    public string AddonId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsGmaFile { get; set; } = false;
    public bool IsLocal { get; set; } = false;
    
    public AddonState State
    {
        get => state;
        set
        {
            if (state != value)
            {
                state = value;
                OnPropertyChanged();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    
    public event EventHandler? StateChanged;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
