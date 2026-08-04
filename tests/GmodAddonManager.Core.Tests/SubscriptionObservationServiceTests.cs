using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SubscriptionObservationServiceTests
{
    [Fact]
    public void Observe_FirstAuthoritativeSnapshotCreatesUntimedBaseline()
    {
        var configuration = new Configuration();
        configuration.AddonMetadata["100"] = new WorkshopAddon("100", string.Empty)
        {
            FirstSeenSubscribedAtUtc = new DateTime(
                2025,
                1,
                2,
                3,
                4,
                5,
                DateTimeKind.Utc)
        };
        var snapshot = new SteamWorkshopSnapshot(
            subscribedIds: ["200", "100"],
            installedIds: ["100"],
            isAuthoritative: true,
            observedAtUtc: new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));

        var result = new SubscriptionObservationService().Observe(configuration, snapshot);

        Assert.True(result.Changed);
        Assert.Equal(1, result.PendingDownloadCount);
        Assert.Equal(["100", "200"], configuration.KnownSubscribedAddonIds);
        Assert.Empty(configuration.SubscriptionFirstSeenAtUtc);
        Assert.Null(configuration.AddonMetadata["100"].FirstSeenSubscribedAtUtc);
    }

    [Fact]
    public void Observe_SubsequentNewSubscriptionGetsStableFirstSeenTimestamp()
    {
        var configuration = new Configuration
        {
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100"]
        };
        configuration.AddonMetadata["200"] = new WorkshopAddon("200", string.Empty);
        var observedAt = new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc);
        var service = new SubscriptionObservationService();

        var first = service.Observe(
            configuration,
            new SteamWorkshopSnapshot(["100", "200"], ["100", "200"], true, observedAt));
        var second = service.Observe(
            configuration,
            new SteamWorkshopSnapshot(["100", "200"], ["100", "200"], true, observedAt.AddHours(1)));

        Assert.Equal(["200"], first.NewlySubscribedIds);
        Assert.Empty(second.NewlySubscribedIds);
        Assert.Equal(observedAt, configuration.SubscriptionFirstSeenAtUtc["200"]);
        Assert.Equal(observedAt, configuration.AddonMetadata["200"].FirstSeenSubscribedAtUtc);
    }

    [Fact]
    public void Observe_NonAuthoritativeSnapshotDoesNotMutateHistory()
    {
        var configuration = new Configuration
        {
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100"]
        };

        var result = new SubscriptionObservationService().Observe(
            configuration,
            new SteamWorkshopSnapshot([], [], false, DateTime.UtcNow));

        Assert.False(result.Changed);
        Assert.False(result.IsAuthoritative);
        Assert.Equal(["100"], configuration.KnownSubscribedAddonIds);
    }

    [Fact]
    public void Observe_UnsubscribeClearsConfigurationAndMetadataFirstSeenTimestamps()
    {
        var firstSeen = new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc);
        var configuration = new Configuration
        {
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100", "200"],
            SubscriptionFirstSeenAtUtc = new Dictionary<string, DateTime>
            {
                ["200"] = firstSeen
            }
        };
        configuration.AddonMetadata["200"] = new WorkshopAddon("200", string.Empty)
        {
            FirstSeenSubscribedAtUtc = firstSeen
        };

        var result = new SubscriptionObservationService().Observe(
            configuration,
            new SteamWorkshopSnapshot(["100"], ["100"], true, firstSeen.AddHours(1)));

        Assert.Equal(["200"], result.UnsubscribedIds);
        Assert.DoesNotContain("200", configuration.SubscriptionFirstSeenAtUtc.Keys);
        Assert.Null(configuration.AddonMetadata["200"].FirstSeenSubscribedAtUtc);
    }

    [Fact]
    public void Observe_RepairsMetadataAndOrphansFromCanonicalFirstSeenHistory()
    {
        var canonical = new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc);
        var stale = canonical.AddDays(-30);
        var configuration = new Configuration
        {
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100", "200"],
            SubscriptionFirstSeenAtUtc = new Dictionary<string, DateTime>
            {
                ["100"] = canonical,
                ["999"] = stale
            }
        };
        configuration.AddonMetadata["100"] = new WorkshopAddon("100", string.Empty)
        {
            FirstSeenSubscribedAtUtc = stale
        };
        configuration.AddonMetadata["200"] = new WorkshopAddon("200", string.Empty)
        {
            FirstSeenSubscribedAtUtc = stale
        };
        configuration.AddonMetadata["999"] = new WorkshopAddon("999", string.Empty)
        {
            FirstSeenSubscribedAtUtc = stale
        };

        var result = new SubscriptionObservationService().Observe(
            configuration,
            new SteamWorkshopSnapshot(
                ["100", "200"],
                ["100", "200"],
                true,
                canonical.AddHours(1)));

        Assert.True(result.Changed);
        Assert.Equal(canonical, configuration.SubscriptionFirstSeenAtUtc["100"]);
        Assert.DoesNotContain("999", configuration.SubscriptionFirstSeenAtUtc.Keys);
        Assert.Equal(canonical, configuration.AddonMetadata["100"].FirstSeenSubscribedAtUtc);
        Assert.Null(configuration.AddonMetadata["200"].FirstSeenSubscribedAtUtc);
        Assert.Null(configuration.AddonMetadata["999"].FirstSeenSubscribedAtUtc);
    }
}
