using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace GmodAddonManager.UI.Tests;

public sealed class SmartAssetUiContractTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CreateDialogOffersExactlyFixedTypeAndTagModes()
    {
        var document = LoadXaml("SimpleAssetCreateDialog.axaml");
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "SimpleAssetCreateDialog.axaml.cs");
        var modeSelector = document.Descendants(AvaloniaNamespace + "ComboBox")
            .Single(element =>
                (string?)element.Attribute(XamlNamespace + "Name") ==
                "CreationModeComboBox");

        Assert.Equal("True", (string?)document.Root!.Attribute("CanResize"));
        Assert.Equal("400", (string?)document.Root.Attribute("MinWidth"));
        Assert.Equal("320", (string?)document.Root.Attribute("MinHeight"));
        Assert.Single(document.Descendants(AvaloniaNamespace + "ScrollViewer"));
        Assert.Equal(3, modeSelector.Elements(AvaloniaNamespace + "ComboBoxItem").Count());
        Assert.Contains("SmartAsset.Mode.Fixed", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("SmartAsset.Mode.Type", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("SmartAsset.Mode.Tag", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.SupportedTypes", source, StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.SupportedTags", source, StringComparison.Ordinal);
        Assert.Contains("TryNormalizeRule", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetListAndDetailsExposeSmartIdentityRuleAndAutomationState()
    {
        var assetList = LoadXaml("AssetListView.axaml").ToString();
        var details = LoadXaml("AssetDetailsDialog.axaml").ToString();
        var assetListViewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetListViewModel.cs");
        var assetItemViewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AssetItemViewModel.cs");

        Assert.Contains("IsVisible=\"{Binding IsSmart}\"", assetList, StringComparison.Ordinal);
        Assert.Contains("SmartBadgeText", assetList, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsSmartAsset}\"", details, StringComparison.Ordinal);
        Assert.Contains("SmartRuleText", details, StringComparison.Ordinal);
        Assert.Contains("SmartAutomationStatusText", details, StringComparison.Ordinal);
        Assert.Contains("CreateSmartAssetAsync", assetListViewModel, StringComparison.Ordinal);
        Assert.Contains("!IsSystem && !IsSmart", assetItemViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void GridUsesCoreClassificationAndDoesNotOfferSmartAssetsForManualMembership()
    {
        var source = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "AddonGridViewModel.cs");

        Assert.Contains("AddonClassificationService.SupportedTypes", source, StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.SupportedTags", source, StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.Evaluate", source, StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.NormalizeTags", source, StringComparison.Ordinal);
        Assert.Contains("AddonClassificationService.InferTypeFromTags", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TypeTagMappings", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TagAliases", source, StringComparison.Ordinal);
        Assert.Contains(".Where(asset => !asset.IsSmart)", source, StringComparison.Ordinal);
        Assert.Contains("if (currentAsset.IsSmart) return;", source, StringComparison.Ordinal);
        Assert.Contains("await addonManager.ReconcileSmartAssetsAsync();", source, StringComparison.Ordinal);
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src", "GmodAddonManager.UI", "Views", fileName));

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
