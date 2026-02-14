using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Services;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class CollectionDetailsDialog : Window
{
    private CollectionDetailsViewModel _viewModel = null!;
    private WorkshopCollectionInfo _collectionInfo = null!;
    private bool _importConfirmed = false;
    
    public CollectionDetailsDialog()
    {
        InitializeComponent();
        InitializeViewModel(new WorkshopCollectionInfo());
    }
    
    public CollectionDetailsDialog(WorkshopCollectionInfo collectionInfo)
    {
        InitializeComponent();
        InitializeViewModel(collectionInfo);
    }

    private void InitializeViewModel(WorkshopCollectionInfo collectionInfo)
    {
        _collectionInfo = collectionInfo;
        _viewModel = new CollectionDetailsViewModel(collectionInfo);
        DataContext = _viewModel;
    }
    
    public bool ImportConfirmed => _importConfirmed;
    public WorkshopCollectionInfo CollectionInfo => _collectionInfo;
    
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
        try
        {
            await _viewModel.LoadMoreAddonsAsync();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionDetailsDialog.OnLoadMoreClick", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Release();
        base.OnClosed(e);
    }
}
