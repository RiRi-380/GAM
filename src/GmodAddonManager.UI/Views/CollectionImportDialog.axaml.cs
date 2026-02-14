using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Services;
using System.Reactive;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class CollectionImportDialog : Window
{
    private readonly CollectionImportViewModel _viewModel;
    private readonly Action<string>? _onConfirm;
    private readonly Func<string, List<string>, Task>? _onConfirmWithAddons;
    
    public Func<string, List<string>, Task>? OnConfirmWithAddons => _onConfirmWithAddons;
    
    public CollectionImportDialog()
    {
        InitializeComponent();
        _viewModel = new CollectionImportViewModel();
        DataContext = _viewModel;
        if (!_viewModel.ShowSubscribeActions)
        {
            ImportTabControl.SelectedIndex = 1;
        }
    }
    
    public CollectionImportDialog(Action<string> onConfirm) : this()
    {
        _onConfirm = onConfirm;
    }
    
    public CollectionImportDialog(Func<string, List<string>, Task> onConfirmWithAddons) : this()
    {
        _onConfirmWithAddons = onConfirmWithAddons;
    }
    
    private void OnCollectionUrlKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.LoadCollectionCommand.Execute(Unit.Default).Subscribe(
                _ => { },
                ex => SafeFileLogger.TryLogException("CollectionImportDialog.OnCollectionUrlKeyUp", ex));
        }
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.AssetName))
            {
                var addonIds = _viewModel.SelectedAddonIds;
                
                if (_onConfirmWithAddons != null)
                {
                    await _onConfirmWithAddons(_viewModel.AssetName, addonIds);
                }
                else if (_onConfirm != null)
                {
                    // 譌ｧ譁ｹ蠑上・莠呈鋤諤ｧ縺ｮ縺溘ａ谿九☆
                    _onConfirm(_viewModel.AssetName);
                }
                
                Close();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionImportDialog.OnCreate", ex);
        }
    }
    
    private async void OnBrowseGamFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;
            
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.Get("CollectionImport.GamFilePickerTitle"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.Get("CollectionImport.GamFileType"))
                    {
                        Patterns = new[] { "*.gam" }
                    },
                    new FilePickerFileType(L.Get("CollectionImport.AllFilesType"))
                    {
                        Patterns = new[] { "*" }
                    }
                }
            });
            
            if (files.Count > 0)
            {
                var file = files[0];
                await _viewModel.LoadGamFileAsync(file.Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("CollectionImportDialog.OnBrowseGamFile", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Release();
        base.OnClosed(e);
    }
}
