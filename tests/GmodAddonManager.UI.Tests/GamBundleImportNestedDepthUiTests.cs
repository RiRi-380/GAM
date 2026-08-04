using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class GamBundleImportNestedDepthUiTests
{
    [Fact]
    public void PreviewReportsZeroWhenBundleContainsNoGroups()
    {
        var preview = CreatePreview(new GamAssetBundleDocument([], []));

        Assert.Equal(0, preview.RequiredNestedGroupDepth);
    }

    [Fact]
    public void PreviewMeasuresRootGroupAsZeroAndNestedGroupsFromOne()
    {
        var root = new GamAssetBundleGroup(
            "root",
            "Root",
            GamAssetDocumentState.Enabled,
            [GamAssetBundleEntryReference.Group("child")]);
        var child = new GamAssetBundleGroup(
            "child",
            "Child",
            GamAssetDocumentState.Enabled,
            [GamAssetBundleEntryReference.Group("grandchild")]);
        var grandchild = new GamAssetBundleGroup(
            "grandchild",
            "Grandchild",
            GamAssetDocumentState.Enabled,
            Array.Empty<GamAssetBundleEntryReference>());
        var bundle = new GamAssetBundleDocument(
            [],
            [root, child, grandchild],
            [GamAssetBundleEntryReference.Group("root")]);

        var preview = CreatePreview(bundle);

        Assert.Equal(2, preview.RequiredNestedGroupDepth);
    }

    [AvaloniaFact]
    public void DialogShowsCurrentAndRequiredDepthOnlyWhenIncreaseIsNeeded()
    {
        var preview = CreatePreview(CreateNestedBundle());
        var dialog = new GamBundleImportPreviewDialog(
            preview,
            currentMaxNestedGroupDepth: 1);
        try
        {
            var warning = Assert.IsType<Border>(
                dialog.FindControl<Border>("NestedDepthIncreaseBorder"));
            var current = Assert.IsType<TextBlock>(
                dialog.FindControl<TextBlock>("CurrentNestedDepthTextBlock"));
            var required = Assert.IsType<TextBlock>(
                dialog.FindControl<TextBlock>("RequiredNestedDepthTextBlock"));
            var confirmation = Assert.IsType<TextBlock>(
                dialog.FindControl<TextBlock>(
                    "NestedDepthIncreaseConfirmationTextBlock"));

            Assert.True(warning.IsVisible);
            Assert.Equal("1", current.Text);
            Assert.Equal("2", required.Text);
            Assert.False(string.IsNullOrWhiteSpace(confirmation.Text));
            Assert.Contains("1", confirmation.Text, StringComparison.Ordinal);
            Assert.Contains("2", confirmation.Text, StringComparison.Ordinal);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void CompatibilityConstructorDoesNotInventARequiredIncrease()
    {
        var preview = CreatePreview(CreateNestedBundle());
        var dialog = new GamBundleImportPreviewDialog(preview);
        try
        {
            var warning = Assert.IsType<Border>(
                dialog.FindControl<Border>("NestedDepthIncreaseBorder"));

            Assert.False(warning.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static GamAssetFileImportPreview CreatePreview(
        GamAssetBundleDocument bundle)
    {
        return new GamAssetFileImportPreview(
            GamAssetFileReadResult.FromBundle(bundle),
            singleAssetPreview: null,
            subscriptionStatusKnown: true,
            referencedAddonIds: [],
            missingSubscriptionAddonIds: []);
    }

    private static GamAssetBundleDocument CreateNestedBundle()
    {
        var root = new GamAssetBundleGroup(
            "root",
            "Root",
            GamAssetDocumentState.Enabled,
            [GamAssetBundleEntryReference.Group("child")]);
        var child = new GamAssetBundleGroup(
            "child",
            "Child",
            GamAssetDocumentState.Enabled,
            [GamAssetBundleEntryReference.Group("grandchild")]);
        var grandchild = new GamAssetBundleGroup(
            "grandchild",
            "Grandchild",
            GamAssetDocumentState.Enabled,
            Array.Empty<GamAssetBundleEntryReference>());
        return new GamAssetBundleDocument(
            [],
            [root, child, grandchild],
            [GamAssetBundleEntryReference.Group("root")]);
    }
}
