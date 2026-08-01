using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class LegacyProductionWiringContractTests
{
    [Theory]
    [InlineData("ViewModels", "AssetItemViewModel.cs")]
    [InlineData("ViewModels", "AddonGridViewModel.cs")]
    [InlineData("ViewModels", "DashboardViewModel.cs")]
    [InlineData("Converters", "AssetIdToColorConverter.cs")]
    [InlineData("ViewModels", "AssetListViewModel.cs")]
    [InlineData("Views", "AssetEditDialog.axaml.cs")]
    [InlineData("Views", "VersionDetailsDialog.axaml.cs")]
    public void ActiveUiSourcesDoNotSpecialCaseLegacyJunctionAsset(
        string directory,
        string fileName)
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            directory,
            fileName);

        Assert.DoesNotContain("junction-system-asset", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableMode.Hard", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetAndGridDoNotExposeLegacyImportOrExclusiveDeveloperWiring()
    {
        var assetItem = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AssetItemViewModel.cs");
        var addonGrid = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");

        Assert.DoesNotContain(
            "DisableManifestImportServiceConstants",
            assetItem,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DeveloperModeCommands", addonGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("DeveloperModePhrase", addonGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("showExclusiveApply", assetItem, StringComparison.Ordinal);
        Assert.DoesNotContain("showExclusiveApply", addonGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("HasImportBaseline", assetItem, StringComparison.Ordinal);
        Assert.DoesNotContain("Version.ImportBaseline", assetItem, StringComparison.Ordinal);
        Assert.Contains("ApplyAssetEditAsync(", assetItem, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameAsset(", assetItem, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAssetImageFromFile(", assetItem, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAssetImage(", assetItem, StringComparison.Ordinal);
        Assert.Contains("AddAddonsToAssetBatch(", addonGrid, StringComparison.Ordinal);
        Assert.Contains("AddAddonsToNewAssetBatch(", addonGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedAsset.AddAddon(", addonGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning.NoAvailableAssets", addonGrid, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDoesNotSurfaceJunctionSpecificInitializationFailure()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            string.Empty,
            "App.axaml.cs");

        Assert.DoesNotContain("Error.JunctionCreationFailed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.DisableMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.StrictLinkMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "settings.EnableLocalAddonsExperimental",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnableLocalAddonsExperimental",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnableLocalAddonManagement",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyDeveloperCommandSourceIsNotShipped()
    {
        var path = FindRepositoryPath(
            "src",
            "GmodAddonManager.UI",
            "Services",
            "DeveloperModeCommands.cs");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AppSettingsDoesNotPersistRetiredProductSwitches()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Models",
            "AppSettings.cs");

        Assert.DoesNotContain("DisableMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StrictLinkMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableLocalAddonsExperimental", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableDisableManifestImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeveloperModePhrase", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetDetailsKeepsMembershipReadOnly()
    {
        var assetItem = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AssetItemViewModel.cs");
        var dialogCode = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml.cs");
        var dialogXaml = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml");
        var combined = assetItem + dialogCode + dialogXaml;

        Assert.DoesNotContain("AddonStateControl", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddonStates", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAddonState(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAddonState(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("StateChanged", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FilterLocal", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("showLocalAddons", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("localAddonCount", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionBadgeUsesMembershipDiffContract()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AssetItemViewModel.cs");

        Assert.Contains(
            "AssetVersionHasMembershipChanges(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Version.ChangedFormat", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AddonStateControl.axaml")]
    [InlineData("AddonStateControl.axaml.cs")]
    [InlineData("AssetCleanupDialog.axaml")]
    [InlineData("AssetCleanupDialog.axaml.cs")]
    [InlineData("InitialLoadingWindow.axaml")]
    [InlineData("InitialLoadingWindow.axaml.cs")]
    [InlineData("NewAddonCheckWindow.axaml")]
    [InlineData("NewAddonCheckWindow.axaml.cs")]
    [InlineData("AddAddonMethodDialog.axaml")]
    [InlineData("AddAddonMethodDialog.axaml.cs")]
    [InlineData("GamExportDialog.axaml")]
    [InlineData("GamExportDialog.axaml.cs")]
    [InlineData("AssetDeleteDialog.axaml")]
    [InlineData("AssetDeleteDialog.axaml.cs")]
    [InlineData("AddonSelectionDialog.axaml")]
    [InlineData("AddonSelectionDialog.axaml.cs")]
    [InlineData("AddonSelectionGridDialog.axaml")]
    [InlineData("AddonSelectionGridDialog.axaml.cs")]
    [InlineData("SaveVersionDialog.axaml")]
    [InlineData("SaveVersionDialog.axaml.cs")]
    public void ObsoleteUiSourceIsNotShipped(string fileName)
    {
        var path = FindRepositoryPath(
            "src",
            "GmodAddonManager.UI",
            "Views",
            fileName);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WindowActivationReconcilesTheReadOnlyGmodOriginAssetAndRefreshesCards()
    {
        var windowSource = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "MainWindow.axaml.cs");
        var viewModelSource = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");

        Assert.Contains("Activated += OnWindowActivated;", windowSource, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _activationRefreshGeneration)", windowSource, StringComparison.Ordinal);
        Assert.Contains("await viewModel.RefreshActualStateFromRuntimeAsync();", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateAddonStates", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPendingChanges", windowSource, StringComparison.Ordinal);

        Assert.Contains("public async Task RefreshActualStateFromRuntimeAsync()", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("await addonManager.RefreshGmodDisabledAddonsFromRuntimeAsync();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AssetListViewModel.RefreshGmodDisabledAsset();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddonGridViewModel.ApplyFilter();", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedMissingReferencesHaveVisibleBadges()
    {
        var addonItem = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonItemViewModel.cs");
        var addonGrid = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml");
        var assetDetailsCode = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml.cs");
        var assetDetailsXaml = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml");

        Assert.Contains("public bool IsMissing", addonItem, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsMissing}\"", addonGrid, StringComparison.Ordinal);
        Assert.Contains("Addon.Missing", addonGrid, StringComparison.Ordinal);
        Assert.Contains("IsMissing =", assetDetailsCode, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsMissing}\"", assetDetailsXaml, StringComparison.Ordinal);
        Assert.Contains("Addon.Missing", assetDetailsXaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja-JP.json")]
    [InlineData("en-US.json")]
    public void RetiredFeatureResourcesAreNotShipped(string fileName)
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Resources",
            fileName);

        foreach (var retiredPrefix in new[]
                 {
                     "\"Collection.",
                     "\"CollectionImport.",
                     "\"CollectionUpload.",
                     "\"DisableManifest.",
                     "\"AssetDelete.",
                     "\"GamExport.",
                     "\"SaveVersion.",
                     "\"ShareCollection.",
                     "\"JunctionRestore.",
                     "\"Settings.LocalAddons",
                     "\"Settings.HardDisable",
                     "\"Settings.DisableMode"
                 })
        {
            Assert.DoesNotContain(retiredPrefix, source, StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var path = FindRepositoryPath(
            segment,
            segment2,
            segment3,
            segment4,
            sourceFilePath);
        return File.ReadAllText(path);
    }

    private static string FindRepositoryPath(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment, segment2, segment3, segment4 }
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository path: {Path.Combine(segments)}");
    }
}
