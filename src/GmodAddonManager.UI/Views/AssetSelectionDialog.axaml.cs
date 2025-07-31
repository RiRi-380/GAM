using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.UI.Views;

public partial class AssetSelectionDialog : Window
{
    private AssetItemViewModel? selectedAsset;

    public AssetSelectionDialog()
    {
        InitializeComponent();
    }

    public AssetSelectionDialog(IEnumerable<AssetItemViewModel> assets) : this()
    {
        // 既にソート済みのリストを受け取るので、そのまま使用
        AssetListBox.ItemsSource = assets;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        selectedAsset = AssetListBox.SelectedItem as AssetItemViewModel;
        OkButton.IsEnabled = selectedAsset != null;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(selectedAsset);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}