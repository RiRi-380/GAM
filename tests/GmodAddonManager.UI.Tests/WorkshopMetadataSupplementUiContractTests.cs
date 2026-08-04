using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class WorkshopMetadataSupplementUiContractTests
{
    [Fact]
    public void GridBatchesAndPersistsWorkshopMetadataWithoutEagerBitmapDecoding()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");
        var methodStart = source.IndexOf(
            "private async Task SupplementWorkshopMetadataAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static bool NeedsMetadataSupplement",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source.Substring(methodStart, methodEnd - methodStart);

        Assert.Contains("GetWorkshopDetailsBatchAsync(", method, StringComparison.Ordinal);
        Assert.Contains("targetIds,", method, StringComparison.Ordinal);
        Assert.Contains("requireTags: false", method, StringComparison.Ordinal);
        Assert.Contains("metadataMerger.Merge(", method, StringComparison.Ordinal);
        Assert.Contains("addonVm.UpdateTitle(metadata.Title)", method, StringComparison.Ordinal);
        Assert.Contains("addonVm.UpdateTagsAndType(", method, StringComparison.Ordinal);
        Assert.Equal(
            1,
            method.Split(
                "SaveConfigurationAsync()",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(".Take(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadThumbnail", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ThumbnailBitmap", method, StringComparison.Ordinal);
    }

    [Fact]
    public void GridTargetsPlaceholderTitleAndMissingPersistedThumbnailMetadata()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");

        Assert.Contains(
            "WorkshopMetadataMergeService.NeedsSupplement(addon.SortSource)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MetadataSupplementUpdate(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("details,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GridAlsoRepairsSubscribedMetadataWithoutAVisiblePayloadCard()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");
        var methodStart = source.IndexOf(
            "private async Task SupplementWorkshopMetadataAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static bool NeedsMetadataSupplement",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source.Substring(methodStart, methodEnd - methodStart);

        Assert.Contains("currentSubscribedAddonIds.Contains(metadata.Id)", method, StringComparison.Ordinal);
        Assert.Contains("config.AddonMetadata.Values", method, StringComparison.Ordinal);
        Assert.Contains("IsWorkshopMetadata(metadata)", method, StringComparison.Ordinal);
        Assert.Contains("WorkshopMetadataMergeService.NeedsSupplement(metadata)", method, StringComparison.Ordinal);
        Assert.Contains("AddonItemViewModel?", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        return File.ReadAllText(FindRepositoryPath(parts));
    }

    private static string FindRepositoryPath(params string[] parts)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(CurrentFilePath)!);
        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file: {Path.Combine(parts)}");
    }

    private static string CurrentFilePath => GetCurrentFilePath();

    private static string GetCurrentFilePath(
        [CallerFilePath] string path = "") => path;
}
