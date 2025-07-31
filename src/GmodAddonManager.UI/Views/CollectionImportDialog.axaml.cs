using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.UI.ViewModels;
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
            _viewModel.LoadCollectionCommand.Execute(Unit.Default);
        }
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.AssetName))
        {
            var addonIds = _viewModel.GetSelectedAddonIds();
            
            if (_onConfirmWithAddons != null)
            {
                // 通常のアセット作成とコレクションインポート両方に対応
                await _onConfirmWithAddons(_viewModel.AssetName, addonIds);
            }
            else if (_onConfirm != null)
            {
                // 旧方式の互換性のため残す
                _onConfirm(_viewModel.AssetName);
            }
            
            Close();
        }
    }
    
    private async void OnBrowseGamFile(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;
        
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "GAMファイルを選択",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GAM Collection File")
                {
                    Patterns = new[] { "*.gam" }
                },
                new FilePickerFileType("All Files")
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
}