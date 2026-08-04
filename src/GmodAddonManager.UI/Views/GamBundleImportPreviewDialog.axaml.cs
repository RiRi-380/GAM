using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using System;

namespace GmodAddonManager.UI.Views;

public partial class GamBundleImportPreviewDialog : Window
{
    public GamBundleImportPreviewDialog()
        : this(CreateDesignPreview(), Configuration.MinimumNestedGroupDepth)
    {
    }

    public GamBundleImportPreviewDialog(GamAssetFileImportPreview preview)
        : this(
            preview,
            Math.Max(
                Configuration.MinimumNestedGroupDepth,
                preview?.RequiredNestedGroupDepth ??
                    Configuration.MinimumNestedGroupDepth))
    {
    }

    public GamBundleImportPreviewDialog(
        GamAssetFileImportPreview preview,
        int currentMaxNestedGroupDepth)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.IsBundle)
        {
            throw new ArgumentException(
                "The bundle preview dialog requires a valid .gam bundle.",
                nameof(preview));
        }
        if (currentMaxNestedGroupDepth < Configuration.MinimumNestedGroupDepth ||
            currentMaxNestedGroupDepth > Configuration.MaximumNestedGroupDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentMaxNestedGroupDepth),
                currentMaxNestedGroupDepth,
                $"The configured nested Group depth must be between " +
                $"{Configuration.MinimumNestedGroupDepth} and " +
                $"{Configuration.MaximumNestedGroupDepth}.");
        }

        InitializeComponent();
        FormatTextBlock.Text = L.Format(
            "GamBundleImport.FormatValue",
            preview.Content.SourceFormatVersion);
        AssetCountTextBlock.Text = preview.AssetCount.ToString();
        GroupCountTextBlock.Text = preview.GroupCount.ToString();
        ImageCountTextBlock.Text = preview.ImageCount.ToString();
        ReferenceCountTextBlock.Text = preview.ReferencedAddonIds.Count.ToString();
        MissingCountTextBlock.Text = preview.SubscriptionStatusKnown
            ? preview.MissingSubscriptionAddonIds.Count.ToString()
            : L.Get("GamAssetFile.SubscriptionStatusUnknown");

        var requiresDepthIncrease =
            preview.RequiredNestedGroupDepth > currentMaxNestedGroupDepth;
        NestedDepthIncreaseBorder.IsVisible = requiresDepthIncrease;
        if (requiresDepthIncrease)
        {
            CurrentNestedDepthTextBlock.Text = currentMaxNestedGroupDepth.ToString();
            RequiredNestedDepthTextBlock.Text =
                preview.RequiredNestedGroupDepth.ToString();
            NestedDepthIncreaseConfirmationTextBlock.Text = L.Format(
                "GamBundleImport.NestedDepthIncreaseConfirmation",
                currentMaxNestedGroupDepth,
                preview.RequiredNestedGroupDepth);
        }
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private static GamAssetFileImportPreview CreateDesignPreview()
    {
        var bundle = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            Array.Empty<GamAssetBundleGroup>());
        return new GamAssetFileImportPreview(
            GamAssetFileReadResult.FromBundle(bundle),
            singleAssetPreview: null,
            subscriptionStatusKnown: true,
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
