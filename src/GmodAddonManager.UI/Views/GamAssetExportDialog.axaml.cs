using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public sealed class GamAssetExportRequest
{
    public GamAssetExportRequest(
        string path,
        bool includeImage,
        bool includeMemo = false)
    {
        Path = path;
        IncludeImage = includeImage;
        IncludeMemo = includeMemo;
    }

    public string Path { get; }

    public bool IncludeImage { get; }

    public bool IncludeMemo { get; }
}

public partial class GamAssetExportDialog : Window
{
    private readonly string assetName;

    public GamAssetExportDialog()
        : this(
            L.Get("GamAssetFile.DefaultAssetName"),
            hasImage: false,
            hasMemo: false)
    {
    }

    public GamAssetExportDialog(
        string assetName,
        bool hasImage,
        bool hasMemo = false)
    {
        this.assetName = string.IsNullOrWhiteSpace(assetName)
            ? L.Get("GamAssetFile.DefaultAssetName")
            : assetName.Trim();
        InitializeComponent();

        AssetNameTextBlock.Text = this.assetName;
        IncludeImageCheckBox.IsChecked = false;
        IncludeImageCheckBox.IsEnabled = hasImage;
        ImageDescriptionTextBlock.Text = hasImage
            ? L.Get("GamAssetFile.ImageCompressionDescription")
            : L.Get("GamAssetFile.NoAssetImage");
        IncludeMemoCheckBox.IsChecked = false;
        IncludeMemoCheckBox.IsEnabled = hasMemo;
        MemoDescriptionTextBlock.Text = hasMemo
            ? L.Get("GamAssetFile.MemoDescription")
            : L.Get("GamAssetFile.NoAssetMemo");
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        try
        {
            ExportDialogErrorTextBlock.IsVisible = false;
            ExportDialogErrorTextBlock.Text = string.Empty;
            var topLevel = GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = L.Get("GamAssetFile.SavePickerTitle"),
                    DefaultExtension = "gam",
                    SuggestedFileName = BuildSuggestedFileName(assetName),
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new(L.Get("GamAssetFile.FileType"))
                        {
                            Patterns = new[] { "*.gam" }
                        }
                    }
                });
            if (file == null)
            {
                return;
            }

            var path = file.Path.LocalPath;
            if (!string.Equals(
                    System.IO.Path.GetExtension(path),
                    ".gam",
                    StringComparison.OrdinalIgnoreCase))
            {
                path += ".gam";
            }

            Close(new GamAssetExportRequest(
                path,
                IncludeImageCheckBox.IsEnabled &&
                IncludeImageCheckBox.IsChecked == true,
                IncludeMemoCheckBox.IsEnabled &&
                IncludeMemoCheckBox.IsChecked == true));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("GamAssetExportDialog.OnExport", ex);
            ExportDialogErrorTextBlock.Text =
                L.Get("GamAssetFile.SavePickerFailed");
            ExportDialogErrorTextBlock.IsVisible = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private static string BuildSuggestedFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (Array.IndexOf(invalid, chars[index]) >= 0)
            {
                chars[index] = '_';
            }
        }

        var safe = new string(chars).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "asset";
        }

        return safe.EndsWith(".gam", StringComparison.OrdinalIgnoreCase)
            ? safe
            : safe + ".gam";
    }
}
