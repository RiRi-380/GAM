using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Views;

public class SelectableAddon : INotifyPropertyChanged
{
    private bool isSelected;
    
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long Size { get; set; }
    public string FormattedSize => GetFormattedSize();
    
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected != value)
            {
                isSelected = value;
                OnPropertyChanged();
            }
        }
    }
    
    private string GetFormattedSize()
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = Size;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class AddonSelectionDialog : Window
{
    private readonly AddonManager? addonManager;
    private readonly Asset? currentAsset;
    private readonly ObservableCollection<SelectableAddon> allAddons;
    private readonly ObservableCollection<SelectableAddon> filteredAddons;
    private readonly DispatcherTimer searchTimer = new();
    private bool isClosed;

    public AddonSelectionDialog()
    {
        this.allAddons = new ObservableCollection<SelectableAddon>();
        this.filteredAddons = new ObservableCollection<SelectableAddon>();

        InitializeComponent();
        InitializeUi(loadAddons: false);
    }

    public AddonSelectionDialog(AddonManager addonManager, Asset currentAsset)
    {
        this.addonManager = addonManager;
        this.currentAsset = currentAsset;
        this.allAddons = new ObservableCollection<SelectableAddon>();
        this.filteredAddons = new ObservableCollection<SelectableAddon>();

        InitializeComponent();
        InitializeUi(loadAddons: true);
    }

    private void InitializeUi(bool loadAddons)
    {
        searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        searchTimer.Tick += OnSearchTimerTick;

        SearchTextBox.TextChanged += OnSearchTextChanged;

        AddonList.ItemsSource = filteredAddons;

        if (loadAddons)
        {
            // 繝・・繧ｿ縺ｮ隱ｭ縺ｿ霎ｼ縺ｿ
            LoadAddons();

            foreach (var addon in allAddons)
            {
                addon.PropertyChanged += OnAddonPropertyChanged;
            }
        }
    }

    private void LoadAddons()
    {
        if (addonManager == null || currentAsset == null)
        {
            return;
        }
        var allAddonsDict = addonManager.GetAllAddons();
        var availableAddons = allAddonsDict.Values
            .Where(a => !currentAsset.Addons.Contains(a.Id))
            .OrderBy(a => a.Title);
        
        foreach (var addon in availableAddons)
        {
            var selectableAddon = new SelectableAddon
            {
                Id = addon.Id,
                Title = addon.Title,
                Size = addon.Size,
                IsSelected = false
            };
            
            allAddons.Add(selectableAddon);
            filteredAddons.Add(selectableAddon);
        }
        
        UpdateSelectionText();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (isClosed)
        {
            return;
        }

        searchTimer.Stop();
        searchTimer.Start();
    }

    private void OnSearchTimerTick(object? sender, EventArgs e)
    {
        searchTimer.Stop();
        FilterAddons();
    }

    private void FilterAddons()
    {
        var searchText = SearchTextBox.Text?.ToLower() ?? "";
        
        filteredAddons.Clear();
        
            foreach (var addon in allAddons)
        {
            if (string.IsNullOrWhiteSpace(searchText) ||
                addon.Title.ToLower().Contains(searchText) ||
                addon.Id.ToLower().Contains(searchText))
            {
                filteredAddons.Add(addon);
            }
        }
    }

    private void OnAddonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableAddon.IsSelected))
        {
            UpdateSelectionText();
        }
    }

    private void UpdateSelectionText()
    {
        var selectedCount = allAddons.Count(a => a.IsSelected);
        SelectionText.Text = selectedCount > 0 
            ? L.Format("Dialog.AddonsSelected", selectedCount) 
            : L.Get("Dialog.SelectAddonsPrompt");
        
        OkButton.IsEnabled = selectedCount > 0;
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var addon in filteredAddons)
        {
            addon.IsSelected = true;
        }
    }

    private void OnDeselectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var addon in filteredAddons)
        {
            addon.IsSelected = false;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var selectedIds = allAddons
            .Where(a => a.IsSelected)
            .Select(a => a.Id)
            .ToList();
        
        Close(selectedIds);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
    
    protected override void OnClosed(EventArgs e)
    {
        isClosed = true;
        searchTimer.Stop();
        searchTimer.Tick -= OnSearchTimerTick;
        SearchTextBox.TextChanged -= OnSearchTextChanged;

        // Unsubscribe from event handlers to prevent memory leaks
            foreach (var addon in allAddons)
        {
            addon.PropertyChanged -= OnAddonPropertyChanged;
        }
        
        base.OnClosed(e);
    }
}


