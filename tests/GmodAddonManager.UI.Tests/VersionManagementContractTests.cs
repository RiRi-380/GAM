using System.Runtime.CompilerServices;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class VersionManagementContractTests
{
    [Fact]
    public void VersionManagementDelegatesEveryMutationToCoreMembershipApis()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "VersionManagementViewModel.cs"));

        Assert.Contains("CreateAssetVersionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("RestoreAssetVersionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeleteAssetVersionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ClearAssetVersionHistoryAsync(", source, StringComparison.Ordinal);

        Assert.DoesNotContain("VersionHistory.Add(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionHistory.Remove(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionHistory.Clear(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeAddonStates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GamContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsImportBaseline", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameVersions", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVersionDialogDoesNotOfferStateSnapshotsOrRenumbering()
    {
        var managementXaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "VersionManagementDialog.axaml"));

        Assert.DoesNotContain("IncludeAddonStates", managementXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameVersions", managementXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsUseSnapshotMembershipAndNearestEarlierVersionAcrossGaps()
    {
        var asset = new Asset
        {
            Id = "custom",
            Name = "Custom",
            Addons = new List<string> { "live-only" },
            CurrentVersion = 3,
            VersionHistory =
            [
                new AssetVersion
                {
                    Version = 1,
                    AddonIds = new List<string> { "old" }
                },
                new AssetVersion
                {
                    Version = 3,
                    AddonIds = new List<string> { "snapshot" }
                }
            ]
        };

        var viewModel =
            new VersionDetailsViewModel(asset, 3, asset.VersionHistory);
        try
        {
            Assert.True(viewModel.HasPreviousVersion);
            Assert.Equal(1, viewModel.AddedCount);
            Assert.Equal(1, viewModel.RemovedCount);
            Assert.Contains(
                viewModel.DisplayAddons,
                addon =>
                    addon.AddonId == "snapshot" &&
                    addon.Status == AddonDiffStatus.Added);
            Assert.Contains(
                viewModel.DisplayAddons,
                addon =>
                    addon.AddonId == "old" &&
                    addon.Status == AddonDiffStatus.Removed);
            Assert.DoesNotContain(
                viewModel.DisplayAddons,
                addon => addon.AddonId == "live-only");
        }
        finally
        {
            viewModel.Release();
        }
    }

    private static string FindRepositoryFile(
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
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
