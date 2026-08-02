using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonSortServiceTests
{
    private readonly AddonSortService service = new();

    public static TheoryData<AddonSortMode, AddonSortDirection, string[]> AllModeCases =>
        new()
        {
            {
                AddonSortMode.RecentlySubscribed,
                AddonSortDirection.Ascending,
                new[] { "Bravo", "Charlie", "Alpha" }
            },
            {
                AddonSortMode.RecentlySubscribed,
                AddonSortDirection.Descending,
                new[] { "Alpha", "Charlie", "Bravo" }
            },
            {
                AddonSortMode.Name,
                AddonSortDirection.Ascending,
                new[] { "Alpha", "Bravo", "Charlie" }
            },
            {
                AddonSortMode.Name,
                AddonSortDirection.Descending,
                new[] { "Charlie", "Bravo", "Alpha" }
            },
            {
                AddonSortMode.Size,
                AddonSortDirection.Ascending,
                new[] { "Bravo", "Charlie", "Alpha" }
            },
            {
                AddonSortMode.Size,
                AddonSortDirection.Descending,
                new[] { "Alpha", "Charlie", "Bravo" }
            },
            {
                AddonSortMode.WorkshopUpdated,
                AddonSortDirection.Ascending,
                new[] { "Alpha", "Bravo", "Charlie" }
            },
            {
                AddonSortMode.WorkshopUpdated,
                AddonSortDirection.Descending,
                new[] { "Charlie", "Bravo", "Alpha" }
            }
        };

    [Theory]
    [MemberData(nameof(AllModeCases))]
    public void Sort_AllModesAndDirections_UseTheCompletePrimaryKey(
        AddonSortMode mode,
        AddonSortDirection direction,
        string[] expected)
    {
        var addons = new[]
        {
            Addon(
                "3",
                "Alpha",
                size: 300,
                firstSeen: Utc(2026, 1, 1, 10, 0, 0, 900).AddTicks(9),
                workshopUpdated: Utc(2025, 12, 31, 23, 59, 59, 999).AddTicks(9)),
            Addon(
                "2",
                "Bravo",
                size: 100,
                firstSeen: Utc(2025, 12, 31, 23, 59, 59, 999).AddTicks(8),
                workshopUpdated: Utc(2026, 1, 1, 10, 0, 0, 100).AddTicks(1)),
            Addon(
                "1",
                "Charlie",
                size: 200,
                firstSeen: Utc(2026, 1, 1, 10, 0, 0, 100).AddTicks(1),
                workshopUpdated: Utc(2026, 1, 1, 10, 0, 0, 900).AddTicks(9))
        };

        var result = Sort(addons.Reverse(), mode, direction);

        Assert.Equal(expected, result.Select(addon => addon.Title));
    }

    [Fact]
    public void Sort_DefaultsToRecentlySubscribedDescending_WithBaselineLastByName()
    {
        var older = Addon("1", "Older", firstSeen: Utc(2026, 1, 1));
        var newest = Addon("2", "Newest", firstSeen: Utc(2026, 2, 1));
        var baselineZ = Addon("3", "Zulu");
        var baselineA = Addon("4", "Alpha");

        var result = service.Sort(new[] { baselineZ, older, baselineA, newest });

        Assert.Equal(
            new[] { "Newest", "Older", "Alpha", "Zulu" },
            result.Select(addon => addon.Title));
    }

    [Fact]
    public void Sort_RecentlySubscribedAscending_KeepsBaselineAfterObservedEntries()
    {
        var older = Addon("1", "Older", firstSeen: Utc(2026, 1, 1));
        var newer = Addon("2", "Newer", firstSeen: Utc(2026, 2, 1));
        var baseline = Addon("3", "Baseline");

        var result = Sort(
            new[] { newer, baseline, older },
            AddonSortMode.RecentlySubscribed,
            AddonSortDirection.Ascending);

        Assert.Equal(
            new[] { "Older", "Newer", "Baseline" },
            result.Select(addon => addon.Title));
    }

    [Theory]
    [InlineData(AddonSortDirection.Ascending, "Alpha", "Bravo", "Zulu")]
    [InlineData(AddonSortDirection.Descending, "Zulu", "Bravo", "Alpha")]
    public void Sort_Name_UsesRequestedDirection(
        AddonSortDirection direction,
        params string[] expected)
    {
        var addons = new[]
        {
            Addon("1", "Zulu"),
            Addon("2", "Alpha"),
            Addon("3", "Bravo")
        };

        var result = Sort(addons, AddonSortMode.Name, direction);

        Assert.Equal(expected, result.Select(addon => addon.Title));
    }

    [Theory]
    [InlineData(AddonSortDirection.Ascending, "Small", "Alpha large", "Zulu large")]
    [InlineData(AddonSortDirection.Descending, "Alpha large", "Zulu large", "Small")]
    public void Sort_Size_UsesDirectionAndStableNameTieBreak(
        AddonSortDirection direction,
        params string[] expected)
    {
        var addons = new[]
        {
            Addon("1", "Zulu large", size: 200),
            Addon("2", "Small", size: 100),
            Addon("3", "Alpha large", size: 200)
        };

        var result = Sort(addons, AddonSortMode.Size, direction);

        Assert.Equal(expected, result.Select(addon => addon.Title));
    }

    [Fact]
    public void Sort_WorkshopUpdated_PrefersWorkshopTimestampOverLastUpdated()
    {
        var workshopNew = Addon(
            "1",
            "Workshop new",
            lastUpdated: Utc(2020, 1, 1),
            workshopUpdated: Utc(2026, 3, 1));
        var localNew = Addon(
            "2",
            "Local new",
            lastUpdated: Utc(2027, 1, 1),
            workshopUpdated: Utc(2026, 1, 1));

        var result = Sort(
            new[] { localNew, workshopNew },
            AddonSortMode.WorkshopUpdated,
            AddonSortDirection.Descending);

        Assert.Equal(
            new[] { "Workshop new", "Local new" },
            result.Select(addon => addon.Title));
    }

    [Fact]
    public void Sort_WorkshopUpdated_FallsBackToLastUpdatedWhenWorkshopTimeMissing()
    {
        var fallbackNew = Addon(
            "1",
            "Fallback new",
            lastUpdated: Utc(2026, 3, 1));
        var workshopOld = Addon(
            "2",
            "Workshop old",
            lastUpdated: Utc(2027, 1, 1),
            workshopUpdated: Utc(2026, 1, 1));

        var result = Sort(
            new[] { workshopOld, fallbackNew },
            AddonSortMode.WorkshopUpdated,
            AddonSortDirection.Descending);

        Assert.Equal(
            new[] { "Fallback new", "Workshop old" },
            result.Select(addon => addon.Title));
    }

    [Fact]
    public void Sort_WorkshopUpdated_UsesNameTieBreakInBothDirections()
    {
        var sameTime = Utc(2026, 1, 1);
        var zulu = Addon("1", "Zulu", workshopUpdated: sameTime);
        var alpha = Addon("2", "Alpha", workshopUpdated: sameTime);

        var ascending = Sort(
            new[] { zulu, alpha },
            AddonSortMode.WorkshopUpdated,
            AddonSortDirection.Ascending);
        var descending = Sort(
            new[] { zulu, alpha },
            AddonSortMode.WorkshopUpdated,
            AddonSortDirection.Descending);

        Assert.Equal(new[] { "Alpha", "Zulu" }, ascending.Select(addon => addon.Title));
        Assert.Equal(new[] { "Alpha", "Zulu" }, descending.Select(addon => addon.Title));
    }

    [Theory]
    [InlineData(AddonSortMode.RecentlySubscribed, AddonSortDirection.Ascending)]
    [InlineData(AddonSortMode.RecentlySubscribed, AddonSortDirection.Descending)]
    [InlineData(AddonSortMode.Name, AddonSortDirection.Ascending)]
    [InlineData(AddonSortMode.Name, AddonSortDirection.Descending)]
    [InlineData(AddonSortMode.Size, AddonSortDirection.Ascending)]
    [InlineData(AddonSortMode.Size, AddonSortDirection.Descending)]
    [InlineData(AddonSortMode.WorkshopUpdated, AddonSortDirection.Ascending)]
    [InlineData(AddonSortMode.WorkshopUpdated, AddonSortDirection.Descending)]
    public void Sort_AllModesAndDirections_UseStableIdForExactTies(
        AddonSortMode mode,
        AddonSortDirection direction)
    {
        var timestamp = Utc(2026, 1, 1, 12, 34, 56, 789);
        var addons = new[]
        {
            Addon("3", "Same", 42, timestamp, timestamp, timestamp),
            Addon("1", "Same", 42, timestamp, timestamp, timestamp),
            Addon("2", "Same", 42, timestamp, timestamp, timestamp)
        };

        var result = Sort(addons, mode, direction);

        Assert.Equal(new[] { "1", "2", "3" }, result.Select(addon => addon.Id));
    }

    [Fact]
    public void GetSortTimestampUtc_NormalizesObservedAndFallbackValues()
    {
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var unspecified = new DateTime(2025, 12, 31, 23, 59, 58, DateTimeKind.Unspecified);
        var addon = Addon(
            "1",
            "Timestamp",
            firstSeen: local,
            lastUpdated: unspecified);

        var observed = AddonSortService.GetSortTimestampUtc(
            addon,
            AddonSortMode.RecentlySubscribed);
        var fallback = AddonSortService.GetSortTimestampUtc(
            addon,
            AddonSortMode.WorkshopUpdated);

        Assert.Equal(local.ToUniversalTime(), observed);
        Assert.Equal(DateTimeKind.Utc, observed?.Kind);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), fallback);
        Assert.Equal(DateTimeKind.Utc, fallback?.Kind);
        Assert.Null(AddonSortService.GetSortTimestampUtc(addon, AddonSortMode.Name));
        Assert.Null(AddonSortService.GetSortTimestampUtc(addon, AddonSortMode.Size));
    }

    [Fact]
    public void Sort_DoesNotMutateInputOrder()
    {
        var zulu = Addon("1", "Zulu");
        var alpha = Addon("2", "Alpha");
        var input = new List<WorkshopAddon> { zulu, alpha };

        _ = Sort(
            input,
            AddonSortMode.Name,
            AddonSortDirection.Ascending);

        Assert.Same(zulu, input[0]);
        Assert.Same(alpha, input[1]);
    }

    private IReadOnlyList<WorkshopAddon> Sort(
        IEnumerable<WorkshopAddon> addons,
        AddonSortMode mode,
        AddonSortDirection direction)
    {
        return service.Sort(
            addons,
            new AddonSortOptions
            {
                Mode = mode,
                Direction = direction
            });
    }

    private static WorkshopAddon Addon(
        string id,
        string title,
        long size = 0,
        DateTime? firstSeen = null,
        DateTime? lastUpdated = null,
        DateTime? workshopUpdated = null)
    {
        return new WorkshopAddon
        {
            Id = id,
            Title = title,
            Size = size,
            FirstSeenSubscribedAtUtc = firstSeen,
            LastUpdated = lastUpdated ?? DateTime.UnixEpoch,
            WorkshopUpdatedAtUtc = workshopUpdated
        };
    }

    private static DateTime Utc(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0,
        int second = 0,
        int millisecond = 0)
    {
        return new DateTime(
            year,
            month,
            day,
            hour,
            minute,
            second,
            millisecond,
            DateTimeKind.Utc);
    }
}
