using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Tests;

public sealed class ConfigurationMigrationServiceTests
{
    [Fact]
    public void Migrate_NormalizesSubscribeAndUniformCustomAssets()
    {
        var raw = JObject.Parse(
            """
            {
              "version": "1.0",
              "assets": [
                {
                  "id": "subscribe-system-asset",
                  "name": "Subscribe Asset",
                  "isSystem": true,
                  "enabled": false,
                  "addons": ["*", "100"],
                  "addonStates": { "100": 2 },
                  "defaultAddonState": 0
                },
                {
                  "id": "fps",
                  "name": "FPS",
                  "isSystem": false,
                  "enabled": true,
                  "addons": ["100", "200"],
                  "addonStates": { "100": 2, "200": 2 },
                  "defaultAddonState": 0
                }
              ],
              "addonMetadata": {},
              "junctionHistory": {}
            }
            """);
        var config = raw.ToObject<Configuration>()!;

        var result = new ConfigurationMigrationService().Migrate(raw, config, removeLegacyJunctionAsset: true);

        Assert.True(result.Changed);
        Assert.Equal(Configuration.CurrentSchemaVersion, config.SchemaVersion);
        var subscribe = Assert.Single(config.Assets, a => a.Id == "subscribe-system-asset");
        Assert.Equal(AddonState.Disabled, subscribe.GetWholeState());
        Assert.Equal(["*"], subscribe.Addons);
        var fps = Assert.Single(config.Assets, a => a.Id == "fps");
        Assert.Equal(AddonState.Excluded, fps.GetWholeState());
        Assert.Empty(fps.AddonStates);
        Assert.False(fps.NeedsMigrationReview);
    }

    [Fact]
    public void Migrate_MixedCustomAssetBecomesDisabledAndNeedsReviewWithoutSplitting()
    {
        var raw = JObject.Parse(
            """
            {
              "assets": [
                {
                  "id": "mixed",
                  "name": "Mixed",
                  "isSystem": false,
                  "enabled": true,
                  "addons": ["100", "200"],
                  "addonStates": { "100": 0, "200": 2 },
                  "defaultAddonState": 1
                }
              ]
            }
            """);
        var config = raw.ToObject<Configuration>()!;

        var result = new ConfigurationMigrationService().Migrate(raw, config, removeLegacyJunctionAsset: true);

        var mixed = Assert.Single(config.Assets, a => a.Id == "mixed");
        Assert.Equal(AddonState.Disabled, mixed.GetWholeState());
        Assert.True(mixed.NeedsMigrationReview);
        Assert.Equal(["100", "200"], mixed.Addons);
        Assert.Contains("mixed", result.NeedsReviewAssetIds);
        Assert.Single(config.Assets, a => a.Id == "subscribe-system-asset");
    }

