using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
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
    
    public class AddonStateItem : INotifyPropertyChanged
    {
        private AddonState state;
        
        public string AddonId { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsGmaFile { get; set; } = false;
        
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
    
    private int normalAddonCount = 0;
    private int cacheAddonCount = 0;
    private int totalAddonCount => normalAddonCount + cacheAddonCount;
    
    public string AddonCountText
    {
        get
        {
            return addonFilterIndex switch
            {
                0 => $"Addons: {totalAddonCount}",  // 全て表示
                1 => $"Addons: {normalAddonCount}",  // 通常のみ
                2 => $"Addons: {cacheAddonCount}",   // キャッシュのみ
                _ => $"Addons: {totalAddonCount}"
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
        CurrentDialog = this;
        Closed += (s, e) => CurrentDialog = null;
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
        
        var addonIds = assetViewModel.GetAddonIds();
        var addonStates = assetViewModel.GetAddonStates();
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
                    State = addonStates.ContainsKey(addonId) ? addonStates[addonId] : AddonState.Enabled
                };
                
                // 状態変更時の処理
                item.StateChanged += OnAddonStateChanged;
                
                AllAddons.Add(item);
                
                // カウントを更新
                if (addon.IsGmaFile)
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
                // エラー処理
            }
        }
    }
    
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    public new event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}