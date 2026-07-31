using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class LegacyUiWiringContractTests
{
    [Fact]
    public void MainWindowKeepsOnlySupportedSettingsAndResetWiring()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");

        Assert.Contains("new SettingsDialog(addonManager)", source, StringComparison.Ordinal);
        Assert.Contains("if (dialog.WasSaved)", source, StringComparison.Ordinal);

        Assert.DoesNotContain("CheckCollectionExistenceAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MigrateAddonsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetAllStatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetCurrentAssetStatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportDisableManifestAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreOriginalAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.RetainMissingAssetReferences", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetItemAlwaysUsesUnifiedMembershipVersionManager()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AssetItemViewModel.cs");

        Assert.Contains("await ShowVersionManagementWindowAsync();", source, StringComparison.Ordinal);

        Assert.DoesNotContain("useLegacyInlineVersionSave", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveAddonIdsForVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNewVersionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveVersionDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GamContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeAddonStates", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        var segments = new[] { segment, segment2, segment3, segment4 };
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate =
                Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
