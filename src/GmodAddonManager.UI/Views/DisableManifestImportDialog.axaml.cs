using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Views;

public partial class DisableManifestImportDialog : Window
{
    public DisableManifestImportDialog()
    {
        InitializeComponent();
    }

    public DisableManifestImportDialog(DisableManifestImportViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DisableManifestImportViewModel viewModel)
        {
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select .gamdisable manifest",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GAM Disable Manifest")
                {
                    Patterns = new[] { "*.gamdisable" }
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = new[] { "*" }
                }
            }
        });

        if (files.Count > 0)
        {
            await viewModel.LoadPreviewAsync(files[0].Path.LocalPath);
        }
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DisableManifestImportViewModel viewModel)
        {
            await viewModel.ApplyAsync();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
