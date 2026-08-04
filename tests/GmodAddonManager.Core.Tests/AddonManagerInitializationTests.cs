using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerInitializationTests : IDisposable
{
    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string noMountPath;
    private readonly string manifestPath;

    public AddonManagerInitializationTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "gam-initialization-tests-" + Guid.NewGuid().ToString("N"));
        workshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
        appDataPath = Path.Combine(rootPath, "appdata");
        gmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
        noMountPath = Path.Combine(gmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
        manifestPath = Path.Combine(rootPath, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(noMountPath)!);
    }

    [Fact]
    public async Task Initialize_NewProfileImportsSubscribedDisabledIdsWithoutWritingRuntimeFile()
    {
        WriteManifest(("100", true), ("200", true));
        var originalNoMount = BuildNoMount("100", "999");
        File.WriteAllText(noMountPath, originalNoMount, new UTF8Encoding(false));

        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();

            var imported = Assert.Single(
                manager.GetConfiguration().Assets,
                asset => asset.Name == InitialAddonStateImportService.ImportedAssetName);
            Assert.Equal(SystemAssetDefinitions.GmodDisabledDefaultState, imported.GetWholeState());
            Assert.Equal(["100"], imported.Addons);
            Assert.True(manager.GetConfiguration().InitialRuntimeImportCompleted);

            var persisted = JsonConvert.DeserializeObject<Configuration>(
                File.ReadAllText(Path.Combine(manager.GetManagerPath(), "config.json")));
            Assert.NotNull(persisted);
            Assert.True(persisted.InitialRuntimeImportCompleted);
            Assert.Single(
                persisted.Assets,
                asset => asset.Name == InitialAddonStateImportService.ImportedAssetName);
        }

        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));

        using var secondManager = CreateManager();
        await secondManager.InitializeAsync();
        Assert.Single(
            secondManager.GetConfiguration().Assets,
            asset => asset.Name == InitialAddonStateImportService.ImportedAssetName);
        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));
    }

    [Fact]
    public async Task Initialize_MalformedNoMountDoesNotMarkInitialImportComplete()
    {
        WriteManifest(("100", true));
        const string malformed = "\"addonnomount\"\n\"1\" \"100\"\n";
        File.WriteAllText(noMountPath, malformed);

        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();

            Assert.False(manager.GetConfiguration().InitialRuntimeImportCompleted);
            var disabled = Assert.Single(
                manager.GetConfiguration().Assets,
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
            Assert.Empty(disabled.Addons);
        }

        Assert.Equal(malformed, File.ReadAllText(noMountPath));

        File.WriteAllText(noMountPath, BuildNoMount("100"));
        using var retryManager = CreateManager();
        await retryManager.InitializeAsync();
        Assert.True(retryManager.GetConfiguration().InitialRuntimeImportCompleted);
        Assert.Single(
            retryManager.GetConfiguration().Assets,
            asset => asset.Name == InitialAddonStateImportService.ImportedAssetName);
    }

    [Fact]
    public async Task Initialize_LegacyConfigurationCreatesRawBackupAndDoesNotWriteRuntimeState()
    {
        WriteManifest(("100", true), ("200", true));
        var originalNoMount = BuildNoMount("999");
        File.WriteAllText(noMountPath, originalNoMount);
        var legacyJson =
            """
            {
              "version": "1.0",
              "assets": [
                {
                  "id": "mixed",
                  "name": "Mixed",
                  "isSystem": false,
                  "enabled": true,
                  "addons": ["100", "200"],
                  "addonStates": { "100": 0, "200": 2 },
                  "defaultAddonState": 0
                }
              ],
              "addonMetadata": {},
              "junctionHistory": {}
            }
            """;
        var configPath = Path.Combine(appDataPath, "config.json");
        File.WriteAllText(configPath, legacyJson, new UTF8Encoding(false));

        using var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Equal(
            legacyJson,
            File.ReadAllText(
                configPath + $".pre-schema-{Configuration.CurrentSchemaVersion}.bak",
                Encoding.UTF8));
        var mixed = Assert.Single(manager.GetConfiguration().Assets, asset => asset.Id == "mixed");
        Assert.Equal(AddonState.Disabled, mixed.GetWholeState());
        Assert.True(mixed.NeedsMigrationReview);
        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath));
    }

    [Fact]
    public async Task LoadConfiguration_Schema3To4IsLosslessBackedUpAndIdempotent()
    {
        var firstSeenAt = new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc);
        var gamAppliedAt = new DateTime(2026, 7, 31, 2, 3, 4, DateTimeKind.Utc);
        var observedAt = new DateTime(2026, 7, 31, 3, 4, 5, DateTimeKind.Utc);
        var pendingAt = new DateTime(2026, 7, 31, 4, 5, 6, DateTimeKind.Utc);
        var originalNoMount = BuildNoMount("100", "999");
        File.WriteAllText(noMountPath, originalNoMount, new UTF8Encoding(false));

        var schema3 = new Configuration
        {
            SchemaVersion = 3,
            InitialRuntimeImportCompleted = true,
            InitialRuntimeImportCompletedAtUtc = firstSeenAt,
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100", "200"],
            SubscriptionFirstSeenAtUtc = new Dictionary<string, DateTime>
            {
                ["100"] = firstSeenAt
            },
            RetainMissingAssetReferences = true,
            GamAppliedRuntimeBaselineInitialized = true,
            LastGamAppliedAddonStates = new Dictionary<string, bool>
            {
                ["100"] = false,
                ["200"] = true
            },
            LastGamAppliedRuntimeAtUtc = gamAppliedAt,
            LastGamAppliedStateStorePath = noMountPath,
            GmodObservationBaselineInitialized = true,
            LastObservedGmodAddonStates = new Dictionary<string, bool>
            {
                ["100"] = false,
                ["200"] = true
            },
            LastObservedGmodRuntimeAtUtc = observedAt,
            LastObservedGmodStateStorePath = noMountPath,
            PendingGamRuntimeWrite = new PendingGamRuntimeWrite
            {
                OperationId = "pending-schema-3",
                TargetStates = new Dictionary<string, bool> { ["100"] = false },
                PreviousStates = new Dictionary<string, bool> { ["100"] = true },
                CreatedAtUtc = pendingAt,
                StateStorePath = noMountPath,
                ConflictDetected = true
            },
            GmodAttributionMigrationPending = true,
            PathState = new PathState
            {
                LastManagerPath = @"C:\Manager",
                LastAddonsPath = @"C:\Workshop"
            }
        };
        schema3.CreateDefaultAssets();
        schema3.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId)
            .SetWholeState(AddonState.Disabled);
        schema3.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId)
            .Addons = ["100"];
        schema3.Assets.Add(new Asset("FPS")
        {
            Id = "fps",
            State = AddonState.Enabled,
            Addons = ["200"]
        });

        var schema3Json = JsonConvert.SerializeObject(schema3, Formatting.Indented);
        var configPath = Path.Combine(appDataPath, "config.json");
        var migrationBackupPath =
            configPath + $".pre-schema-{Configuration.CurrentSchemaVersion}.bak";
        File.WriteAllText(configPath, schema3Json, new UTF8Encoding(false));

        string migratedJson;
        using (var manager = CreateManager())
        {
            await manager.LoadConfigurationAsync();

            Assert.Equal(schema3Json, File.ReadAllText(migrationBackupPath, Encoding.UTF8));
            AssertSchema4AttributionState(manager.GetConfiguration());
            migratedJson = File.ReadAllText(configPath, Encoding.UTF8);
        }

        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));

        using (var restarted = CreateManager())
        {
            await restarted.LoadConfigurationAsync();

            AssertSchema4AttributionState(restarted.GetConfiguration());
            Assert.Equal(migratedJson, File.ReadAllText(configPath, Encoding.UTF8));
            Assert.Equal(schema3Json, File.ReadAllText(migrationBackupPath, Encoding.UTF8));
        }

        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));

        void AssertSchema4AttributionState(Configuration configuration)
        {
            Assert.Equal(Configuration.CurrentSchemaVersion, configuration.SchemaVersion);
            Assert.True(configuration.InitialRuntimeImportCompleted);
            Assert.Equal(firstSeenAt, configuration.InitialRuntimeImportCompletedAtUtc);
            Assert.True(configuration.SubscriptionBaselineInitialized);
            Assert.Equal(["100", "200"], configuration.KnownSubscribedAddonIds);
            Assert.Equal(firstSeenAt, configuration.SubscriptionFirstSeenAtUtc["100"]);
            Assert.True(configuration.RetainMissingAssetReferences);
            Assert.True(configuration.GamAppliedRuntimeBaselineInitialized);
            Assert.False(configuration.LastGamAppliedAddonStates["100"]);
            Assert.True(configuration.LastGamAppliedAddonStates["200"]);
            Assert.Equal(gamAppliedAt, configuration.LastGamAppliedRuntimeAtUtc);
            Assert.Equal(noMountPath, configuration.LastGamAppliedStateStorePath);
            Assert.True(configuration.GmodObservationBaselineInitialized);
            Assert.False(configuration.LastObservedGmodAddonStates["100"]);
            Assert.True(configuration.LastObservedGmodAddonStates["200"]);
            Assert.Equal(observedAt, configuration.LastObservedGmodRuntimeAtUtc);
            Assert.Equal(noMountPath, configuration.LastObservedGmodStateStorePath);
            var pending = Assert.IsType<PendingGamRuntimeWrite>(
                configuration.PendingGamRuntimeWrite);
            Assert.Equal("pending-schema-3", pending.OperationId);
            Assert.False(pending.TargetStates["100"]);
            Assert.True(pending.PreviousStates["100"]);
            Assert.Equal(pendingAt, pending.CreatedAtUtc);
            Assert.Equal(noMountPath, pending.StateStorePath);
            Assert.True(pending.ConflictDetected);
            Assert.True(configuration.GmodAttributionMigrationPending);
            Assert.Equal(@"C:\Manager", configuration.PathState.LastManagerPath);
            Assert.Equal(@"C:\Workshop", configuration.PathState.LastAddonsPath);
            Assert.Equal(
                AddonState.Disabled,
                configuration.Assets.Single(
                    asset => asset.Id == SystemAssetDefinitions.SubscribeId).GetWholeState());
            Assert.Equal(
                ["100"],
                configuration.Assets.Single(
                    asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
            Assert.Equal(
                AddonState.Enabled,
                configuration.Assets.Single(asset => asset.Id == "fps").GetWholeState());
        }
    }

    [Fact]
    public async Task Initialize_ExistingCurrentSchemaWithoutImportMarkerDoesNotRunFirstImport()
    {
        WriteManifest(("100", true));
        var originalNoMount = BuildNoMount("100");
        File.WriteAllText(noMountPath, originalNoMount, new UTF8Encoding(false));

        var existingConfiguration = new Configuration();
        existingConfiguration.CreateDefaultAssets();
        existingConfiguration.Assets.Add(new Asset("Existing custom asset")
        {
            Id = "existing-custom",
            State = AddonState.Enabled,
            Addons = ["100"]
        });

        var rawConfiguration = JObject.FromObject(existingConfiguration);
        rawConfiguration.Remove("initialRuntimeImportCompleted");
        rawConfiguration.Remove("initialRuntimeImportCompletedAtUtc");
        File.WriteAllText(
            Path.Combine(appDataPath, "config.json"),
            rawConfiguration.ToString(Formatting.Indented),
            new UTF8Encoding(false));

        using var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.True(manager.GetConfiguration().InitialRuntimeImportCompleted);
        Assert.Empty(manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));

        var persisted = JObject.Parse(
            File.ReadAllText(Path.Combine(appDataPath, "config.json"), Encoding.UTF8));
        Assert.True(persisted.Value<bool>("initialRuntimeImportCompleted"));
        Assert.NotNull(persisted.Value<DateTime?>("initialRuntimeImportCompletedAtUtc"));
    }

    [Fact]
    public async Task LoadConfiguration_CurrentSchemaPersistsNormalizedGroupTopology()
    {
        var existingConfiguration = new Configuration
        {
            InitialRuntimeImportCompleted = true,
            InitialRuntimeImportCompletedAtUtc = DateTime.UtcNow
        };
        existingConfiguration.CreateDefaultAssets();
        existingConfiguration.AssetGroups.Add(new AssetGroup("Orphaned group")
        {
            Id = "orphaned-group",
            ParentGroupId = "missing-parent"
        });

        var configPath = Path.Combine(appDataPath, "config.json");
        File.WriteAllText(
            configPath,
            JsonConvert.SerializeObject(existingConfiguration, Formatting.Indented),
            new UTF8Encoding(false));

        using var manager = CreateManager();
        await manager.LoadConfigurationAsync();

        Assert.Null(manager.GetConfiguration().AssetGroups.Single(
            group => group.Id == "orphaned-group").ParentGroupId);

        var persisted = JsonConvert.DeserializeObject<Configuration>(
            File.ReadAllText(configPath, Encoding.UTF8));
        Assert.NotNull(persisted);
        Assert.Null(persisted.AssetGroups.Single(
            group => group.Id == "orphaned-group").ParentGroupId);
    }

    [Fact]
    public async Task Initialize_FutureSchemaFailsClosedWithoutRewritingConfigurationOrRuntimeState()
    {
        WriteManifest(("100", true));
        var originalNoMount = BuildNoMount("100");
        File.WriteAllText(noMountPath, originalNoMount, new UTF8Encoding(false));
        var configPath = Path.Combine(appDataPath, "config.json");
        var futureSchemaVersion = Configuration.CurrentSchemaVersion + 1;
        var futureJson =
            $"{{\"schemaVersion\":{futureSchemaVersion},\"version\":\"{futureSchemaVersion}.0\",\"futureOnly\":{{\"preserve\":true}},\"assets\":[]}}";
        File.WriteAllText(configPath, futureJson, new UTF8Encoding(false));

        using var manager = CreateManager();
        await Assert.ThrowsAsync<UnsupportedConfigurationSchemaException>(manager.InitializeAsync);

        Assert.Equal(futureJson, File.ReadAllText(configPath, Encoding.UTF8));
        Assert.Equal(originalNoMount, File.ReadAllText(noMountPath, Encoding.UTF8));
        Assert.False(File.Exists(
            configPath + $".pre-schema-{Configuration.CurrentSchemaVersion}.bak"));
    }

    [Fact]
    public async Task Initialize_SubscribeExcludedSurvivesRestartWithoutGmodMisattribution()
    {
        WriteManifest(("100", true));

        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            var subscribe = manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.SubscribeId);

            await manager.ApplyAssetDefaultStateAsync(
                subscribe.Id,
                AddonState.Excluded);

            Assert.Equal(AddonState.Excluded, subscribe.GetWholeState());
            Assert.False(manager.GetFinalAddonStates()["100"]);
            Assert.Empty(manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        }

        using var restarted = CreateManager();
        await restarted.InitializeAsync();

        Assert.Equal(
            AddonState.Excluded,
            restarted.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.SubscribeId).GetWholeState());
        Assert.False(restarted.GetFinalAddonStates()["100"]);
        Assert.Empty(restarted.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        Assert.Equal(
            Configuration.CurrentSchemaVersion,
            restarted.GetConfiguration().SchemaVersion);
    }

    [Fact]
    public async Task LoadConfiguration_CorruptPrimaryRecoversFromBackupAndPreservesEvidence()
    {
        var configPath = Path.Combine(appDataPath, "config.json");
        var backupPath = configPath + ".bak";
        const string corruptPrimary = "{ this is not valid json";
        var backupConfiguration = new Configuration
        {
            InitialRuntimeImportCompleted = true,
            InitialRuntimeImportCompletedAtUtc = DateTime.UtcNow
        };
        backupConfiguration.CreateDefaultAssets();
        backupConfiguration.Assets.Add(new Asset("Recovered asset")
        {
            Id = "recovered",
            Addons = ["100"]
        });
        var backupJson = JsonConvert.SerializeObject(backupConfiguration, Formatting.Indented);
        File.WriteAllText(configPath, corruptPrimary, new UTF8Encoding(false));
        File.WriteAllText(backupPath, backupJson, new UTF8Encoding(false));

        using var manager = CreateManager();
        await manager.LoadConfigurationAsync();

        Assert.Contains(manager.GetConfiguration().Assets, asset => asset.Id == "recovered");
        Assert.Equal(backupJson, File.ReadAllText(configPath, Encoding.UTF8));
        Assert.Equal(backupJson, File.ReadAllText(backupPath, Encoding.UTF8));
        var corruptArchive = Assert.Single(
            Directory.GetFiles(appDataPath, "config.json.corrupt-*.bak"));
        Assert.Equal(corruptPrimary, File.ReadAllText(corruptArchive, Encoding.UTF8));
    }

    [Fact]
    public async Task LoadConfiguration_WhenPrimaryAndBackupAreInvalid_PreservesBothAndFails()
    {
        var configPath = Path.Combine(appDataPath, "config.json");
        var backupPath = configPath + ".bak";
        const string corruptPrimary = "{ primary";
        const string corruptBackup = "{ backup";
        File.WriteAllText(configPath, corruptPrimary, new UTF8Encoding(false));
        File.WriteAllText(backupPath, corruptBackup, new UTF8Encoding(false));

        using var manager = CreateManager();
        await Assert.ThrowsAsync<InvalidOperationException>(manager.LoadConfigurationAsync);

        Assert.Equal(corruptPrimary, File.ReadAllText(configPath, Encoding.UTF8));
        Assert.Equal(corruptBackup, File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(appDataPath, "config.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Initialize_MissingPrimaryRestoresBackupInsteadOfCreatingDefaults()
    {
        WriteManifest(("100", true));
        var configPath = Path.Combine(appDataPath, "config.json");
        var backupConfiguration = new Configuration
        {
            InitialRuntimeImportCompleted = true,
            InitialRuntimeImportCompletedAtUtc = DateTime.UtcNow,
            SubscriptionBaselineInitialized = true,
            KnownSubscribedAddonIds = ["100"]
        };
        backupConfiguration.CreateDefaultAssets();
        backupConfiguration.Assets.Add(new Asset("Backup-only asset")
        {
            Id = "backup-only",
            Addons = ["100"]
        });
        File.WriteAllText(
            configPath + ".bak",
            JsonConvert.SerializeObject(backupConfiguration, Formatting.Indented),
            new UTF8Encoding(false));

        using var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Contains(manager.GetConfiguration().Assets, asset => asset.Id == "backup-only");
        Assert.True(File.Exists(configPath));
    }

    private AddonManager CreateManager()
    {
        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
        return manager;
    }

    private void WriteManifest(params (string Id, bool Installed)[] addons)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"AppWorkshop\"");
        builder.AppendLine("{");
        builder.AppendLine("  \"WorkshopItemDetails\"");
        builder.AppendLine("  {");
        foreach (var addon in addons)
        {
            builder.Append("    \"").Append(addon.Id).AppendLine("\"");
            builder.AppendLine("    {");
            builder.AppendLine("      \"subscribedby\" \"76561198000000000\"");
            builder.AppendLine("    }");
        }
        builder.AppendLine("  }");
        builder.AppendLine("  \"WorkshopItemsInstalled\"");
        builder.AppendLine("  {");
        foreach (var addon in addons.Where(addon => addon.Installed))
        {
            builder.Append("    \"").Append(addon.Id).AppendLine("\"");
            builder.AppendLine("    {");
            builder.AppendLine("      \"size\" \"1\"");
            builder.AppendLine("    }");
        }
        builder.AppendLine("  }");
        builder.AppendLine("}");
        File.WriteAllText(manifestPath, builder.ToString());
    }

    private static string BuildNoMount(params string[] ids)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"addonnomount\"");
        builder.AppendLine("{");
        for (var index = 0; index < ids.Length; index++)
        {
            builder.Append("\t\"").Append(index + 1).Append("\"\t\t\"")
                .Append(ids[index]).AppendLine("\"");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    public void Dispose()
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }
}
