using System.Diagnostics;
using System.Globalization;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Tests;

public sealed class SmartAssetReconciliationServiceTests
{
    [Fact]
    public void Reconcile_AddsMatchesRemovesConfirmedNonmatchesAndKeepsUnknownMembers()
    {
        var configuration = CreateConfiguration();
        var smart = CreateSmartAsset(
            "Fun",
            AssetMembershipRuleKind.Tag,
            "Fun",
            ["1", "2", "5", "999", "local_test"]);
        configuration.Assets.Add(smart);
        configuration.AddonMetadata["1"] = AddonWithTags("1", ["Fun"]);
        configuration.AddonMetadata["2"] = AddonWithTags("2", ["Build"]);
        configuration.AddonMetadata["3"] = AddonWithTags("3", ["Fun"]);
        configuration.AddonMetadata["4"] = new WorkshopAddon("4", string.Empty);
        configuration.AddonMetadata["5"] = new WorkshopAddon("5", string.Empty);

        var result = new SmartAssetReconciliationService().Reconcile(
            configuration,
            ["1", "2", "3", "4", "5"]);

        Assert.True(result.IsAuthoritative);
        Assert.True(result.MembershipChanged);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(3, result.RemovedCount);
        Assert.Equal(2, result.UnknownCount);
        Assert.Equal(["1", "3", "5"], smart.Addons);
        var change = Assert.Single(result.Assets);
        Assert.Equal(["3"], change.AddedAddonIds);
        Assert.Equal(["2", "999", "local_test"], change.RemovedAddonIds);
        Assert.Equal(["4", "5"], change.UnknownAddonIds);
    }

    [Fact]
    public void Reconcile_ConditionLossRemovesMemberOnlyWhenMetadataIsConfirmed()
    {
        var configuration = CreateConfiguration();
        var smart = CreateSmartAsset(
            "Maps",
            AssetMembershipRuleKind.Type,
            "Map",
            ["1", "2"]);
        configuration.Assets.Add(smart);
        configuration.AddonMetadata["1"] = new WorkshopAddon("1", string.Empty)
        {
            Type = "Weapon",
            TypeMetadataStatus = AddonClassificationMetadataStatus.Known
        };
        configuration.AddonMetadata["2"] = new WorkshopAddon("2", string.Empty)
        {
            Type = string.Empty,
            TypeMetadataStatus = AddonClassificationMetadataStatus.Unknown,
            Tags = ["Fun"],
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };

        var result = new SmartAssetReconciliationService().Reconcile(
            configuration,
            ["1", "2"]);

        Assert.Equal(["2"], smart.Addons);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.UnknownCount);
    }

    [Fact]
    public void Reconcile_MalformedRuleFreezesWithoutChangingMembership()
    {
        var configuration = CreateConfiguration();
        var smart = CreateSmartAsset(
            "Future",
            AssetMembershipRuleKind.Tag,
            "Fun",
            ["1", "not-workshop"]);
        smart.MembershipRule!.SchemaVersion = 99;
        configuration.Assets.Add(smart);

        var result = new SmartAssetReconciliationService().Reconcile(
            configuration,
            ["1"]);

        Assert.Equal(["1", "not-workshop"], smart.Addons);
        Assert.False(result.MembershipChanged);
        Assert.Equal(1, result.FrozenAssetCount);
        Assert.Equal(
            SmartAssetAutomationStatus.FrozenInvalidRule,
            smart.SmartAutomationState!.Status);
        Assert.NotNull(smart.SmartAutomationState.Message);
    }

    [Fact]
    public void Reconcile_UnknownSerializedRuleKindFreezesInsteadOfBreakingProfileLoad()
    {
        var smart = JsonConvert.DeserializeObject<Asset>(
            """
            {
              "id": "future-smart",
              "name": "Future Smart",
              "addons": ["1"],
              "membershipRule": {
                "schemaVersion": 1,
                "kind": "FutureKind",
                "value": "Fun"
              }
            }
            """)!;
        var configuration = CreateConfiguration();
        configuration.Assets.Add(smart);

        var result = new SmartAssetReconciliationService().Reconcile(
            configuration,
            ["1"]);

        Assert.Equal(AssetMembershipRuleKind.Unknown, smart.MembershipRule!.Kind);
        Assert.Equal(["1"], smart.Addons);
        Assert.Equal(1, result.FrozenAssetCount);
        Assert.Equal(
            SmartAssetAutomationStatus.FrozenInvalidRule,
            smart.SmartAutomationState!.Status);
    }

    [Fact]
    public void Reconcile_OneThousandAddonsAndTenRules_StaysInMemoryAndBounded()
    {
        var configuration = CreateConfiguration();
        var ids = Enumerable.Range(1, 1000)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        foreach (var id in ids)
        {
            var numeric = int.Parse(id, CultureInfo.InvariantCulture);
            configuration.AddonMetadata[id] = new WorkshopAddon(id, string.Empty)
            {
                Type = numeric % 2 == 0 ? "Map" : "Weapon",
                TypeMetadataStatus = AddonClassificationMetadataStatus.Known,
                Tags = numeric % 3 == 0 ? ["Fun"] : ["Build"],
                TagsMetadataStatus = AddonClassificationMetadataStatus.Known
            };
        }
        for (var index = 0; index < 5; index++)
        {
            configuration.Assets.Add(CreateSmartAsset(
                "Maps " + index,
                AssetMembershipRuleKind.Type,
                "Map",
                []));
            configuration.Assets.Add(CreateSmartAsset(
                "Fun " + index,
                AssetMembershipRuleKind.Tag,
                "Fun",
                []));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = new SmartAssetReconciliationService().Reconcile(
            configuration,
            ids);
        stopwatch.Stop();

        Assert.Equal(10, result.Assets.Count);
        Assert.Equal(5 * 500 + 5 * 333, result.AddedCount);
        Assert.All(
            configuration.Assets.Where(asset =>
                asset.IsSmart &&
                asset.Name.StartsWith("Maps", StringComparison.Ordinal)),
            asset => Assert.Equal(500, asset.Addons.Count));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"In-memory Smart Asset reconcile took {stopwatch.Elapsed}.");
    }

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration();
        configuration.CreateDefaultAssets();
        return configuration;
    }

    private static Asset CreateSmartAsset(
        string name,
        AssetMembershipRuleKind kind,
        string value,
        IEnumerable<string> members)
    {
        return new Asset(name)
        {
            MembershipRule = new AssetMembershipRule(kind, value),
            SmartAutomationState = new SmartAssetAutomationState(),
            Addons = members.ToList()
        };
    }

    private static WorkshopAddon AddonWithTags(string id, string[] tags)
    {
        return new WorkshopAddon(id, string.Empty)
        {
            Tags = tags,
            TagsMetadataStatus = AddonClassificationMetadataStatus.Known
        };
    }
}
