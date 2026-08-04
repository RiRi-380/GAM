using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AssetVersionServiceTests
{
    [Fact]
    public void CreateSnapshot_StoresMembershipMetadataOnlyAndUsesMaximumVersionPlusOne()
    {
        var createdAtUtc = new DateTime(2026, 7, 31, 13, 45, 0, DateTimeKind.Utc);
        var service = new AssetVersionService(() => createdAtUtc);
        var asset = CreateAsset(AddonState.Excluded, "100", "missing-addon", "100", " ");
        asset.WorkshopCollectionId = "steam-collection";
        asset.AutoUpdateCollection = true;
        asset.VersionHistory.Add(new AssetVersion { Version = 1 });
        asset.VersionHistory.Add(new AssetVersion { Version = 3 });
        var originalMembership = asset.Addons.ToArray();

        var snapshot = service.CreateSnapshot(asset, "before cleanup");

        Assert.Equal(4, snapshot.Version);
        Assert.Equal(createdAtUtc, snapshot.CreatedAt);
        Assert.Equal(new[] { "100", "missing-addon" }, snapshot.AddonIds);
        Assert.Equal("before cleanup", snapshot.Note);
        Assert.Null(snapshot.GamContent);
        Assert.False(snapshot.IncludeAddonStates);
        Assert.Null(snapshot.AddonStates);
        Assert.False(snapshot.IsImportBaseline);
        Assert.Null(snapshot.NewlySubscribedAddonIds);
        Assert.Null(snapshot.ImportType);
        Assert.Equal(4, asset.CurrentVersion);
        Assert.Same(snapshot, asset.VersionHistory[^1]);
        Assert.Equal(originalMembership, asset.Addons);
        Assert.Equal(AddonState.Excluded, asset.GetWholeState());
        Assert.Equal("steam-collection", asset.WorkshopCollectionId);
        Assert.True(asset.AutoUpdateCollection);
    }

    [Fact]
    public void RestoreSnapshot_ChangesMembershipOnlyAndPreservesMissingIds()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Excluded, "current-1", "current-2");
        asset.IsFavorite = true;
        asset.WorkshopCollectionId = "steam-collection";
        asset.AutoUpdateCollection = true;
        asset.AddonStates["legacy-current"] = AddonState.Disabled;
        var snapshot = new AssetVersion
        {
            Version = 7,
            AddonIds = new List<string> { "missing-addon", "restored", "missing-addon" },
            GamContent = "legacy",
            IncludeAddonStates = true,
            AddonStates = new Dictionary<string, AddonState>
            {
                ["restored"] = AddonState.Enabled
            },
            IsImportBaseline = true,
            NewlySubscribedAddonIds = new List<string> { "restored" },
            ImportType = ImportTypes.Collection
        };
        asset.VersionHistory.Add(snapshot);

        var restored = service.RestoreSnapshot(asset, 7);

        Assert.True(restored);
        Assert.Equal(new[] { "missing-addon", "restored" }, asset.Addons);
        Assert.Equal(7, asset.CurrentVersion);
        Assert.Equal(AddonState.Excluded, asset.GetWholeState());
        Assert.True(asset.IsFavorite);
        Assert.Equal("steam-collection", asset.WorkshopCollectionId);
        Assert.True(asset.AutoUpdateCollection);
        Assert.Equal(AddonState.Disabled, asset.AddonStates["legacy-current"]);
        Assert.Null(snapshot.GamContent);
        Assert.False(snapshot.IncludeAddonStates);
        Assert.Null(snapshot.AddonStates);
        Assert.False(snapshot.IsImportBaseline);
        Assert.Null(snapshot.NewlySubscribedAddonIds);
        Assert.Null(snapshot.ImportType);
    }

    [Fact]
    public void DeleteSnapshot_DoesNotChangeLiveMembershipOrRenumberGaps()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Enabled, "live-1", "missing-live");
        asset.VersionHistory.AddRange(new[]
        {
            CreateVersion(1, "old-1"),
            CreateVersion(3, "live-1"),
            CreateVersion(8, "future-1")
        });
        asset.CurrentVersion = 3;
        var originalMembership = asset.Addons.ToArray();

        var deleted = service.DeleteSnapshot(asset, 3);

        Assert.True(deleted);
        Assert.Equal(new[] { 1, 8 }, asset.VersionHistory.Select(version => version.Version));
        Assert.Equal(originalMembership, asset.Addons);
        Assert.Equal(0, asset.CurrentVersion);
        Assert.Equal(9, service.GetNextVersionNumber(asset));
    }

    [Fact]
    public void ClearHistory_DoesNotChangeLiveMembership()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Disabled, "live-1", "missing-live");
        asset.VersionHistory.AddRange(new[]
        {
            CreateVersion(2, "old-1"),
            CreateVersion(5, "old-2")
        });
        asset.CurrentVersion = 5;
        var originalMembership = asset.Addons.ToArray();

        var removedCount = service.ClearHistory(asset);

        Assert.Equal(2, removedCount);
        Assert.Empty(asset.VersionHistory);
        Assert.Equal(0, asset.CurrentVersion);
        Assert.Equal(originalMembership, asset.Addons);
        Assert.Equal(AddonState.Disabled, asset.GetWholeState());
    }

    [Fact]
    public void CompareCurrentMembership_ReturnsBothSidesAndPreservesMissingIds()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Enabled, "shared", "current-only", "missing-shared");
        var snapshot = CreateVersion(4, "shared", "snapshot-only", "missing-shared");

        var difference = service.CompareCurrentMembership(asset, snapshot);

        Assert.True(difference.HasChanges);
        Assert.Equal(4, difference.Version);
        Assert.Equal(new[] { "current-only" }, difference.CurrentOnlyIds);
        Assert.Equal(new[] { "snapshot-only" }, difference.SnapshotOnlyIds);
        Assert.True(service.HasMembershipChanges(asset, snapshot));
    }

    [Fact]
    public void CompareCurrentMembership_IgnoresOrderDuplicatesAndWhitespace()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Enabled, " 100 ", "missing", "100");
        var snapshot = CreateVersion(2, "missing", "100");

        var difference = service.CompareCurrentMembership(asset, snapshot);

        Assert.False(difference.HasChanges);
        Assert.Empty(difference.CurrentOnlyIds);
        Assert.Empty(difference.SnapshotOnlyIds);
        Assert.False(service.HasMembershipChanges(asset, snapshot));
    }

    [Fact]
    public void RestoreSnapshot_MissingVersionDoesNothing()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Excluded, "live");
        asset.CurrentVersion = 2;
        var originalMembership = asset.Addons.ToArray();

        var restored = service.RestoreSnapshot(asset, 99);

        Assert.False(restored);
        Assert.Equal(originalMembership, asset.Addons);
        Assert.Equal(2, asset.CurrentVersion);
        Assert.Equal(AddonState.Excluded, asset.GetWholeState());
    }

    [Fact]
    public void VersionMutations_RejectSmartAssetMembership()
    {
        var service = new AssetVersionService();
        var asset = CreateAsset(AddonState.Enabled, "100");
        asset.MembershipRule = new AssetMembershipRule(
            AssetMembershipRuleKind.Tag,
            "Fun");
        asset.VersionHistory.Add(CreateVersion(1, "100"));

        Assert.Throws<InvalidOperationException>(
            () => service.CreateSnapshot(asset));
        Assert.Throws<InvalidOperationException>(
            () => service.RestoreSnapshot(asset, 1));
        Assert.Throws<InvalidOperationException>(
            () => service.DeleteSnapshot(asset, 1));
        Assert.Throws<InvalidOperationException>(
            () => service.ClearHistory(asset));
        Assert.Equal(["100"], asset.Addons);
        Assert.Single(asset.VersionHistory);
    }

    private static Asset CreateAsset(AddonState state, params string[] addonIds)
    {
        var asset = new Asset("Versioned Asset");
        asset.SetWholeState(state);
        asset.Addons.AddRange(addonIds);
        return asset;
    }

    private static AssetVersion CreateVersion(int version, params string[] addonIds)
    {
        return new AssetVersion
        {
            Version = version,
            CreatedAt = DateTime.UtcNow,
            AddonIds = addonIds.ToList(),
            IncludeAddonStates = false,
            AddonStates = null,
            IsImportBaseline = false,
            NewlySubscribedAddonIds = null,
            ImportType = null,
            GamContent = null
        };
    }
}
