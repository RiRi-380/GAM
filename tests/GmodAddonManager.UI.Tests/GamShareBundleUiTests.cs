using System.Xml.Linq;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class GamShareBundleUiTests
{
    [Fact]
    public void BundleContentsPreserveMixedOrderAndShowTheNestedAssetState()
    {
        var asset = new GamAssetBundleAsset("a", "Second", GamAssetDocumentState.Excluded,
            GamAssetDocumentMembership.Fixed(["101"]));
        var child = new GamAssetBundleGroup("nested", "First", GamAssetDocumentState.Disabled,
            Array.Empty<GamAssetBundleEntryReference>());
        var root = new GamAssetBundleGroup("root", "Root", GamAssetDocumentState.Enabled,
            [GamAssetBundleEntryReference.Group(child.LocalId), GamAssetBundleEntryReference.Asset(asset.LocalId)]);

        var item = Assert.Single(GamBundleImportPreviewDialog.BuildContents(new GamAssetBundleDocument([asset], [child, root])));
        Assert.Equal(L.Format("GamBundleImport.GroupEntry", root.Name, L.Get("AddonState.Enabled")), item.Label);
        Assert.Collection(item.Children,
            first => Assert.Equal(L.Format("GamBundleImport.GroupEntry", child.Name, L.Get("AddonState.Disabled")), first.Label),
            second => Assert.Equal(L.Format("GamBundleImport.AssetEntry", asset.Name, L.Get("AddonState.Excluded")), second.Label));
    }

    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    private static readonly XNamespace ViewsNamespace =
        "using:GmodAddonManager.UI.Views";

    private static readonly XNamespace ControlsNamespace =
        "using:GmodAddonManager.UI.Controls";

    [Fact]
    public void MainWindowSwitchesTheCenterPaneToResponsiveShareWorkspace()
    {
        var main = LoadXaml("MainWindow.axaml");
        var share = LoadXaml("GamShareWorkspaceView.axaml");
        var mainSource = main.ToString();
        var shareSource = share.ToString();

        Assert.Contains("GamShareWorkspaceView", mainSource, StringComparison.Ordinal);
        Assert.Contains("AssetListViewModel.IsShareMode", mainSource, StringComparison.Ordinal);
        Assert.Contains("AssetListViewModel.IsAddonGridVisible", mainSource, StringComparison.Ordinal);
        Assert.Contains(
            "Name=\"AddonDetailsPanel\" IsVisible=\"{Binding AssetListViewModel.IsAddonGridVisible}\"",
            NormalizeWhitespace(mainSource),
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding AllOffCommand}\" IsEnabled=\"{Binding AssetListViewModel.IsAssetMutationEnabled}\"",
            NormalizeWhitespace(mainSource),
            StringComparison.Ordinal);
        Assert.Contains("AssetListViewModel.IsAssetMutationEnabled", mainSource, StringComparison.Ordinal);
        Assert.NotEmpty(share.Descendants(AvaloniaNamespace + "ScrollViewer"));
        Assert.Contains("ShareSelectionSummary", shareSource, StringComparison.Ordinal);
        Assert.Contains("IncludeImagesInShare", shareSource, StringComparison.Ordinal);
        Assert.Contains("IncludeMemosInShare", shareSource, StringComparison.Ordinal);
        Assert.Contains("CanExportShareSelection", shareSource, StringComparison.Ordinal);

        var addonGrid = Assert.Single(main.Descendants(ViewsNamespace + "AddonGridView"));
        var addonGridHost = Assert.IsType<XElement>(addonGrid.Parent);
        Assert.Equal(
            "{Binding AssetListViewModel.IsAddonGridVisible}",
            (string?)addonGridHost.Attribute("IsVisible"));
        Assert.Equal(
            "{Binding AddonGridViewModel}",
            (string?)addonGrid.Attribute("DataContext"));
        Assert.Null(addonGrid.Attribute("IsVisible"));

        var shareWorkspace = Assert.Single(
            main.Descendants(ViewsNamespace + "GamShareWorkspaceView"));
        var shareHost = Assert.IsType<XElement>(shareWorkspace.Parent);
        Assert.Equal(
            "{Binding AssetListViewModel.IsShareMode}",
            (string?)shareHost.Attribute("IsVisible"));
        Assert.Equal("#181818", (string?)shareHost.Attribute("Background"));
        Assert.Equal(
            "{Binding AssetListViewModel}",
            (string?)shareWorkspace.Attribute("DataContext"));
        Assert.Null(shareWorkspace.Attribute("IsVisible"));
    }

    [Fact]
    public void ChildViewDataContextsDoNotOwnParentVisibilityBindings()
    {
        var details = XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Controls", "AddonDetailsControl.axaml"));
        var dashboard = Assert.Single(
            details.Descendants(ControlsNamespace + "DashboardControl"));
        var dashboardHost = Assert.IsType<XElement>(dashboard.Parent);

        Assert.Equal(
            "{Binding SelectedAddon, Converter={x:Static ObjectConverters.IsNull}}",
            (string?)dashboardHost.Attribute("IsVisible"));
        Assert.Equal(
            "{Binding DashboardViewModel}",
            (string?)dashboard.Attribute("DataContext"));
        Assert.Null(dashboard.Attribute("IsVisible"));
    }

    [Fact]
    public void CardShareActionPrecedesDeleteAndSelectionDelegatesToCoreBundleExport()
    {
        var card = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml");
        var listCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");
        var shareIndex = card.IndexOf("OnToggleShareSelection", StringComparison.Ordinal);
        var deleteIndex = card.IndexOf("Command=\"{Binding DeleteCommand}\"", StringComparison.Ordinal);

        Assert.True(shareIndex >= 0);
        Assert.True(deleteIndex > shareIndex);
        Assert.Contains("ExportGamSelectionAsync(", listCode, StringComparison.Ordinal);
        Assert.Contains("SaveFilePickerAsync", listCode, StringComparison.Ordinal);
        Assert.Contains("IncludeImagesInShare", listCode, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionDisplay", card, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionManageCommand", card, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareModeLocksTheDetailsEditingEntryPoint()
    {
        var card = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "AssetListView.axaml");
        var entryCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListEntryViewModel.cs");

        Assert.Contains(
            "Command=\"{Binding ShowDetailsCommand}\" IsEnabled=\"{Binding CanShowDetails}\"",
            NormalizeWhitespace(card),
            StringComparison.Ordinal);
        Assert.Contains("public bool CanShowDetails => !isShareMode;", entryCode, StringComparison.Ordinal);
        Assert.Contains(
            "RaisePropertyChanged(nameof(CanShowDetails))",
            entryCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImportUsesSingleAssetPreviewForDocumentsAndBundlePreviewForBundles()
    {
        var listCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");
        var bundlePreview = LoadXaml("GamBundleImportPreviewDialog.axaml").ToString();
        var bundleCode = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "GamBundleImportPreviewDialog.axaml.cs");

        Assert.Contains("PreviewGamFileImportAsync(", listCode, StringComparison.Ordinal);
        Assert.Contains("preview.IsBundle", listCode, StringComparison.Ordinal);
        Assert.Contains("new GamAssetImportPreviewDialog", listCode, StringComparison.Ordinal);
        Assert.Contains("new GamBundleImportPreviewDialog", listCode, StringComparison.Ordinal);
        Assert.Contains("ImportGamFileAsync(", listCode, StringComparison.Ordinal);
        Assert.Contains("AssetCountTextBlock", bundlePreview, StringComparison.Ordinal);
        Assert.Contains("GroupCountTextBlock", bundlePreview, StringComparison.Ordinal);
        Assert.Contains("ImageCountTextBlock", bundlePreview, StringComparison.Ordinal);
        Assert.Contains("ReferenceCountTextBlock", bundlePreview, StringComparison.Ordinal);
        Assert.Contains("MissingCountTextBlock", bundlePreview, StringComparison.Ordinal);
        Assert.Contains("SubscriptionStatusKnown", bundleCode, StringComparison.Ordinal);
        Assert.Contains("GamAssetFile.NoSteamChanges", bundlePreview, StringComparison.Ordinal);
    }

    private static XDocument LoadXaml(string fileName)
    {
        return XDocument.Parse(ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", fileName));
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(TestRepository.FindRoot(), Path.Combine(parts)));
}
