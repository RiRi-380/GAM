using System.Reflection;
using System.Runtime.CompilerServices;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonGridRealizedThumbnailLoadingTests
{
    [Fact]
    public void BottomRealizedSnapshotProducesAnInBoundsLoadRange()
    {
        var method = typeof(AddonGridView).GetMethod(
            "GetRealizedRanges",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var ranges = Assert.IsAssignableFrom<
            IReadOnlyList<(int StartIndex, int EndIndex)>>(method!.Invoke(
                null,
                [Enumerable.Range(1048, 12).Concat([-1, 1060]).ToArray(), 1060]));

        var range = Assert.Single(ranges);
        Assert.Equal((1048, 1060), range);
    }

    [Fact]
    public void EmptyOrStaleSnapshotsDoNotQueueAThumbnailRange()
    {
        var method = typeof(AddonGridView).GetMethod(
            "GetRealizedRanges",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var ranges = Assert.IsAssignableFrom<
            IReadOnlyList<(int StartIndex, int EndIndex)>>(method!.Invoke(
                null,
                [new[] { -1, 1060, 2000 }, 1060]));

        Assert.Empty(ranges);
    }

    [Fact]
    public void DisjointRealizedSnapshotLoadsOnlyExactContiguousRanges()
    {
        var method = typeof(AddonGridView).GetMethod(
            "GetRealizedRanges",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var ranges = Assert.IsAssignableFrom<
            IReadOnlyList<(int StartIndex, int EndIndex)>>(method!.Invoke(
                null,
                [new[] { 8, 1, 2, 5, 7, 8 }, 20]));

        Assert.Equal([(1, 3), (5, 6), (7, 9)], ranges);
    }

    [Fact]
    public void RepeaterLifecycleIsTheOnlyAuthorityForThumbnailLoading()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml.cs");

        Assert.Contains("ElementPrepared += OnRepeaterElementPrepared", source, StringComparison.Ordinal);
        Assert.Contains("ElementClearing += OnRepeaterElementClearing", source, StringComparison.Ordinal);
        Assert.Contains("ElementIndexChanged += OnRepeaterElementIndexChanged", source, StringComparison.Ordinal);
        Assert.Contains("_realizedAddonIndices[e.Element] = e.Index", source, StringComparison.Ordinal);
        Assert.Contains("addonVm.ReleaseThumbnailBitmap();", source, StringComparison.Ordinal);
        Assert.Contains("allowRemote: true", source, StringComparison.Ordinal);
        Assert.Contains(
            "await addon.LoadThumbnailAsync(allowRemote, token);",
            ReadRepositoryFile(
                "src",
                "GmodAddonManager.UI",
                "ViewModels",
                "AddonGridViewModel.cs"),
            StringComparison.Ordinal);

        var itemSource = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonItemViewModel.cs");
        Assert.Contains("CancelThumbnailLoad();", itemSource, StringComparison.Ordinal);
        Assert.Contains(
            "RemoteImageLoader.LoadFromUrlAsync(uri, loadToken)",
            itemSource,
            StringComparison.Ordinal);

        Assert.DoesNotContain("EffectiveViewportChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MinItemHeight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MinItemWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollViewer.Offset", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allowRemote: false", source, StringComparison.Ordinal);
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
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
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
