using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Linq;

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
        BundleContentsTree.ItemsSource = BuildContents(preview.Content.Bundle!);
        MissingIdsSection.IsVisible = preview.SubscriptionStatusKnown && preview.MissingSubscriptionAddonIds.Count > 0;
        MissingIdsTextBlock.Text = string.Join(", ", preview.MissingSubscriptionAddonIds);

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

    internal static IReadOnlyList<GamBundlePreviewEntry> BuildContents(GamAssetBundleDocument bundle)
    {
        var assets = bundle.Assets.ToDictionary(asset => asset.LocalId, StringComparer.OrdinalIgnoreCase);
        var groups = bundle.Groups.ToDictionary(group => group.LocalId, StringComparer.OrdinalIgnoreCase);
        return bundle.RootChildren.Select(CreateEntry).ToArray();

        GamBundlePreviewEntry CreateEntry(GamAssetBundleEntryReference entry)
        {
            if (entry.Kind == GamAssetBundleEntryKind.Asset)
            {
                var asset = assets[entry.LocalId];
                return new GamBundlePreviewEntry(
                    L.Format("GamBundleImport.AssetEntry", asset.Name, StateLabel(asset.State)),
                    Array.Empty<GamBundlePreviewEntry>());
            }

            var group = groups[entry.LocalId];
            return new GamBundlePreviewEntry(
                L.Format("GamBundleImport.GroupEntry", group.Name, StateLabel(group.DefaultChildState)),
                group.Children.Select(CreateEntry).ToArray());
        }
    }

    private static string StateLabel(GamAssetDocumentState state) => L.Get(state switch
    {
        GamAssetDocumentState.Enabled => "AddonState.Enabled",
        GamAssetDocumentState.Disabled => "AddonState.Disabled",
        _ => "AddonState.Excluded"
    });

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

public sealed record GamBundlePreviewEntry(string Label, IReadOnlyList<GamBundlePreviewEntry> Children);
