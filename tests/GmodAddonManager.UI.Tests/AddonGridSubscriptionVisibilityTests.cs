using System.Reflection;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonGridSubscriptionVisibilityTests
{
    [Fact]
    public void OnlySearchTextUsesTheThrottledFilterPipeline()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs"));

        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "this.WhenAnyValue(x => x.FilterText)\n            .Throttle(TimeSpan.FromMilliseconds(300))\n            .ObserveOn(RxApp.MainThreadScheduler)",
            normalized);
        Assert.DoesNotContain("x => x.CurrentAsset,", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("x => x.ShowOnlyAssetAddons,", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetSelectionAppliesTheFilterImmediately()
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
        Assert.Contains("ApplyFilter();", source[methodStart..methodEnd]);
    }

    [Fact]
    public void AssetDetailsUseSummaryInsteadOfDuplicatingTheAddonGrid()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GmodAddonManager.UI",
            "Views",
            "AssetDetailsDialog.axaml"));

        Assert.DoesNotContain("ItemsSource=\"{Binding Addons}\"", source);
        Assert.Contains("Text=\"{Binding MemberCountText}\"", source);
        Assert.Contains("Text=\"{Binding AvailableCountText}\"", source);
        Assert.Contains("Text=\"{Binding MissingCountText}\"", source);
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
            subscribedIds,
            isLocal: false));
        Assert.False(InvokeMatchesAssetMembership(
            "subscribe-system-asset",
            ["*"],
            "200",
            subscribedIds,
            isLocal: false));
    }

    [Fact]
    public void CustomAssetMembershipRemainsIndependentOfSubscriptionMembership()
    {
        var subscribedIds = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(InvokeMatchesAssetMembership(
            "custom",
            ["200"],
            "200",
            subscribedIds,
            isLocal: false));
        Assert.False(InvokeMatchesAssetMembership(
            "custom",
            ["200"],
            "300",
            subscribedIds,
            isLocal: false));
    }

    [Fact]
    public void LocalAddonIsDiscoverableOnlyBesideInitialSubscribeInventory()
    {
        var noSubscriptions = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(InvokeMatchesAssetMembership(
            "subscribe-system-asset",
            ["*"],
            "local-folder-id",
            noSubscriptions,
            isLocal: true));
        Assert.False(InvokeMatchesAssetMembership(
            "custom",
            ["local-folder-id"],
            "local-folder-id",
            noSubscriptions,
            isLocal: true));
    }

    [Fact]
    public void ImportedFixedAssetRetentionProducesOneVisibleRowPerReference()
    {
        var asset = new Asset("Imported")
        {
            Addons = ["100", "999"],
            RetainMissingReferences = true
        };
        var unavailable = new WorkshopAddon("999", string.Empty)
        {
            Title = "Unavailable",
            IsAvailable = false,
            IsDownloadPending = false
        };
        var config = new Configuration
        {
            Assets = [asset],
            RetainMissingAssetReferences = false,
            SubscriptionBaselineInitialized = true,
            AddonMetadata = new Dictionary<string, WorkshopAddon>(StringComparer.Ordinal)
            {
                [unavailable.Id] = unavailable
            }
        };
        var inventory = new List<WorkshopAddon>
        {
            new("100", string.Empty)
            {
                Title = "Installed",
                IsAvailable = true,
                IsDownloadPending = false
            }
        };
        var subscribedIds = new HashSet<string>(StringComparer.Ordinal) { "100" };

        InvokeAddRetainedMissingAddons(inventory, config, subscribedIds);
        var visibleRows = inventory.Where(addon => InvokeMatchesAssetMembership(
            asset.Id,
            asset.Addons,
            addon.Id,
            subscribedIds,
            addon.IsLocal)).ToList();

        Assert.Equal(asset.Addons.Count, visibleRows.Count);
        var synthetic = Assert.Single(visibleRows, addon => addon.Id == "999");
        Assert.False(synthetic.IsAvailable);
        Assert.False(synthetic.IsDownloadPending);
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
            InvokeFormatSubscriptionCountDisplay(300, 300, 1021, 0, japanese: true));
        Assert.Equal(
            "(Showing 42 / Available 300 / Subscribed 1021)",
            InvokeFormatSubscriptionCountDisplay(42, 300, 1021, 0, japanese: false));
        Assert.Equal(
            "(Available 300 / Subscribed 1021 / Local 2)",
            InvokeFormatSubscriptionCountDisplay(300, 300, 1021, 2, japanese: false));
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
        IReadOnlySet<string> subscribedAddonIds,
        bool isLocal)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "MatchesAssetMembership",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(
            null,
            [assetId, assetAddonIds, addonId, subscribedAddonIds, isLocal]));
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
        int visibleLocalCount,
        bool japanese)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "FormatSubscriptionCountDisplay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(
            null,
            [visibleCount, availableCount, subscribedCount, visibleLocalCount, japanese]));
    }

    private static void InvokeAddRetainedMissingAddons(
        IList<WorkshopAddon> inventory,
        Configuration config,
        IReadOnlySet<string> subscribedAddonIds)
    {
        var method = typeof(AddonGridViewModel).GetMethod(
            "AddRetainedMissingAddons",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(
            null,
            [
                inventory,
                config,
                subscribedAddonIds,
                (Func<string, bool>)(_ => false),
                CancellationToken.None
            ]);
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
