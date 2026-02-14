using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.UI.Views;

public partial class JunctionAssetSelectionDialog : Window
{
    private AssetItemViewModel? selectedAsset;
    private readonly List<string> sourceAssetIds;

    public AssetSelectionResult? Result { get; private set; }

    public JunctionAssetSelectionDialog()
    {
        InitializeComponent();
        sourceAssetIds = new List<string>();
    }

    public JunctionAssetSelectionDialog(IEnumerable<AssetItemViewModel> assets, List<string> sourceAssetIds) : this()
    {
        AssetListBox.ItemsSource = assets;
        this.sourceAssetIds = sourceAssetIds;
        
        // 元のアセットがある場合は「元の場所に戻す」ボタンを表示
        if (sourceAssetIds.Count > 0)
        {
            RestoreToOriginalButton.IsVisible = true;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        selectedAsset = AssetListBox.SelectedItem as AssetItemViewModel;
        OkButton.IsEnabled = selectedAsset != null;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = new AssetSelectionResult
        {
            RestoreToOriginal = false,
            SelectedAsset = selectedAsset
        };
        Close(Result);
    }

    private void OnRestoreToOriginalClick(object? sender, RoutedEventArgs e)
    {
        Result = new AssetSelectionResult
        {
            RestoreToOriginal = true,
            SelectedAsset = null
        };
        Close(Result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

public class AssetSelectionResult
{
    public bool RestoreToOriginal { get; set; }
    public AssetItemViewModel? SelectedAsset { get; set; }
}
