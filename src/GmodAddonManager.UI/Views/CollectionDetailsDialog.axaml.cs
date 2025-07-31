using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class CollectionDetailsDialog : Window
{
    private readonly CollectionDetailsViewModel _viewModel;
    private readonly SteamworksManager.CollectionInfo _collectionInfo;
    private bool _importConfirmed = false;
    
    public CollectionDetailsDialog()
    {
        InitializeComponent();
    }
    
    public CollectionDetailsDialog(SteamworksManager.CollectionInfo collectionInfo) : this()
    {
        _collectionInfo = collectionInfo;
        _viewModel = new CollectionDetailsViewModel(collectionInfo);
        DataContext = _viewModel;
    }
    
    public bool ImportConfirmed => _importConfirmed;
    public SteamworksManager.CollectionInfo CollectionInfo => _collectionInfo;
    
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _viewModel.LoadAddonsAsync();
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void OnImport(object? sender, RoutedEventArgs e)
    {
        _importConfirmed = true;
        Close();
    }
    
    private async void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        await _viewModel.LoadMoreAddonsAsync();
    }
}