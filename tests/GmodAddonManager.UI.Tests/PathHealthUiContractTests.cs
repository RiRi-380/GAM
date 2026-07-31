using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class PathHealthUiContractTests
{
    [Fact]
    public void PathHealthDoesNotOfferSteamWorkshopFolderDeletion()
    {
        var xaml = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "PathHealthDialog.axaml");
        var codeBehind = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Views", "PathHealthDialog.axaml.cs");
        var viewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "PathHealthViewModel.cs");

        Assert.DoesNotContain("OnCleanupEmptyFolders", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CanCleanupEmptyFolders", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PathHealth.EmptyFolderCleanup", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PathHealth.Delete", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("OnCleanupEmptyFolders", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmCleanup", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmDeleteTitle", codeBehind, StringComparison.Ordinal);

        Assert.DoesNotContain("CleanupCandidate", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupEmptyFoldersAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupSummary", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PathHealthLocalizesIssuesAndDisclosesTruncatedCounts()
    {
        var viewModel = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "ViewModels", "PathHealthViewModel.cs");
        var japanese = ReadRepositoryFile(
            "src", "GmodAddonManager.UI", "Resources", "ja-JP.json");

        Assert.Contains("Select(LocalizePathIssue)", viewModel, StringComparison.Ordinal);
        Assert.Contains("PathHealth.MoreItemsLine", viewModel, StringComparison.Ordinal);
        Assert.Contains("PathHealth.MoreItemsInline", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("report.Issues.Take(12)", viewModel, StringComparison.Ordinal);
        Assert.Contains("PathHealth.Issue.WorkshopRootMissing", japanese, StringComparison.Ordinal);
        Assert.Contains("…ほか{0}件", japanese, StringComparison.Ordinal);
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
