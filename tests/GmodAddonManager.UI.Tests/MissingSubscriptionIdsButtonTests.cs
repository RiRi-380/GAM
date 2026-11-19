using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Controls;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class MissingSubscriptionIdsButtonTests
{
    [AvaloniaFact]
    public async Task BothImportPreviewsCopyOnlyMissingIdsOnePerLine()
    {
        string[] references = ["101", "202", "303"];
        string[] missing = ["202", "303"];
        var asset = new GamAssetDocument("Asset", GamAssetDocumentState.Disabled,
            GamAssetDocumentMembership.Fixed(references));
        var single = new GamAssetImportPreview(asset, asset.Name, true, references, missing);
        var bundle = new GamAssetBundleDocument([], []);
        var bundlePreview = new GamAssetFileImportPreview(
            GamAssetFileReadResult.FromBundle(bundle), null, true, references, missing);

        foreach (var dialog in new Window[] { new GamAssetImportPreviewDialog(single), new GamBundleImportPreviewDialog(bundlePreview) })
        {
            try
            {
                dialog.Show();
                var section = dialog.FindControl<Expander>("MissingIdsSection");
                if (section != null)
                {
                    section.IsExpanded = true;
                    dialog.UpdateLayout();
                }
                var button = dialog.FindControl<MissingSubscriptionIdsButton>("CopyIdsButton")!;
                Assert.True(button.IsVisible);
                Assert.Same(dialog, TopLevel.GetTopLevel(button));
                var clipboard = Assert.IsAssignableFrom<IClipboard>(dialog.Clipboard);
                await clipboard.SetTextAsync("fixture");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var attempt = 0; attempt < 20 && Equals(button.Content,
                         L.Get("GamAssetFile.CopyMissingIds")); attempt++)
                {
                    await Task.Delay(10);
                }
                Assert.Equal(L.Get("GamAssetFile.MissingIdsCopied"), button.Content);
                Assert.Equal(string.Join(Environment.NewLine, missing), await clipboard.TryGetTextAsync());
            }
            finally
            {
                dialog.Close();
            }
        }
    }

    [AvaloniaFact]
    public void EmptyOrUnknownSubscriptionsHideTheCopyAction()
    {
        var bundle = new GamAssetBundleDocument([], []);
        foreach (var known in new[] { false, true })
        {
            var preview = new GamAssetFileImportPreview(
                GamAssetFileReadResult.FromBundle(bundle), null, known, [], []);
            var dialog = new GamBundleImportPreviewDialog(preview);
            Assert.False(dialog.FindControl<MissingSubscriptionIdsButton>("CopyIdsButton")!.IsVisible);
        }
    }
}
