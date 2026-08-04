using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class GamAssetUiContractTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AssetHeaderProvidesCompactGamImportAndRunsPreviewBeforeMutation()
    {
        var assetList = LoadXaml("AssetListView.axaml");
        var importButton = assetList
            .Descendants(AvaloniaNamespace + "Button")
            .Single(element =>
                (string?)element.Attribute("Command") ==
                "{Binding ImportGamAssetCommand}");
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");

        Assert.Equal("30", (string?)importButton.Attribute("Width"));
        Assert.Equal("30", (string?)importButton.Attribute("Height"));
        Assert.Contains("FileIcon", importButton.ToString(), StringComparison.Ordinal);
        Assert.Contains("*.gam", source, StringComparison.Ordinal);

        var previewIndex = source.IndexOf(
            "PreviewGamFileImportAsync(",
            StringComparison.Ordinal);
        var bundleConfirmationIndex = source.IndexOf(
            "bundleDialog.ShowDialog<bool>",
            previewIndex,
            StringComparison.Ordinal);
        var singleConfirmationIndex = source.IndexOf(
            "previewDialog.ShowDialog<string?>",
            previewIndex,
            StringComparison.Ordinal);
        var importIndex = source.IndexOf(
            "ImportGamFileAsync(",
            previewIndex,
            StringComparison.Ordinal);
        var reloadIndex = source.IndexOf(
            "LoadAssets();",
            importIndex,
            StringComparison.Ordinal);

        Assert.True(previewIndex >= 0);
        Assert.True(bundleConfirmationIndex > previewIndex);
        Assert.True(singleConfirmationIndex > bundleConfirmationIndex);
        Assert.True(importIndex > singleConfirmationIndex);
        Assert.True(reloadIndex > importIndex);
        Assert.Contains("SelectedAsset = Assets.FirstOrDefault", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetDetailsUseCompactOverviewAndDoNotOwnExport()
    {
        var details = LoadXaml("AssetDetailsDialog.axaml");
        var detailsSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetDetailsDialog.axaml.cs");

        Assert.DoesNotContain(
            details.Descendants(AvaloniaNamespace + "Button"),
            element => (string?)element.Attribute("Click") == "OnExportGamAsset");
        Assert.Empty(details.Descendants(AvaloniaNamespace + "ItemsRepeater"));
        Assert.Empty(details.Descendants(AvaloniaNamespace + "ItemsControl"));
        Assert.Contains("{Binding AssetTypeText}", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding MemberCountText}", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding AvailableCountText}", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding MissingCountText}", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding TotalSizeText}", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("UpdateAssetMemoAsync", detailsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GamAssetExportDialog", detailsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleExportOffersImageAndMemoSharingOffByDefault()
    {
        var export = LoadXaml("GamAssetExportDialog.axaml");
        var optionsSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetExportDialog.axaml.cs");

        Assert.Single(
            export.Descendants(AvaloniaNamespace + "CheckBox"),
            element => (string?)element.Attribute(XamlNamespace + "Name") ==
                       "IncludeMemoCheckBox");
        Assert.Contains("IncludeImageCheckBox.IsChecked = false;", optionsSource, StringComparison.Ordinal);
        Assert.Contains("IncludeMemoCheckBox.IsChecked = false;", optionsSource, StringComparison.Ordinal);
        Assert.Contains("public bool IncludeMemo { get; }", optionsSource, StringComparison.Ordinal);
        Assert.Contains("*.gam", optionsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void GamDialogsRemainReachableAtMinimumSizeAndPreviewMissingReferences()
    {
        var export = LoadXaml("GamAssetExportDialog.axaml");
        var preview = LoadXaml("GamAssetImportPreviewDialog.axaml");
        var previewSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetImportPreviewDialog.axaml.cs");
        var nameInput = preview
            .Descendants(AvaloniaNamespace + "TextBox")
            .Single();

        Assert.Equal("True", (string?)export.Root!.Attribute("CanResize"));
        Assert.Equal("True", (string?)preview.Root!.Attribute("CanResize"));
        Assert.NotEmpty(export.Descendants(AvaloniaNamespace + "ScrollViewer"));
        Assert.NotEmpty(preview.Descendants(AvaloniaNamespace + "ScrollViewer"));
        Assert.Equal("200", (string?)nameInput.Attribute("MaxLength"));
        Assert.Contains("MissingSubscriptionAddonIds", previewSource, StringComparison.Ordinal);
        Assert.Contains("CopyMissingUrlsButton.IsVisible", previewSource, StringComparison.Ordinal);
        Assert.Contains("steamcommunity.com/sharedfiles/filedetails/?id=", previewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SmartPreviewTreatsSnapshotAsInformationalAndNeverOffersMissingUrlCopy()
    {
        var preview = LoadXaml("GamAssetImportPreviewDialog.axaml");
        var previewSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetImportPreviewDialog.axaml.cs");

        Assert.Single(
            preview.Descendants(AvaloniaNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "SmartSnapshotPanel");
        Assert.Contains("SmartSnapshotCountLabel", previewSource, StringComparison.Ordinal);
        Assert.Contains("SmartSnapshotPanel.IsVisible = preview.IsSmart", previewSource, StringComparison.Ordinal);
        Assert.Contains("!preview.IsSmart &&", previewSource, StringComparison.Ordinal);
        Assert.Contains("if (preview.IsSmart || preview.MissingSubscriptionAddonIds.Count == 0)", previewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportPreviewValidatesDuplicateNameInlineBeforeClosing()
    {
        var preview = LoadXaml("GamAssetImportPreviewDialog.axaml");
        var previewSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetImportPreviewDialog.axaml.cs");
        var assetListSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");

        Assert.Single(
            preview.Descendants(AvaloniaNamespace + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "AssetNameErrorTextBlock");
        Assert.Contains("Func<string, string?>? assetNameValidator", previewSource, StringComparison.Ordinal);
        Assert.Contains("assetNameValidator?.Invoke(name!)", previewSource, StringComparison.Ordinal);
        Assert.Contains("!ImportButton.IsEnabled", previewSource, StringComparison.Ordinal);
        Assert.Contains("candidateName => addonManager.AssetNameExists(candidateName)", assetListSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingUrlClipboardFailureIsReportedInline()
    {
        var preview = LoadXaml("GamAssetImportPreviewDialog.axaml");
        var previewSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetImportPreviewDialog.axaml.cs");

        Assert.Single(
            preview.Descendants(AvaloniaNamespace + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "CopyMissingUrlsErrorTextBlock");
        Assert.Contains("if (clipboard == null)", previewSource, StringComparison.Ordinal);
        Assert.Contains("ShowClipboardCopyFailure();", previewSource, StringComparison.Ordinal);
        Assert.Contains("MissingUrlsCopyFailed", previewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportDialogExposesFullAssetNameAsTooltip()
    {
        var export = LoadXaml("GamAssetExportDialog.axaml");
        var name = export.Descendants(AvaloniaNamespace + "TextBlock")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "AssetNameTextBlock");

        Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
        Assert.Equal(
            "{Binding #AssetNameTextBlock.Text}",
            (string?)name.Attribute("ToolTip.Tip"));
    }

    [Fact]
    public void GamResourcesAreCompleteAndStayInParity()
    {
        var japanese = LoadResources("ja-JP.json");
        var english = LoadResources("en-US.json");
        var requiredKeys = new[]
        {
            "GamAssetFile.Import",
            "GamAssetFile.ImportTooltip",
            "GamAssetFile.ImportPreviewTitle",
            "GamAssetFile.ImportFailed",
            "GamAssetFile.FormatV3",
            "GamAssetFile.Export",
            "GamAssetFile.ExportTooltip",
            "GamAssetFile.ExportTitle",
            "GamAssetFile.ExportFailed",
            "GamAssetFile.IncludeImage",
            "GamAssetFile.IncludeMemo",
            "GamAssetFile.ImageCompressionDescription",
            "GamAssetFile.MemoDescription",
            "GamAssetFile.MissingDescription",
            "GamAssetFile.MissingUrlsCopyFailed",
            "GamAssetFile.SmartSnapshotCountLabel",
            "GamAssetFile.SmartSnapshotDescription",
            "GamAssetFile.NoSteamChanges"
        };

        foreach (var key in requiredKeys)
        {
            Assert.True(japanese.ContainsKey(key), $"Japanese resource is missing {key}.");
            Assert.True(english.ContainsKey(key), $"English resource is missing {key}.");
            Assert.False(string.IsNullOrWhiteSpace(japanese[key]));
            Assert.False(string.IsNullOrWhiteSpace(english[key]));
        }

        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            japanese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void SingleImportPreviewReportsTheActualDocumentFormatVersion()
    {
        var previewSource = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamAssetImportPreviewDialog.axaml.cs");

        Assert.Contains("preview.Document.SourceFormatVersion switch", previewSource, StringComparison.Ordinal);
        Assert.Contains("2 => L.Get(\"GamAssetFile.FormatV2\")", previewSource, StringComparison.Ordinal);
        Assert.Contains("3 => L.Get(\"GamAssetFile.FormatV3\")", previewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportSuggestedFileNameIsSafeAndAlwaysUsesGamExtension()
    {
        var method = typeof(GamAssetExportDialog).GetMethod(
            "BuildSuggestedFileName",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = Assert.IsType<string>(method!.Invoke(null, ["FPS: Test."]));
        Assert.EndsWith(".gam", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":", result, StringComparison.Ordinal);
        Assert.False(result[..^4].EndsWith('.'));

        var existingExtension = Assert.IsType<string>(
            method.Invoke(null, ["Shared.gam"]));
        Assert.Equal("Shared.gam", existingExtension);
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src", "GmodAddonManager.UI", "Views", fileName));

    private static Dictionary<string, string> LoadResources(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "GmodAddonManager.UI", "Resources", fileName));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ??
               throw new InvalidDataException($"Could not parse {fileName}.");
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(segments).ToArray()));

    private static string FindRepositoryRoot([CallerFilePath] string callerPath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(callerPath)!);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
