using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace GmodAddonManager.UI.Views;

public partial class AssetDeleteDialog : Window
{
    public enum DeleteOption
    {
        Cancel,
        DeleteAssetOnly,
        MoveToOther,
        DeleteWithContents,
        DisableAddons
    }
    
    public DeleteOption Result { get; private set; } = DeleteOption.Cancel;
    
    public AssetDeleteDialog() : this(true, true)
    {
    }

    public AssetDeleteDialog(bool showDisableAddons, bool showDeleteWithContents)
    {
        InitializeComponent();
        DisableAddonsButton.IsVisible = showDisableAddons;
        DeleteWithContentsButton.IsVisible = showDeleteWithContents;
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = DeleteOption.Cancel;
        Close();
    }
    
    private void OnMoveToOtherAsset(object? sender, RoutedEventArgs e)
    {
        Result = DeleteOption.MoveToOther;
        Close();
    }
    
    private void OnDeleteWithContents(object? sender, RoutedEventArgs e)
    {
        Result = DeleteOption.DeleteWithContents;
        Close();
    }
    
    private void OnDisableAddons(object? sender, RoutedEventArgs e)
    {
        Result = DeleteOption.DisableAddons;
        Close();
    }
    
    private void OnDeleteAssetOnly(object? sender, RoutedEventArgs e)
    {
        Result = DeleteOption.DeleteAssetOnly;
        Close();
    }
}
