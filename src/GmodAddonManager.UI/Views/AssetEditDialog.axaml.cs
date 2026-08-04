using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetEditDialog : Window
{
    private string? existingImagePath;
    private string? selectedImagePath;
    private AssetImageCrop? selectedCrop;
    private bool removeImageRequested;

    public AssetEditDialog()
    {
        InitializeComponent();
        UpdateImageStatus();
    }

    public AssetEditDialog(string? imagePath)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            existingImagePath = imagePath;
        }

        UpdateImageStatus();
    }

    private void UpdateImageStatus()
    {
        if (removeImageRequested)
        {
            SelectedImageText.Text = L.Get("AssetEdit.ImageRemoved");
            SelectedImageText.Classes.Add("removed");
        }
        else
        {
            SelectedImageText.Classes.Remove("removed");
            var displayPath = selectedImagePath ?? existingImagePath;
            if (!string.IsNullOrWhiteSpace(displayPath))
            {
                SelectedImageText.Text = Path.GetFileName(displayPath);
            }
            else
            {
                SelectedImageText.Text = L.Get("AssetEdit.NoImage");
            }
        }

        var hasImage = !string.IsNullOrWhiteSpace(selectedImagePath) || !string.IsNullOrWhiteSpace(existingImagePath);
        RemoveImageButton.IsEnabled = hasImage || removeImageRequested;
    }

    private async Task<AssetImageCrop?> OpenImageEditorAsync(string path)
    {
        var dialog = new AssetImageEditDialog(path);
        var result = await dialog.ShowDialog<ImageEditResult?>(this);
        if (result != null && result.IsSaved && result.Crop != null)
        {
            return result.Crop;
        }

        return null;
    }

    private async void OnChangeImage(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.Get("AssetEdit.ChangeImage"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.ico" }
                    },
                    new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("BMP") { Patterns = new[] { "*.bmp" } },
                    new FilePickerFileType("GIF") { Patterns = new[] { "*.gif" } },
                    new FilePickerFileType("ICO") { Patterns = new[] { "*.ico" } },
                    new FilePickerFileType("All") { Patterns = new[] { "*" } }
                }
            });

            if (files.Count == 0)
            {
                return;
            }

            var file = files[0];
            var crop = await OpenImageEditorAsync(file.Path.LocalPath);
            if (crop == null)
            {
                return;
            }

            selectedImagePath = file.Path.LocalPath;
            selectedCrop = crop;
            removeImageRequested = false;
            UpdateImageStatus();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetEditDialog.OnChangeImage", ex);
        }
    }

    private void OnRemoveImage(object? sender, RoutedEventArgs e)
    {
        removeImageRequested = true;
        selectedImagePath = null;
        selectedCrop = null;
        UpdateImageStatus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var result = new AssetEditResult
        {
            IsSaved = true,
            RemoveImage = removeImageRequested,
            SourceImagePath = selectedImagePath,
            Crop = removeImageRequested ? null : selectedCrop
        };

        Close(result);
    }
}

public class AssetEditResult
{
    public bool IsSaved { get; set; }
    public bool RemoveImage { get; set; }
    public string? SourceImagePath { get; set; }
    public AssetImageCrop? Crop { get; set; }
}
