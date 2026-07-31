using System.Reflection;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonGridSubscriptionVisibilityTests
{
    [Fact]
    public void ThrottledFilterUpdatesReturnToTheUiScheduler()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs"));

        Assert.Contains(
            ".Throttle(TimeSpan.FromMilliseconds(300))\n            .ObserveOn(RxApp.MainThreadScheduler)",
            source.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetSelectionUsesOnlyTheDebouncedFilterPipeline()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs"));
        var methodStart = source.IndexOf(
            "public void SetCurrentAsset(AssetItemViewModel? asset)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task LoadAddonDetailsAsync",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        Assert.DoesNotContain("ApplyFilter();", source[methodStart..methodEnd]);
    }

    [Fact]
    public void SubscribeDetailsUsesARepeaterForLargeMembershipLists()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml"));

        Assert.Contains("<ItemsRepeater ItemsSource=\"{Binding Addons}\"", source);
        Assert.Contains("<StackLayout Orientation=\"Vertical\"/>", source);
        Assert.DoesNotContain("<ItemsControl ItemsSource=\"{Binding Addons}\"", source);
    }

    [Fact]
    public void AddonHeaderWrapsLongCountsAndSelectionActionsWithinItsWidth()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AddonGridView.axaml"));
        var headerStart = source.IndexOf("<!-- ヘッダー -->", StringComparison.Ordinal);
        var headerEnd = source.IndexOf("<!-- ローディング表示 -->", headerStart, StringComparison.Ordinal);

        Assert.True(headerStart >= 0);
        Assert.True(headerEnd > headerStart);
        var header = source[headerStart..headerEnd];
        Assert.Contains("RowDefinitions=\"Auto,Auto\"", header);
        Assert.Contains("Text=\"{Binding AddonCountDisplay}\"", header);
        Assert.Contains("TextWrapping=\"Wrap\"", header);
        Assert.Contains("<WrapPanel Grid.Row=\"1\"", header);
        Assert.Contains("IsChecked=\"{Binding ShowOnlyAssetAddons}\"", header);
        Assert.Contains("IsChecked=\"{Binding IsSelectionMode}\"", header);
        Assert.True(
            header.Split("IsVisible=\"{Binding IsSelectionMode}\"", StringSplitOptions.None).Length - 1 >= 4);
    }

    [Fact]
    public void SubscribeAssetShowsOnlyCurrentSubscriptionMembership()
    {
        var subscribedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "100"
        };

        Assert.True(InvokeMatchesAssetMembership(
            "subscribe-system-asset",
            ["*"],
            "100",
            subscribedIds));
        Assert.False(InvokeMatchesAssetMembership(
            "subscribe-system-asset",
            ["*"],
            "200",
            subscribedIds));
    }

    [Fact]
    public void CustomAssetMembershipRemainsIndependentOfSubscriptionMembership()
    {
        var subscribedIds = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(InvokeMatchesAssetMembership(
            "custom",
            ["200"],
            "200",
            subscribedIds));
        Assert.False(InvokeMatchesAssetMembership(
            "custom",
            ["200"],
            "300",
            subscribedIds));
    }

    [Fact]
    public void OnlyConfirmedUnsubscribedRetainedReferenceGetsSyntheticCard()
    {
        var unavailable = CreateMetadata(isAvailable: false, isDownloadPending: false);

        Assert.True(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: true,
            subscriptionBaselineInitialized: true,
            unavailable,
            isSubscribed: false));
        Assert.False(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: false,
            subscriptionBaselineInitialized: true,
            unavailable,
            isSubscribed: false));
        Assert.False(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: true,
            subscriptionBaselineInitialized: false,
            unavailable,
            isSubscribed: false));
    }

    [Fact]
    public void PendingOrSubscribedReferenceNeverGetsSyntheticCard()
    {
        Assert.False(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: true,
            subscriptionBaselineInitialized: true,
            CreateMetadata(isAvailable: false, isDownloadPending: true),
            isSubscribed: false));
        Assert.False(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: true,
            subscriptionBaselineInitialized: true,
            CreateMetadata(isAvailable: false, isDownloadPending: false),
            isSubscribed: true));
        Assert.False(InvokeShouldAddRetainedMissingAddon(
            retainMissingReferences: true,
            subscriptionBaselineInitialized: true,
            CreateMetadata(isAvailable: true, isDownloadPending: false),
            isSubscribed: false));
    }

    [Fact]
    public void SubscribeCountExplainsAvailableCardsVersusFullMembership()
    {
        Assert.Equal(
            "(利用可能 300 / 購読中 1021)",
            InvokeFormatSubscriptionCountDisplay(300, 300, 1021, japanese: true));
        Assert.Equal(
            "(Showing 42 / Available 300 / Subscribed 1021)",
            InvokeFormatSubscriptionCountDisplay(42, 300, 1021, japanese: false));
    }

    [Fact]
    public void SubscribeDetailsIncludesAllMembershipOverOneThousandWithoutCreatingCards()
    {
        var subscribedIds = Enumerable.Range(1, 1021)
            .Select(index => (100000000L + index).ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var metadata = subscribedIds
            .Take(995)
            .ToDictionary(
                addonId => addonId,
                addonId => new WorkshopAddon(addonId, string.Empty)
                {
                    Title = $"Addon {addonId}",
                    IsAvailable = true,
                    IsGmaFile = true
                },
                StringComparer.Ordinal);
        var availableIds = subscribedIds
            .Take(300)
            .ToHashSet(StringComparer.Ordinal);

        var rows = InvokeBuildMembershipItems(
            subscribedIds,
            metadata,
            availableIds,
            isSubscribeAsset: true);

        Assert.Equal(1021, rows.Count);
        Assert.Equal(1021, rows.Select(row => row.AddonId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(300, rows.Count(row => !row.IsUnavailable));
        Assert.Equal(721, rows.Count(row => row.IsUnavailable));
        Assert.All(rows.Where(row => row.IsUnavailable), row => Assert.Equal(0.55, row.RowOpacity));
        Assert.All(rows, row => Assert.False(row.IsMissing));
    }

    [Fact]
    public void CustomDetailsKeepsExistingMissingReferenceBehavior()
    {
        var metadata = new Dictionary<string, WorkshopAddon>(StringComparer.Ordinal)
        {
            ["200"] = CreateMetadata(isAvailable: false, isDownloadPending: false)
        };

        var rows = InvokeBuildMembershipItems(
            ["200", "metadata-absent"],
            metadata,
            new HashSet<string>(StringComparer.Ordinal),
            isSubscribeAsset: false);

        var row = Assert.Single(rows);
        Assert.Equal("200", row.AddonId);
        Assert.True(row.IsMissing);
        Assert.False(row.IsUnavailable);
        Assert.Equal(1.0, row.RowOpacity);
    }

    private static WorkshopAddon CreateMetadata(
        bool isAvailable,
        bool isDownloadPending)
    {
        return new WorkshopAddon("200", string.Empty)
        {
            Title = "Test",
            IsAvailable = isAvailable,
            IsDownloadPending = isDownloadPending
        };
    }

    private static bool InvokeMatchesAssetMembership(
        string assetId,
        IReadOnlyCollection<string> assetAddonIds,
        string addonId,
        IReadOnlySet<string> subscribedAddonIds)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "MatchesAssetMembership",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(
            null,
            [assetId, assetAddonIds, addonId, subscribedAddonIds]));
    }

    private static bool InvokeShouldAddRetainedMissingAddon(
        bool retainMissingReferences,
        bool subscriptionBaselineInitialized,
        WorkshopAddon metadata,
        bool isSubscribed)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "ShouldAddRetainedMissingAddon",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(
            null,
            [
                retainMissingReferences,
                subscriptionBaselineInitialized,
                metadata,
                isSubscribed
            ]));
    }

    private static string InvokeFormatSubscriptionCountDisplay(
        int visibleCount,
        int availableCount,
        int subscribedCount,
        bool japanese)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "FormatSubscriptionCountDisplay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(
            null,
            [visibleCount, availableCount, subscribedCount, japanese]));
    }

    private static List<AssetAddonMembershipItem> InvokeBuildMembershipItems(
        IReadOnlyCollection<string> addonIds,
        IReadOnlyDictionary<string, WorkshopAddon> addonMetadata,
        IReadOnlySet<string> availableAddonIds,
        bool isSubscribeAsset)
    {
        var method = typeof(AssetDetailsDialog).GetMethod(
            "BuildMembershipItems",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<List<AssetAddonMembershipItem>>(method!.Invoke(
            null,
            [addonIds, addonMetadata, availableAddonIds, isSubscribeAsset]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GmodAddonManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
