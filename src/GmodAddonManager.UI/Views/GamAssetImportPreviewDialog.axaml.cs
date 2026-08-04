using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public partial class GamAssetImportPreviewDialog : Window
{
    private readonly GamAssetImportPreview preview;
    private readonly Func<string, string?>? assetNameValidator;

    public GamAssetImportPreviewDialog()
        : this(CreateDesignPreview(), assetNameValidator: null)
    {
    }

    public GamAssetImportPreviewDialog(
        GamAssetImportPreview preview,
        Func<string, string?>? assetNameValidator = null)
    {
        this.preview = preview ?? throw new ArgumentNullException(nameof(preview));
        this.assetNameValidator = assetNameValidator;
        InitializeComponent();

        AssetNameTextBox.Text = preview.SuggestedAssetName;
        FormatTextBlock.Text = preview.Document.SourceFormatVersion switch
        {
            1 => L.Get("GamAssetFile.FormatV1"),
            2 => L.Get("GamAssetFile.FormatV2"),
            3 => L.Get("GamAssetFile.FormatV3"),
            _ => $".gam v{preview.Document.SourceFormatVersion}"
        };
        KindTextBlock.Text = BuildKindText(preview.Document);
        StateTextBlock.Text = L.Get($"AddonState.{preview.Document.State}");
        ReferenceCountTextBlock.Text = preview.ReferencedAddonIds.Count.ToString();
        ReferenceCountLabelTextBlock.Text = preview.IsSmart
            ? L.Get("GamAssetFile.SmartSnapshotCountLabel")
            : L.Get("GamAssetFile.ReferenceCountLabel");
        SmartSnapshotPanel.IsVisible = preview.IsSmart;
        ImageTextBlock.Text = preview.HasImage
            ? L.Get("Common.Yes")
            : L.Get("Common.No");

        MissingPanel.IsVisible =
            !preview.IsSmart &&
            (!preview.SubscriptionStatusKnown ||
             preview.MissingSubscriptionAddonIds.Count > 0);
        MissingSummaryTextBlock.Text = !preview.SubscriptionStatusKnown
            ? L.Get("GamAssetFile.SubscriptionStatusUnknown")
            : L.Format(
                "GamAssetFile.MissingCount",
                preview.MissingSubscriptionAddonIds.Count);
        CopyMissingUrlsButton.IsVisible =
            !preview.IsSmart &&
            preview.MissingSubscriptionAddonIds.Count > 0;
        UpdateImportButton();

        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                AssetNameTextBox.Focus();
                AssetNameTextBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnAssetNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateImportButton();
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        UpdateImportButton();
        var name = AssetNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !ImportButton.IsEnabled)
        {
            return;
        }

        Close(name);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void OnCopyMissingUrls(object? sender, RoutedEventArgs e)
    {
        CopyMissingUrlsErrorTextBlock.IsVisible = false;
        CopyMissingUrlsErrorTextBlock.Text = string.Empty;

        try
        {
            if (preview.IsSmart || preview.MissingSubscriptionAddonIds.Count == 0)
            {
                return;
            }

            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                ShowClipboardCopyFailure();
                return;
            }

            var urls = string.Join(
                Environment.NewLine,
                preview.MissingSubscriptionAddonIds.Select(id =>
                    $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"));
            await clipboard.SetTextAsync(urls);
            CopyMissingUrlsButton.Content =
                L.Get("GamAssetFile.MissingUrlsCopied");
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException(
                "GamAssetImportPreviewDialog.OnCopyMissingUrls",
                ex);
            ShowClipboardCopyFailure();
        }
    }

    private void UpdateImportButton()
    {
        var name = AssetNameTextBox.Text?.Trim();
        var hasName = !string.IsNullOrWhiteSpace(name);
        var validationMessage = hasName
            ? assetNameValidator?.Invoke(name!)
            : null;

        AssetNameErrorTextBlock.Text = validationMessage ?? string.Empty;
        AssetNameErrorTextBlock.IsVisible =
            !string.IsNullOrWhiteSpace(validationMessage);
        ImportButton.IsEnabled = hasName &&
                                 string.IsNullOrWhiteSpace(validationMessage);
    }

    private void ShowClipboardCopyFailure()
    {
        CopyMissingUrlsErrorTextBlock.Text =
            L.Get("GamAssetFile.MissingUrlsCopyFailed");
        CopyMissingUrlsErrorTextBlock.IsVisible = true;
    }

    private static string BuildKindText(GamAssetDocument document)
    {
        if (document.Membership.Kind == GamAssetDocumentMembershipKind.Fixed)
        {
            return L.Get("GamAssetFile.FixedAsset");
        }

        var rule = document.Membership.Rule;
        if (rule == null)
        {
            return L.Get("GamAssetFile.SmartAsset");
        }

        var kind = rule.Kind == GamAssetDocumentRuleKind.Type
            ? L.Get("GamAssetFile.TypeRule")
            : L.Get("GamAssetFile.TagRule");
        var valueKey = (rule.Kind == GamAssetDocumentRuleKind.Type
            ? "AddonType."
            : "AddonTag.") + rule.Value;
        var localizedValue = L.Get(valueKey);
        if (string.Equals(localizedValue, valueKey, StringComparison.Ordinal))
        {
            localizedValue = rule.Value;
        }

        return L.Format("GamAssetFile.SmartRuleFormat", kind, localizedValue);
    }

    private static GamAssetImportPreview CreateDesignPreview()
    {
        var document = new GamAssetDocument(
            "Imported Asset",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()));
        return new GamAssetImportPreview(
            document,
            document.Name,
            subscriptionStatusKnown: true,
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
