using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetSelectionDialog : Window
{
    private AssetItemViewModel? selectedAsset;
    private readonly ObservableCollection<AssetItemViewModel> assetItems = new ObservableCollection<AssetItemViewModel>();
    private readonly Func<string, Task<AssetItemViewModel?>>? createAssetAsync;

    public AssetSelectionDialog()
    {
        InitializeComponent();
        AssetListBox.ItemsSource = assetItems;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
    }

    public AssetSelectionDialog(IEnumerable<AssetItemViewModel> assets, Func<string, Task<AssetItemViewModel?>>? createAssetAsync = null) : this()
    {
        // 既にソート済みのリストを受け取るので、そのまま使用
        foreach (var asset in assets)
        {
            assetItems.Add(asset);
        }

        this.createAssetAsync = createAssetAsync;
        CreateAssetButton.IsVisible = createAssetAsync != null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        selectedAsset = AssetListBox.SelectedItem as AssetItemViewModel;
        OkButton.IsEnabled = selectedAsset != null;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => AssetListBox.Focus(), DispatcherPriority.Input);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (selectedAsset != null)
            {
                Close(selectedAsset);
            }
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Up || e.Key == Key.Down) && selectedAsset == null)
        {
            var initialSelection = GetInitialSelection();
            if (initialSelection != null)
            {
                AssetListBox.SelectedItem = initialSelection;
                AssetListBox.ScrollIntoView(initialSelection);
                AssetListBox.Focus();
            }
            e.Handled = true;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(selectedAsset);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void OnCreateAssetClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (createAssetAsync == null)
            {
                return;
            }

            var dialog = new SimpleAssetCreateDialog();
            var name = await dialog.ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var newAsset = await createAssetAsync(name.Trim());
            if (newAsset == null)
            {
                return;
            }

            InsertAsset(newAsset);
            AssetListBox.SelectedItem = newAsset;
            AssetListBox.ScrollIntoView(newAsset);
            AssetListBox.Focus();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetSelectionDialog.OnCreateAssetClick", ex);
        }
    }

    private AssetItemViewModel? GetInitialSelection()
    {
        return assetItems.FirstOrDefault(a => a.Id == "subscribe-system-asset")
               ?? assetItems.FirstOrDefault();
    }

    private void InsertAsset(AssetItemViewModel asset)
    {
        if (assetItems.Count == 0)
        {
            assetItems.Add(asset);
            return;
        }

        var systemAssets = assetItems.Where(a => a.IsSystem).ToList();
        var normalAssets = assetItems.Where(a => !a.IsSystem).ToList();
        normalAssets.Add(asset);

        var orderedNormalAssets = normalAssets
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        assetItems.Clear();
        foreach (var systemAsset in systemAssets)
        {
            assetItems.Add(systemAsset);
        }

        foreach (var normalAsset in orderedNormalAssets)
        {
            assetItems.Add(normalAsset);
        }
    }
}
