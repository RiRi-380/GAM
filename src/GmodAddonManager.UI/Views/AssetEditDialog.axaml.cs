using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetEditDialog : Window
{
    private readonly Asset? asset;
    private readonly AddonManager? addonManager;
    private readonly bool allowRename = true;
    private readonly string originalName = string.Empty;

    private string? existingImagePath;
    private string? selectedImagePath;
    private AssetImageCrop? selectedCrop;
    private bool removeImageRequested;
    private IDisposable? _assetNameSubscription;

    public AssetEditDialog()
    {
        InitializeComponent();
        InitializeDialog();
    }

    public AssetEditDialog(Asset asset, AddonManager addonManager, bool allowRename = true)
    {
        InitializeComponent();
        this.asset = asset;
        this.addonManager = addonManager;
        this.allowRename = allowRename;
        originalName = asset.Name;

        InitializeDialog();
    }

    private void InitializeDialog()
    {
        if (asset != null)
        {
            if (allowRename)
            {
                AssetNameTextBox.Text = asset.Name;
            }
            else
            {
                AssetNameTextBox.Text = asset.Id switch
                {
                    "subscribe-system-asset" => L.Get("Asset.SubscribeAsset"),
                    _ => asset.Name
                };
                AssetNameTextBox.IsEnabled = false;
            }

            if (addonManager != null)
            {
                existingImagePath = addonManager.ResolveAssetImagePath(asset);
                if (!string.IsNullOrWhiteSpace(existingImagePath) && !File.Exists(existingImagePath))
                {
                    existingImagePath = null;
                }
            }
        }
        else
        {
            AssetNameTextBox.Text = string.Empty;
        }

        _assetNameSubscription?.Dispose();
        _assetNameSubscription = AssetNameTextBox.GetObservable(TextBox.TextProperty)
            .Subscribe(_ =>
            {
                UpdateSaveState();
                UpdateImageStatus();
            });

        UpdateImageStatus();
        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        var name = AssetNameTextBox.Text?.Trim();
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(name);
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

    protected override void OnClosed(EventArgs e)
    {
        _assetNameSubscription?.Dispose();
        _assetNameSubscription = null;
        base.OnClosed(e);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var result = new AssetEditResult
        {
            IsSaved = true,
            Name = allowRename ? (AssetNameTextBox.Text?.Trim() ?? string.Empty) : originalName,
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
    public string Name { get; set; } = string.Empty;
    public bool RemoveImage { get; set; }
    public string? SourceImagePath { get; set; }
    public AssetImageCrop? Crop { get; set; }
}
