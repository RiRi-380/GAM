using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using System.Globalization;

namespace GmodAddonManager.Core.Tests;

public sealed class WorkshopMetadataMergeServiceTests
{
    [Fact]
    public void NeedsSupplement_CoversPlaceholderTitleAndMissingPersistedPreview()
    {
        var complete = new WorkshopAddon("100", string.Empty)
        {
            Title = "Concrete title",
            ThumbnailUrl = "https://example.test/preview.jpg",
            WorkshopUpdatedAtUtc = new DateTime(2026, 8, 3, 1, 2, 3, DateTimeKind.Utc),
            Tags = ["fun"],
            Type = "Weapon"
        };
        var placeholder = new WorkshopAddon("200", string.Empty)
        {
            Title = "Workshop-200 (Pending Download)",
            ThumbnailUrl = complete.ThumbnailUrl,
            WorkshopUpdatedAtUtc = complete.WorkshopUpdatedAtUtc,
            Tags = complete.Tags,
            Type = complete.Type
        };
        var localizedPlaceholder = new WorkshopAddon("300", string.Empty)
        {
            Title = "ワークショップ-300",
            ThumbnailUrl = complete.ThumbnailUrl,
            WorkshopUpdatedAtUtc = complete.WorkshopUpdatedAtUtc,
            Tags = complete.Tags,
            Type = complete.Type
        };
        var missingPreview = new WorkshopAddon("400", string.Empty)
        {
            Title = complete.Title,
            ThumbnailUrl = string.Empty,
            WorkshopUpdatedAtUtc = complete.WorkshopUpdatedAtUtc,
            Tags = complete.Tags,
            Type = complete.Type
        };
        var missingWorkshopUpdatedAt = new WorkshopAddon("500", string.Empty)
        {
            Title = complete.Title,
            ThumbnailUrl = complete.ThumbnailUrl,
            Tags = complete.Tags,
            Type = complete.Type
        };

        Assert.False(WorkshopMetadataMergeService.NeedsSupplement(complete));
        Assert.True(WorkshopMetadataMergeService.NeedsSupplement(placeholder));
        Assert.True(WorkshopMetadataMergeService.NeedsSupplement(localizedPlaceholder));
        Assert.True(WorkshopMetadataMergeService.NeedsSupplement(missingPreview));
        Assert.True(WorkshopMetadataMergeService.NeedsSupplement(missingWorkshopUpdatedAt));
    }

    [Fact]
    public void Merge_PersistsWorkshopCoreMetadataAndExistingClassificationContract()
    {
        var target = new WorkshopAddon("200", string.Empty)
        {
            Title = "Workshop-200 (Pending Download)",
            NeedsTitleUpdate = true,
            ThumbnailUrl = string.Empty,
            Tags = [],
            Type = string.Empty
        };
        var updatedAt = new DateTimeOffset(2026, 8, 3, 4, 5, 6, TimeSpan.Zero);
        var details = new WorkshopItemDetails
        {
            Id = target.Id,
            Title = "Authoritative Workshop title",
            PreviewUrl = "https://example.test/200.jpg",
            TimeUpdated = updatedAt.ToUnixTimeSeconds(),
            Tags = ["Weapon", "Fun"]
        };

        var changes = new WorkshopMetadataMergeService().Merge(target, details);

        Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.Title));
        Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.ThumbnailUrl));
        Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.WorkshopUpdatedAtUtc));
        Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.Tags));
        Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.Type));
        Assert.Equal(details.Title, target.Title);
        Assert.False(target.NeedsTitleUpdate);
        Assert.Equal(details.PreviewUrl, target.ThumbnailUrl);
        Assert.Equal(updatedAt.UtcDateTime, target.WorkshopUpdatedAtUtc);
        Assert.Equal(["fun", "weapon"], target.Tags);
        Assert.Equal("Weapon", target.Type);
    }

    [Fact]
    public void Merge_DoesNotEraseKnownMetadataWhenFetchIsPartial()
    {
        var target = new WorkshopAddon("300", string.Empty)
        {
            Title = "Known title",
            ThumbnailUrl = "https://example.test/known.jpg",
            WorkshopUpdatedAtUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Tags = ["realism"],
            Type = "Model"
        };

        var changes = new WorkshopMetadataMergeService().Merge(
            target,
            new WorkshopItemDetails
            {
                Id = target.Id,
                Title = string.Empty,
                PreviewUrl = null,
                TimeUpdated = 0,
                Tags = null
            });

        Assert.Equal(WorkshopMetadataMergeChanges.None, changes);
        Assert.Equal("Known title", target.Title);
        Assert.Equal("https://example.test/known.jpg", target.ThumbnailUrl);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), target.WorkshopUpdatedAtUtc);
        Assert.Equal(["realism"], target.Tags);
        Assert.Equal("Model", target.Type);
    }

    [Fact]
    public void Merge_AcceptsLocalClassificationSeedWithoutWorkshopDetails()
    {
        var target = new WorkshopAddon("400", string.Empty)
        {
            Title = "Known title",
            Tags = [],
            Type = string.Empty
        };

        var changes = new WorkshopMetadataMergeService().Merge(
            target,
            details: null,
            supplementalTags: ["Scenery"],
            supplementalType: "Map");

        Assert.Equal(
            WorkshopMetadataMergeChanges.Tags | WorkshopMetadataMergeChanges.Type,
            changes);
        Assert.Equal(["scenic"], target.Tags);
        Assert.Equal("Map", target.Type);
    }

    [Fact]
    public void Merge_HasNoThreeHundredItemBoundary()
    {
        var merger = new WorkshopMetadataMergeService();
        var targets = Enumerable.Range(1, 1060)
            .Select(index => new WorkshopAddon(
                index.ToString(CultureInfo.InvariantCulture),
                string.Empty)
            {
                Title = $"Workshop-{index}",
                NeedsTitleUpdate = true,
                ThumbnailUrl = string.Empty,
                Tags = ["fun"],
                Type = "Tool"
            })
            .ToList();

        foreach (var target in targets)
        {
            var changes = merger.Merge(
                target,
                new WorkshopItemDetails
                {
                    Id = target.Id,
                    Title = $"Addon {target.Id}",
                    PreviewUrl = $"https://example.test/{target.Id}.jpg"
                });

            Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.Title));
            Assert.True(changes.HasFlag(WorkshopMetadataMergeChanges.ThumbnailUrl));
        }

        Assert.Equal(1060, targets.Count(target => !target.NeedsTitleUpdate));
        Assert.Equal(1060, targets.Count(target =>
            target.Title.StartsWith("Addon ", StringComparison.Ordinal)));
        Assert.Equal(1060, targets.Count(target =>
            !string.IsNullOrWhiteSpace(target.ThumbnailUrl)));
    }
}