    [Fact]
    public void Migrate_RemovesJunctionAndStripsVersionRuntimeMetadata()
    {
        var raw = JObject.Parse(
            """
            {
              "assets": [
                {
                  "id": "junction-system-asset",
                  "name": "Junction",
                  "isSystem": true,
                  "enabled": false,
                  "addons": ["100"]
                },
                {
                  "id": "custom",
                  "name": "Custom",
                  "enabled": true,
                  "addons": ["100"],
                  "defaultAddonState": 0,
                  "workshopCollectionId": "123",
                  "autoUpdateCollection": true,
                  "currentVersion": 7,
                  "versionHistory": [{
                    "version": 7,
                    "createdAt": "2026-01-01T00:00:00Z",
                    "addonIds": ["200", "100"],
                    "gamContent": "legacy",
                    "includeAddonStates": true,
                    "addonStates": { "100": 2 },
                    "isImportBaseline": true,
                    "newlySubscribedAddonIds": ["200"],
                    "importType": "GAM",
                    "note": "keep"
                  }]
                }
              ],
              "junctionHistory": {
                "100": ["custom", "missing-source-asset"]
              }
            }
            """);
        var config = raw.ToObject<Configuration>()!;

        var result = new ConfigurationMigrationService().Migrate(raw, config, removeLegacyJunctionAsset: true);

        Assert.DoesNotContain(config.Assets, a => a.Id == "junction-system-asset");
        Assert.Contains("junction-system-asset", result.RemovedLegacySystemAssetIds);
        var custom = Assert.Single(config.Assets, a => a.Id == "custom");
        Assert.Null(custom.WorkshopCollectionId);
        Assert.False(custom.AutoUpdateCollection);
        var version = Assert.Single(custom.VersionHistory);
        Assert.Equal(["100", "200"], version.AddonIds);
        Assert.Equal("keep", version.Note);
        Assert.Null(version.GamContent);
        Assert.Null(version.AddonStates);
        Assert.False(version.IsImportBaseline);
        Assert.Equal(
            ["custom", "missing-source-asset"],
            config.JunctionHistory["100"]);

        var serialized = JsonConvert.SerializeObject(config);
        Assert.DoesNotContain("addonStates", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("gamContent", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("workshopCollectionId", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_CanonicalizesDuplicateSubscribeAssetsAtFirstPosition()
    {
        var raw = JObject.Parse(
            """
            {
              "assets": [
                {
                  "id": "custom",
                  "name": "Custom",
                  "enabled": true,
                  "addons": ["100"]
                },
                {
                  "id": "subscribe-system-asset",
                  "name": "Subscribe",
                  "isSystem": true,
                  "enabled": false,
                  "addons": ["100"]
                },
                {
                  "id": "subscribe-system-asset",
                  "name": "Subscribe Asset",
                  "isSystem": true,
                  "enabled": true,
                  "addons": ["*"]
                }
              ]
            }
            """);
        var config = raw.ToObject<Configuration>()!;

        new ConfigurationMigrationService().Migrate(
            raw,
            config,
            removeLegacyJunctionAsset: true);

        var subscribe = Assert.Single(
            config.Assets,
            asset => asset.Id == "subscribe-system-asset");
        Assert.Same(subscribe, config.Assets[0]);
        Assert.Equal("Subscribe Asset", subscribe.Name);
        Assert.True(subscribe.IsSystem);
        Assert.False(subscribe.IsFavorite);
        Assert.Equal(AddonState.Disabled, subscribe.GetWholeState());
        Assert.Equal(["*"], subscribe.Addons);
    }

    [Fact]
    public void NormalizeCurrentSchema_IsIdempotent()
    {
        var config = new Configuration();
        config.CreateDefaultAssets();
        var subscribe = config.Assets.Single(a => a.Id == "subscribe-system-asset");
        subscribe.Name = "Renamed";
        subscribe.State = AddonState.Excluded;
        subscribe.IsFavorite = true;
        subscribe.Addons = ["100"];
        config.Assets.Add(new Asset("Junction", isSystem: true)
        {
            Id = "junction-system-asset",
            Addons = ["300"]
        });
        var custom = new Asset("Custom")
        {
            Addons = ["200", "100", "100"],
            CurrentVersion = 7,
            VersionHistory = [new AssetVersion(3, ["100"])],
            AddonStates = new Dictionary<string, AddonState>
            {
                ["100"] = AddonState.Excluded
            }
        };
        config.Assets.Add(custom);
        var service = new ConfigurationMigrationService();
        config.JunctionHistory["100"] = ["custom", "missing-source-asset"];

        service.NormalizeCurrentSchema(config);
        var once = JsonConvert.SerializeObject(config);
        service.NormalizeCurrentSchema(config);
        var twice = JsonConvert.SerializeObject(config);

        Assert.Equal(once, twice);
        Assert.DoesNotContain(config.Assets, a => a.Id == "junction-system-asset");
        subscribe = Assert.Single(config.Assets, a => a.Id == "subscribe-system-asset");
        Assert.Equal("Subscribe Asset", subscribe.Name);
        Assert.Equal(AddonState.Enabled, subscribe.GetWholeState());
        Assert.False(subscribe.IsFavorite);
        Assert.Equal(["*"], subscribe.Addons);
        Assert.Equal(["100", "200"], config.Assets.Single(a => !a.IsSystem).Addons);
        Assert.Equal(0, custom.CurrentVersion);
        Assert.Equal(
            ["custom", "missing-source-asset"],
            config.JunctionHistory["100"]);
    }

    [Fact]
    public void NormalizeCurrentSchema_ResetsCurrentVersionWhenSnapshotIsMissing()
    {
        var config = new Configuration();
        config.CreateDefaultAssets();
        var custom = new Asset("Versioned")
        {
            CurrentVersion = 9,
            VersionHistory = [new AssetVersion(1, ["100"])]
        };
        config.Assets.Add(custom);

        var service = new ConfigurationMigrationService();
        service.NormalizeCurrentSchema(config);

        Assert.Equal(0, custom.CurrentVersion);
        Assert.Equal(1, Assert.Single(custom.VersionHistory).Version);
    }

    [Fact]
    public void RequiresMigration_RejectsFutureSchemaInsteadOfDowngradingIt()
    {
        var service = new ConfigurationMigrationService();

        Assert.True(service.RequiresMigration(JObject.Parse("{\"schemaVersion\":1}")));
        Assert.True(service.RequiresMigration(JObject.Parse("{\"schemaVersion\":2}")));
        Assert.False(service.RequiresMigration(JObject.Parse("{\"schemaVersion\":3}")));
        var exception = Assert.Throws<UnsupportedConfigurationSchemaException>(
            () => service.RequiresMigration(JObject.Parse("{\"schemaVersion\":4}")));
        Assert.Equal(4, exception.FoundVersion);
        Assert.Equal(Configuration.CurrentSchemaVersion, exception.SupportedVersion);
    }

    [Fact]
    public void Migrate_AbsorbsExactlyUntouchedLegacyImportedAssetBeforeNormalization()
    {
        var raw = CreateLegacyImportConfiguration();
        var config = raw.ToObject<Configuration>()!;

        new ConfigurationMigrationService().Migrate(
            raw,
            config,
            removeLegacyJunctionAsset: true);

        var disabled = Assert.Single(
            config.Assets,
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
        Assert.Equal(["100", "200"], disabled.Addons);
        Assert.DoesNotContain(config.Assets, asset => asset.Id == "legacy-import");
    }

    [Theory]
    [InlineData("image")]
    [InlineData("favorite")]
    [InlineData("collection")]
    [InlineData("addonStates")]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("state")]
    public void Migrate_DoesNotAbsorbModifiedLegacyNamedAsset(string modification)
    {
        var raw = CreateLegacyImportConfiguration();
        var legacy = (JObject)((JArray)raw["assets"]!)[0]!;
        switch (modification)
        {
            case "image":
                legacy["imagePath"] = "asset-images/custom.png";
                break;
            case "favorite":
                legacy["isFavorite"] = true;
                break;
            case "collection":
                legacy["workshopCollectionId"] = "123";
                break;
            case "addonStates":
                legacy["addonStates"] = new JObject { ["100"] = 2 };
                break;
            case "version":
                legacy["currentVersion"] = 1;
                legacy["versionHistory"] = new JArray
                {
                    new JObject
                    {
                        ["version"] = 1,
                        ["createdAt"] = "2026-01-01T00:00:00Z",
                        ["addonIds"] = new JArray("100")
                    }
                };
                break;
            case "name":
                legacy["name"] = "My GMod Disabled List";
                break;
            case "state":
                legacy["state"] = 0;
                break;
        }
        var config = raw.ToObject<Configuration>()!;

        new ConfigurationMigrationService().Migrate(
            raw,
            config,
            removeLegacyJunctionAsset: true);

        Assert.Contains(config.Assets, asset => asset.Id == "legacy-import");
        Assert.Empty(config.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    [Fact]
    public void NormalizeCurrentSchema_DoesNotAbsorbLaterSameNameCustomAsset()
    {
        var config = new Configuration
        {
            InitialRuntimeImportCompleted = true
        };
        config.CreateDefaultAssets();
        config.Assets.Add(new Asset(
            GmodDisabledAddonReconciliationService.LegacyImportedAssetName)
        {
            Id = "user-created-later",
            Addons = ["100"],
            State = AddonState.Excluded
        });

        new ConfigurationMigrationService().NormalizeCurrentSchema(config);

        Assert.Contains(config.Assets, asset => asset.Id == "user-created-later");
        Assert.Empty(config.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    private static JObject CreateLegacyImportConfiguration()
    {
        return JObject.Parse(
            $$"""
            {
              "schemaVersion": 2,
              "initialRuntimeImportCompleted": true,
              "assets": [
                {
                  "id": "legacy-import",
                  "name": "{{GmodDisabledAddonReconciliationService.LegacyImportedAssetName}}",
                  "isSystem": false,
                  "state": 2,
                  "addons": ["100", "200"],
                  "addonStates": {},
                  "currentVersion": 0,
                  "versionHistory": []
                }
              ]
            }
            """);
    }
}
