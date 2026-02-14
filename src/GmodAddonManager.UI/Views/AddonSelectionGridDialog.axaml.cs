using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public class SelectableAddonGrid : INotifyPropertyChanged
{
    private bool isSelected;
    private Avalonia.Media.Imaging.Bitmap? thumbnailImage;
    private bool hasThumbnail;
    
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
    
    public Avalonia.Media.Imaging.Bitmap? ThumbnailImage
    {
        get => thumbnailImage;
        set
        {
            thumbnailImage = value;
            OnPropertyChanged();
            HasThumbnail = value != null;
        }
    }
    
    public bool HasThumbnail
    {
        get => hasThumbnail;
        private set
        {
            hasThumbnail = value;
            OnPropertyChanged();
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

public partial class AddonSelectionGridDialog : Window
{
    private readonly AddonManager? addonManager;
    private readonly Asset? currentAsset;
    private readonly ObservableCollection<SelectableAddonGrid> allAddons;
    private readonly ObservableCollection<SelectableAddonGrid> filteredAddons;
    private readonly DispatcherTimer searchTimer = new();
    private bool isClosed;

    public AddonSelectionGridDialog()
    {
        this.allAddons = new ObservableCollection<SelectableAddonGrid>();
        this.filteredAddons = new ObservableCollection<SelectableAddonGrid>();

        InitializeComponent();
        InitializeUi(loadAddons: false);
    }

    public AddonSelectionGridDialog(AddonManager addonManager, Asset currentAsset)
    {
        this.addonManager = addonManager;
        this.currentAsset = currentAsset;
        this.allAddons = new ObservableCollection<SelectableAddonGrid>();
        this.filteredAddons = new ObservableCollection<SelectableAddonGrid>();

        InitializeComponent();
        InitializeUi(loadAddons: true);
    }

    private void InitializeUi(bool loadAddons)
    {
        // 検索タイマーの設定
        searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        searchTimer.Tick += OnSearchTimerTick;

        // 検索ボックスのイベント設定
        SearchTextBox.TextChanged += OnSearchTextChanged;

// アドオングリッドの設定
        AddonGrid.ItemsSource = filteredAddons;

        if (loadAddons)
        {
            // データの読み込み
            LoadAddons();

            // 選択状態の監視
            foreach (var addon in allAddons)
            {
                addon.PropertyChanged += OnAddonPropertyChanged;
            }

// サムネイルの非同期読み込み
            _ = LoadThumbnailsAsync();
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
            var selectableAddon = new SelectableAddonGrid
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

    private async Task LoadThumbnailsAsync()
    {
        foreach (var addon in allAddons)
        {
            if (isClosed)
            {
                return;
            }

            try
            {
// 今は仮実装としてスキップ
                await Task.Delay(10);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AddonSelectionGridDialog.LoadThumbnailsAsync", ex);
            }
        }
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
        if (isClosed)
        {
            return;
        }

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

    private void OnAddonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is SelectableAddonGrid addon)
        {
            var point = e.GetCurrentPoint(this);
            var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            
            if (point.Properties.IsLeftButtonPressed)
            {
                if (isCtrlPressed)
                {
                    // Ctrl+クリックでトグル
                    addon.IsSelected = !addon.IsSelected;
                }
                else
                {
                    // Single-select when Ctrl is not pressed.
                    foreach (var a in filteredAddons)
                    {
                        a.IsSelected = a == addon;
                    }
                }
            }
        }
    }

    private void OnAddonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableAddonGrid.IsSelected))
        {
            UpdateSelectionText();
        }
    }

    private void UpdateSelectionText()
    {
        var selectedCount = allAddons.Count(a => a.IsSelected);
        SelectionText.Text = selectedCount > 0 
            ? L.Format("SelectionStatus.Selected", selectedCount) 
            : L.Get("SelectionStatus.NoneSelected");
        
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

