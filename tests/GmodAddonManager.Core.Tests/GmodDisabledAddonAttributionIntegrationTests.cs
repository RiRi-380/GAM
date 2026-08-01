using System.Reflection;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Tests;

public sealed class GmodDisabledAddonAttributionIntegrationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-gmod-attribution-tests-" + Guid.NewGuid().ToString("N"));

    private string WorkshopPath => Path.Combine(rootPath, "workshop");
    private string AppDataPath => Path.Combine(rootPath, "appdata");
    private string GmodPath => Path.Combine(rootPath, "GarrysMod");
    private string NoMountPath => Path.Combine(
        GmodPath,
        "garrysmod",
        "cfg",
        "addonnomount.txt");

    [Fact]
    public async Task PassiveConflictRefresh_KeepsJournalUntilPendingMarkerIsDurablyCleared()
    {
        using var manager = await CreateManagerAsync("100", "200");
        var pending = new PendingChangeManager(manager, AppDataPath);
        await InstallPendingIntentAsync(manager, conflictDetected: false);
        pending.QueueApplyStates();
        WriteNoMount("100");

        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();

        Assert.True(pending.HasPendingChanges());
        Assert.True(manager.GetConfiguration().PendingGamRuntimeWrite?.ConflictDetected);
        Assert.True(ReadPersistedPendingJournal()?["conflictDetected"]?.Value<bool>());

        await pending.ApplyPendingChangesAsync();

        Assert.False(pending.HasPendingChanges());
        Assert.Null(manager.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        var runtime = File.ReadAllText(NoMountPath);
        Assert.Contains("\"100\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("\"200\"", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictMarkerClearFailure_KeepsMarkerAndDurableConflictLatch()
    {
        using var manager = await CreateManagerAsync("100", "200");
        var pending = new PendingChangeManager(manager, AppDataPath);
        await InstallPendingIntentAsync(manager, conflictDetected: false);
        pending.QueueApplyStates();
        WriteNoMount("100");
        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();

        var pendingBackup = Path.Combine(AppDataPath, "pending.json.bak");
        using (File.Open(
                   pendingBackup,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            await pending.ApplyPendingChangesAsync();
        }

        Assert.True(pending.HasPendingChanges());
        Assert.True(manager.GetConfiguration().PendingGamRuntimeWrite?.ConflictDetected);
        Assert.True(ReadPersistedPendingJournal()?["conflictDetected"]?.Value<bool>());
        Assert.Contains("\"100\"", File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartWithDurablyAbsentMarker_FinalizesOrphanedConflictWithoutRuntimeWrite()
    {
        using (var first = await CreateManagerAsync("100", "200"))
        {
            await InstallPendingIntentAsync(first, conflictDetected: true);
            WriteNoMount("100");
        }

        using var restarted = await CreateManagerAsync("100", "200");
        Assert.True(restarted.GetConfiguration().PendingGamRuntimeWrite?.ConflictDetected);

        _ = new PendingChangeManager(restarted, AppDataPath);

        Assert.Null(restarted.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        var runtime = File.ReadAllText(NoMountPath);
        Assert.Contains("\"100\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("\"200\"", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitStoppedApply_WithEmptyTargetScopeFinalizesExactConflict()
    {
        using var manager = await CreateManagerAsync();
        var service = new GmodDisabledAddonReconciliationService();
        var intent = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            NoMountPath);
        intent.ConflictDetected = true;
        manager.GetConfiguration().PendingGamRuntimeWrite = intent;
        await manager.SaveConfigurationImmediatelyAsync();

        Assert.True(await manager.UpdateAddonStatesAsync());

        Assert.Null(manager.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        Assert.DoesNotContain("\"100\"", File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRunningApply_PersistsMarkerBeforeClearingConflictLatch()
    {
        using var manager = await CreateManagerAsync("100", "200");
        var pending = new PendingChangeManager(manager, AppDataPath);
        await InstallPendingIntentAsync(manager, conflictDetected: true);
        manager.GmodRunningProvider = () => true;

        Assert.False(await manager.UpdateAddonStatesAsync());

        Assert.True(pending.HasPendingChanges());
        Assert.Null(manager.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        Assert.Contains(
            "\"action\": \"apply_states\"",
            File.ReadAllText(Path.Combine(AppDataPath, "pending.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRunningApply_WhenMarkerSaveFailsDoesNotClearConflictLatch()
    {
        using var manager = await CreateManagerAsync("100", "200");
        var pending = new PendingChangeManager(manager, AppDataPath);
        pending.QueueApplyStates();
        await InstallPendingIntentAsync(manager, conflictDetected: true);
        manager.GmodRunningProvider = () => true;
        var pendingBackup = Path.Combine(AppDataPath, "pending.json.bak");

        using (File.Open(
                   pendingBackup,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(
                () => manager.UpdateAddonStatesAsync());
        }

        Assert.True(pending.HasPendingChanges());
        Assert.True(manager.GetConfiguration().PendingGamRuntimeWrite?.ConflictDetected);
        Assert.True(ReadPersistedPendingJournal()?["conflictDetected"]?.Value<bool>());
    }

    [Fact]
    public async Task StartupNotAppliedRecovery_QueuesMarkerBeforeClearingJournal()
    {
        using (var first = await CreateManagerAsync("100"))
        {
            AddExcludedCustomAsset(first, "100");
            await InstallSinglePendingIntentAsync(first, conflictDetected: false);
        }

        using var restarted = await CreateManagerAsync("100");
        Assert.NotNull(restarted.GetConfiguration().PendingGamRuntimeWrite);

        var pending = new PendingChangeManager(restarted, AppDataPath);

        Assert.True(pending.HasPendingChanges());
        Assert.Null(restarted.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        Assert.Contains(
            "\"action\": \"apply_states\"",
            File.ReadAllText(Path.Combine(AppDataPath, "pending.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupNotAppliedRecovery_WhenMarkerSaveFailsKeepsDurableJournal()
    {
        using (var first = await CreateManagerAsync("100"))
        {
            AddExcludedCustomAsset(first, "100");
            await InstallSinglePendingIntentAsync(first, conflictDetected: false);
        }

        using var restarted = await CreateManagerAsync("100");
        var pendingBackup = Path.Combine(AppDataPath, "pending.json.bak");
        File.WriteAllText(pendingBackup, "{\"changes\":[]}");

        using (File.Open(
                   pendingBackup,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            _ = new PendingChangeManager(restarted, AppDataPath);
        }

        Assert.NotNull(restarted.GetConfiguration().PendingGamRuntimeWrite);
        Assert.NotNull(ReadPersistedPendingJournal());
    }

    [Fact]
    public async Task ExistingMarkerNotAppliedRecovery_AppliesOnceAndClearsMarkerAndJournal()
    {
        using var manager = await CreateManagerAsync("100");
        AddExcludedCustomAsset(manager, "100");
        await InstallSinglePendingIntentAsync(manager, conflictDetected: false);
        var pending = new PendingChangeManager(manager, AppDataPath);
        pending.QueueApplyStates();

        await pending.ApplyPendingChangesAsync();

        Assert.False(pending.HasPendingChanges());
        Assert.Null(manager.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        Assert.Contains("\"100\"", File.ReadAllText(NoMountPath), StringComparison.Ordinal);
        Assert.Empty(manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
    }

    [Fact]
    public async Task RestartCompletedRecovery_ClearsJournalWithoutImportingGamDisable()
    {
        using (var first = await CreateManagerAsync("100"))
        {
            AddExcludedCustomAsset(first, "100");
            await InstallSinglePendingIntentAsync(first, conflictDetected: false);
            WriteNoMount("100");
        }

        using var restarted = await CreateManagerAsync("100");

        Assert.Null(restarted.GetConfiguration().PendingGamRuntimeWrite);
        Assert.Null(ReadPersistedPendingJournal());
        Assert.Empty(restarted.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        Assert.False(restarted.GetConfiguration().LastGamAppliedAddonStates["100"]);
    }

    [Fact]
    public async Task ProtectedAssetIdBlocksMutationsEvenWhenIsSystemFlagIsCorrupted()
    {
        using var manager = await CreateManagerAsync("100");
        var disabled = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
        disabled.IsSystem = false;
        disabled.Addons = ["100"];
        disabled.VersionHistory.Add(new AssetVersion(1, ["100"]));
        disabled.CurrentVersion = 1;
        var undoCount = manager.GetUndoManager().GetHistory(100).Count;

        Assert.Throws<InvalidOperationException>(
            () => manager.RenameAsset(disabled.Id, "Renamed"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetAssetFavoriteAsync(disabled.Id, true));
        Assert.Throws<InvalidOperationException>(
            () => manager.SetAssetImage(disabled.Id, [1]));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ApplyAssetEditAsync(
                disabled.Id,
                "Renamed",
                sourceImagePath: null,
                crop: null,
                removeImage: true));
        Assert.Throws<InvalidOperationException>(
            () => manager.RemoveAssetImage(disabled.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CreateAssetVersionAsync(disabled.Id));

        manager.DeleteAsset(disabled.Id);
        Assert.False(await manager.DeleteAssetAsync(disabled.Id));
        Assert.False(await manager.RestoreAssetVersionAsync(disabled.Id, 1));
        Assert.False(await manager.DeleteAssetVersionAsync(disabled.Id, 1));
        Assert.Equal(0, await manager.ClearAssetVersionHistoryAsync(disabled.Id));
        manager.AddAddonToAsset(disabled.Id, "200");
        manager.AddAddonsToAssetBatch(disabled.Id, ["200"]);
        manager.RemoveAddonFromAsset(disabled.Id, "100");
        manager.RemoveAddonsFromAssetBatch(disabled.Id, ["100"]);
        manager.SetAddonState(disabled.Id, "100", AddonState.Enabled);
        manager.SetAddonStatesBatch(disabled.Id, ["100"], AddonState.Enabled);
        await manager.EnableAssetAsync(disabled.Id);
        await manager.DisableAssetAsync(disabled.Id);
        await manager.SetAssetEnabledAsync(disabled.Id, enabled: false);
        await manager.ApplyAssetDefaultStateAsync(disabled.Id, AddonState.Enabled);
        var exclusive = await manager.ApplyAssetExclusiveAsync(disabled.Id);

        Assert.False(exclusive.Success);
        Assert.Contains(disabled, manager.GetConfiguration().Assets);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledName, disabled.Name);
        Assert.Equal(AddonState.Excluded, disabled.GetWholeState());
        Assert.Equal(["100"], disabled.Addons);
        Assert.False(disabled.IsFavorite);
        Assert.Single(disabled.VersionHistory);
        Assert.Equal(undoCount, manager.GetUndoManager().GetHistory(100).Count);
    }

    [Fact]
    public async Task DirectAddonToggle_DoesNotInheritAnotherAsyncFlowBulkScope()
    {
        using var manager = await CreateManagerAsync("100");
        var field = typeof(AddonManager).GetField(
            "bulkStateUpdateDepth",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var bulkDepth = Assert.IsType<AsyncLocal<int>>(field?.GetValue(manager));
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var simulatedBulk = Task.Run(async () =>
        {
            bulkDepth.Value = 1;
            entered.SetResult(true);
            await release.Task;
            bulkDepth.Value = 0;
        });
        await entered.Task;

        await Task.Run(() => manager.DisableAddon("100"));
        release.SetResult(true);
        await simulatedBulk;

        Assert.Contains(
            "\"100\"",
            File.ReadAllText(NoMountPath),
            StringComparison.Ordinal);
        Assert.False(manager.GetConfiguration().LastGamAppliedAddonStates["100"]);
    }

    [Fact]
    public async Task DirectAddonToggle_WhenJournalSaveFailsDoesNotChangeRuntimeOrAdvanceBaselines()
    {
        using var manager = await CreateManagerAsync("100");
        var gamBaselineBefore = manager.GetConfiguration().LastGamAppliedAddonStates
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var observedBaselineBefore = manager.GetConfiguration().LastObservedGmodAddonStates
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var gamTimestampBefore = manager.GetConfiguration().LastGamAppliedRuntimeAtUtc;
        var observedTimestampBefore = manager.GetConfiguration().LastObservedGmodRuntimeAtUtc;
        var configPath = Path.Combine(AppDataPath, "config.json");
        File.Delete(configPath);
        Directory.CreateDirectory(configPath);

        try
        {
            Assert.Throws<InvalidOperationException>(
                () => manager.DisableAddon("100"));

            Assert.DoesNotContain(
                "\"100\"",
                File.ReadAllText(NoMountPath),
                StringComparison.Ordinal);
            Assert.Null(manager.GetConfiguration().PendingGamRuntimeWrite);
            Assert.Equal(
                gamBaselineBefore,
                manager.GetConfiguration().LastGamAppliedAddonStates
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal));
            Assert.Equal(
                observedBaselineBefore,
                manager.GetConfiguration().LastObservedGmodAddonStates
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal));
            Assert.Equal(
                gamTimestampBefore,
                manager.GetConfiguration().LastGamAppliedRuntimeAtUtc);
            Assert.Equal(
                observedTimestampBefore,
                manager.GetConfiguration().LastObservedGmodRuntimeAtUtc);
        }
        finally
        {
            Directory.Delete(configPath);
            await manager.SaveConfigurationImmediatelyAsync();
        }
    }

    [Fact]
    public async Task MalformedRuntimeObservationLeavesMembershipAckAndJournalUntouchedUntilRepair()
    {
        using var manager = await CreateManagerAsync("100");
        WriteNoMount("100");
        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();
        var disabled = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
        Assert.Equal(["100"], disabled.Addons);
        Assert.False(manager.GetConfiguration().LastObservedGmodAddonStates["100"]);
        var service = new GmodDisabledAddonReconciliationService();
        var pendingIntent = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = false },
            DateTime.UtcNow,
            @"D:\OtherLibrary\garrysmod\cfg\addonnomount.txt");
        manager.GetConfiguration().PendingGamRuntimeWrite = pendingIntent;
        await manager.SaveConfigurationImmediatelyAsync();
        const string malformed = "not a valid addonnomount document";
        File.WriteAllText(NoMountPath, malformed);

        Assert.False(await manager.RefreshGmodDisabledAddonsFromRuntimeAsync());

        Assert.Equal(malformed, File.ReadAllText(NoMountPath));
        Assert.Equal(["100"], disabled.Addons);
        Assert.False(manager.GetConfiguration().LastObservedGmodAddonStates["100"]);
        Assert.Same(pendingIntent, manager.GetConfiguration().PendingGamRuntimeWrite);
        Assert.False(pendingIntent.ConflictDetected);

        WriteNoMount();
        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();

        Assert.Empty(disabled.Addons);
        Assert.True(manager.GetConfiguration().LastObservedGmodAddonStates["100"]);
        Assert.True(manager.GetConfiguration().PendingGamRuntimeWrite?.ConflictDetected);
    }

    [Fact]
    public async Task ManagerEndToEnd_TracksOnlyExternalTransitionsAndIgnoresGamAuthoredDisable()
    {
        using var manager = await CreateManagerAsync("100");
        var disabled = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);

        WriteNoMount("100");
        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();
        Assert.Equal(["100"], disabled.Addons);

        WriteNoMount();
        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();
        Assert.Empty(disabled.Addons);

        AddExcludedCustomAsset(manager, "100");
        Assert.True(await manager.UpdateAddonStatesAsync());
        Assert.Contains("\"100\"", File.ReadAllText(NoMountPath), StringComparison.Ordinal);

        await manager.RefreshGmodDisabledAddonsFromRuntimeAsync();

        Assert.Empty(disabled.Addons);
        Assert.False(manager.GetConfiguration().LastGamAppliedAddonStates["100"]);
        Assert.False(manager.GetConfiguration().LastObservedGmodAddonStates["100"]);
    }

    [Fact]
    public async Task SchemaV2Startup_ImportsOnlyActualOffStatesPreviouslyDesiredOn()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        File.WriteAllText(
            Path.Combine(AppDataPath, "config.json"),
            """
            {
              "schemaVersion": 2,
              "version": "2.0",
              "initialRuntimeImportCompleted": true,
              "assets": [
                {
                  "id": "subscribe-system-asset",
                  "name": "Subscribe Asset",
                  "isSystem": true,
                  "state": 0,
                  "addons": ["*"]
                },
                {
                  "id": "legacy-excluded",
                  "name": "Legacy Excluded",
                  "isSystem": false,
                  "state": 2,
                  "addons": ["200"]
                }
              ]
            }
            """);
        WriteNoMount("100", "200");

        using var manager = await CreateManagerAsync("100", "200");

        Assert.Equal(Configuration.CurrentSchemaVersion, manager.GetConfiguration().SchemaVersion);
        Assert.Equal(
            ["100"],
            manager.GetConfiguration().Assets.Single(
                asset => asset.Id == SystemAssetDefinitions.GmodDisabledId).Addons);
        Assert.False(manager.GetConfiguration().GmodAttributionMigrationPending);
    }

    private async Task<AddonManager> CreateManagerAsync(params string[] addonIds)
    {
        Directory.CreateDirectory(WorkshopPath);
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        if (!File.Exists(NoMountPath))
        {
            WriteNoMount();
        }
        var manifestPath = WorkshopManifestTestData.Write(rootPath, addonIds);
        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = WorkshopPath,
            CustomGmodInstallPath = GmodPath,
            CustomAppDataPath = AppDataPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
        await manager.InitializeAsync();
        return manager;
    }

    private async Task InstallPendingIntentAsync(
        AddonManager manager,
        bool conflictDetected)
    {
        var service = new GmodDisabledAddonReconciliationService();
        var intent = service.CreatePendingWrite(
            new Dictionary<string, bool>
            {
                ["100"] = false,
                ["200"] = false
            },
            new Dictionary<string, bool>
            {
                ["100"] = true,
                ["200"] = true
            },
            DateTime.UtcNow,
            NoMountPath);
        intent.ConflictDetected = conflictDetected;
        manager.GetConfiguration().PendingGamRuntimeWrite = intent;
        await manager.SaveConfigurationImmediatelyAsync();
    }

    private async Task InstallSinglePendingIntentAsync(
        AddonManager manager,
        bool conflictDetected)
    {
        var service = new GmodDisabledAddonReconciliationService();
        var intent = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            NoMountPath);
        intent.ConflictDetected = conflictDetected;
        manager.GetConfiguration().PendingGamRuntimeWrite = intent;
        await manager.SaveConfigurationImmediatelyAsync();
    }

    private static void AddExcludedCustomAsset(
        AddonManager manager,
        string addonId)
    {
        var asset = new Asset("Explicit exclusion")
        {
            Addons = [addonId]
        };
        asset.SetWholeState(AddonState.Excluded);
        manager.GetConfiguration().Assets.Add(asset);
    }

    private JObject? ReadPersistedPendingJournal()
    {
        var configPath = Path.Combine(AppDataPath, "config.json");
        return JObject.Parse(File.ReadAllText(configPath))["pendingGamRuntimeWrite"]
            as JObject;
    }

    private void WriteNoMount(params string[] disabledIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NoMountPath)!);
        var lines = new List<string> { "\"addonnomount\"", "{" };
        for (var index = 0; index < disabledIds.Length; index++)
        {
            lines.Add($"\t\"{index + 1}\"\t\t\"{disabledIds[index]}\"");
        }
        lines.Add("}");
        File.WriteAllText(NoMountPath, string.Join(Environment.NewLine, lines));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
