using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerWorkflowTests : IDisposable
{
    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string noMountPath;
    private readonly string workshopManifestPath;

    public AddonManagerWorkflowTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "gam-workflow-tests-" + Guid.NewGuid().ToString("N"));
        workshopPath = Path.Combine(rootPath, "workshop");
        appDataPath = Path.Combine(rootPath, "appdata");
        gmodRootPath = Path.Combine(rootPath, "GarrysMod");
        noMountPath = Path.Combine(gmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(Path.GetDirectoryName(noMountPath)!);
        workshopManifestPath = WorkshopManifestTestData.Write(rootPath, "100", "200");
        File.WriteAllText(noMountPath, "\"addonnomount\"\n{\n\t\"1\"\t\t\"999\"\n}\n");
    }

    [Fact]
    public async Task AllOff_DisablesSourcesPreservesExclusionsAndUndoesAsOneOperation()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        AddKnownAddon(manager, "200");
        var enabled = AddAsset(manager, "Enabled", AddonState.Enabled, "100");
        var excluded = AddAsset(manager, "Excluded", AddonState.Excluded, "200");

        await manager.SetAllOffAsync();

        var subscribe = manager.GetConfiguration().Assets.Single(a => a.Id == "subscribe-system-asset");
        Assert.Equal(AddonState.Disabled, subscribe.GetWholeState());
        Assert.Equal(AddonState.Disabled, enabled.GetWholeState());
        Assert.Equal(AddonState.Excluded, excluded.GetWholeState());
        Assert.False(manager.GetFinalAddonStates()["100"]);
        Assert.False(manager.GetFinalAddonStates()["200"]);
        Assert.Equal(UndoActionType.AllOff, manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(AddonState.Enabled, subscribe.GetWholeState());
        Assert.Equal(AddonState.Enabled, enabled.GetWholeState());
        Assert.Equal(AddonState.Excluded, excluded.GetWholeState());
        Assert.True(manager.GetFinalAddonStates()["100"]);
        Assert.False(manager.GetFinalAddonStates()["200"]);
    }

    [Fact]
    public async Task AllOff_FromSubscribeExcludedRemainsDistinctAndUndoRestoresTheVeto()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        var enabled = AddAsset(manager, "Enabled", AddonState.Enabled, "100");
        var subscribe = manager.GetConfiguration().Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.SetWholeState(AddonState.Excluded);
        manager.GetUndoManager().Clear();

        await manager.SetAllOffAsync();

        Assert.Equal(AddonState.Disabled, subscribe.GetWholeState());
        Assert.Equal(AddonState.Disabled, enabled.GetWholeState());
        Assert.Equal(
            UndoActionType.AllOff,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(AddonState.Excluded, subscribe.GetWholeState());
        Assert.Equal(AddonState.Enabled, enabled.GetWholeState());
        Assert.False(manager.GetFinalAddonStates()["100"]);
    }

    [Fact]
    public async Task DeleteAsset_RecomputesWithoutDeletingAddonAndUndoRestoresDefinition()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        var payloadPath = Path.Combine(workshopPath, "100", "lua", "autorun.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllText(payloadPath, "payload");
        var excluded = AddAsset(manager, "Excluded", AddonState.Excluded, "100");
        await manager.UpdateAddonStatesAsync();

        Assert.True(await manager.DeleteAssetAsync(excluded.Id));

        Assert.DoesNotContain(manager.GetConfiguration().Assets, asset => asset.Id == excluded.Id);
        Assert.True(File.Exists(payloadPath));
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey("100"));
        Assert.True(manager.GetFinalAddonStates()["100"]);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Contains(manager.GetConfiguration().Assets, asset => asset.Id == excluded.Id);
        Assert.False(manager.GetFinalAddonStates()["100"]);
        Assert.True(File.Exists(payloadPath));
    }

    [Fact]
    public async Task ResetManager_ClearsGamConfigurationButPreservesSubscriptionsPayloadAndRecoveryMetadata()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        manager.GetConfiguration().AddonMetadata["100"].IsFavorite = true;
        var payloadPath = Path.Combine(workshopPath, "100", "lua", "autorun.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllText(payloadPath, "payload");
        var asset = AddAsset(manager, "Excluded", AddonState.Excluded, "100");
        asset.IsFavorite = true;
        asset.VersionHistory.Add(new AssetVersion(3, ["100"]));
        manager.GetConfiguration().RetainMissingAssetReferences = true;
        manager.GetConfiguration().JunctionHistory["100"] =
            [asset.Id, "missing-source-asset"];
        await manager.UpdateAddonStatesAsync();

        await manager.ResetManagerAsync();

        Assert.Equal(2, manager.GetConfiguration().Assets.Count);
        var subscribe = manager.GetConfiguration().Assets[0];
        Assert.Equal("subscribe-system-asset", subscribe.Id);
        Assert.Equal(AddonState.Enabled, subscribe.GetWholeState());
        var gmodDisabled = manager.GetConfiguration().Assets[1];
        Assert.Equal(SystemAssetDefinitions.GmodDisabledId, gmodDisabled.Id);
        Assert.Equal(AddonState.Excluded, gmodDisabled.GetWholeState());
        Assert.Empty(gmodDisabled.Addons);
        Assert.False(manager.GetConfiguration().AddonMetadata["100"].IsFavorite);
        Assert.True(File.Exists(payloadPath));
        var noMount = File.ReadAllText(noMountPath);
        Assert.DoesNotContain("\"100\"", noMount, StringComparison.Ordinal);
        Assert.Contains("\"999\"", noMount, StringComparison.Ordinal);
        Assert.True(manager.GetConfiguration().RetainMissingAssetReferences);
        Assert.Equal(
            [asset.Id, "missing-source-asset"],
            manager.GetConfiguration().JunctionHistory["100"]);
        var persisted = JObject.Parse(
            File.ReadAllText(Path.Combine(manager.GetManagerPath(), "config.json")));
        Assert.Equal(
            [asset.Id, "missing-source-asset"],
            persisted["junctionHistory"]!["100"]!.Values<string>());
        Assert.False(manager.GetUndoManager().CanUndo);
    }

    [Fact]
    public async Task RestoreVersion_UndoRestoresMembershipAndPreviousVersionLabel()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        AddKnownAddon(manager, "200");
        var asset = AddAsset(manager, "Versioned", AddonState.Enabled, "100");
        asset.CurrentVersion = 2;
        asset.VersionHistory.Add(new AssetVersion(5, ["200"]));

        Assert.True(await manager.RestoreAssetVersionAsync(asset.Id, 5));
        Assert.Equal(["200"], asset.Addons);
        Assert.Equal(5, asset.CurrentVersion);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(["100"], asset.Addons);
        Assert.Equal(2, asset.CurrentVersion);
    }

    [Fact]
    public async Task Undo_SaveFailureReturnsFalseAndRetainsHistory()
    {
        using var manager = await CreateInitializedManager();
        manager.CreateAsset("Temporary");
        await manager.SaveConfigurationImmediatelyAsync();
        var actionId = manager.GetUndoManager().PeekLastAction()!.Id;
        var configPath = Path.Combine(manager.GetManagerPath(), "config.json");
        File.Delete(configPath);
        Directory.CreateDirectory(configPath);

        var undone = await manager.UndoLastActionAsync();

        Assert.False(undone);
        Assert.Contains(
            manager.GetConfiguration().Assets,
            asset => asset.Name == "Temporary");
        Assert.True(manager.GetUndoManager().CanUndo);
        Assert.Equal(actionId, manager.GetUndoManager().PeekLastAction()!.Id);

        Directory.Delete(configPath);
        await manager.SaveConfigurationImmediatelyAsync();
    }

    [Fact]
    public async Task UndoDeletedAsset_WhenSameDefinitionAlreadyExists_DoesNotDuplicateIt()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "Deleted", AddonState.Enabled, "100");

        Assert.True(await manager.DeleteAssetAsync(asset.Id));
        var actionId = manager.GetUndoManager().PeekLastAction()!.Id;
        manager.GetConfiguration().Assets.Add(new Asset("Already restored")
        {
            Id = asset.Id,
            Addons = ["100"]
        });

        Assert.False(await manager.UndoLastActionAsync());
        Assert.Single(
            manager.GetConfiguration().Assets,
            current => current.Id == asset.Id);
        Assert.Equal(actionId, manager.GetUndoManager().PeekLastAction()!.Id);
    }

    [Fact]
    public async Task CreateAssetWithSelectedAddons_IsOneUndoUnit()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        AddKnownAddon(manager, "200");
        var historyCountBefore = manager.GetUndoManager().GetHistory(50).Count;

        manager.CreateAsset("Selected");
        var created = manager.GetConfiguration().Assets.Single(asset => asset.Name == "Selected");
        manager.AddAddonsToNewAssetBatch(created.Id, ["100", "200"]);

        var newHistory = manager.GetUndoManager().GetHistory(50)
            .Take(manager.GetUndoManager().GetHistory(50).Count - historyCountBefore)
            .ToList();
        var creation = Assert.Single(newHistory);
        Assert.Equal(UndoActionType.AssetCreated, creation.Type);
        Assert.Equal(["100", "200"], created.Addons);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Id == created.Id);
    }

    [Fact]
    public async Task AddMembersToOlderNewAsset_DoesNotHideBehindNewerCreationUndo()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        manager.CreateAsset("First");
        var first = manager.GetConfiguration().Assets
            .Single(asset => asset.Name == "First");
        manager.CreateAsset("Second");
        var second = manager.GetConfiguration().Assets
            .Single(asset => asset.Name == "Second");

        manager.AddAddonsToNewAssetBatch(first.Id, ["100"]);

        Assert.Equal(
            UndoActionType.AddonAddedToAsset,
            manager.GetUndoManager().PeekLastAction()!.Type);
        Assert.True(await manager.UndoLastActionAsync());
        Assert.Empty(first.Addons);
        Assert.Contains(second, manager.GetConfiguration().Assets);
        Assert.Equal(
            second.Id,
            manager.GetUndoManager().PeekLastAction()!.AssetId);
    }

    [Fact]
    public async Task SubscribeAsset_CannotBeRenamed()
    {
        using var manager = await CreateInitializedManager();
        var subscribe = manager.GetConfiguration().Assets
            .Single(asset => asset.Id == "subscribe-system-asset");

        Assert.Throws<InvalidOperationException>(
            () => manager.RenameAsset(subscribe.Id, "Renamed"));

        Assert.Equal("Subscribe Asset", subscribe.Name);
    }

    [Fact]
    public async Task AddMembers_UndoPreservesMembershipThatPredatedTheOperation()
    {
        using var manager = await CreateInitializedManager();
        AddKnownAddon(manager, "100");
        AddKnownAddon(manager, "200");
        var asset = AddAsset(manager, "Existing", AddonState.Enabled, "100");
        var historyCount = manager.GetUndoManager().GetHistory(50).Count;

        manager.AddAddonToAsset(asset.Id, " 100 ");
        Assert.Equal(historyCount, manager.GetUndoManager().GetHistory(50).Count);

        manager.AddAddonsToAssetBatch(
            asset.Id,
            ["100", "200", "200", " "]);

        Assert.Equal(["100", "200"], asset.Addons);
        Assert.Equal(historyCount + 1, manager.GetUndoManager().GetHistory(50).Count);
        var action = manager.GetUndoManager().PeekLastAction();
        Assert.Equal(["200"], action!.AffectedAddonIds);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(["100"], asset.Addons);
    }

    [Fact]
    public async Task AssetEdit_NameAndImageRemoval_AreOneDurableUndoUnit()
    {
        using var manager = await CreateInitializedManager();
        manager.CreateAsset("Original");
        var asset = manager.GetConfiguration().Assets
            .Single(current => current.Name == "Original");
        var originalImageBytes = new byte[] { 1, 2, 3, 4 };
        manager.SetAssetImage(asset.Id, originalImageBytes);
        await manager.SaveConfigurationImmediatelyAsync();
        var originalImagePath = manager.ResolveAssetImagePath(asset);
        Assert.NotNull(originalImagePath);
        Assert.True(File.Exists(originalImagePath));
        manager.GetUndoManager().Clear();

        Assert.True(await manager.ApplyAssetEditAsync(
            asset.Id,
            "Renamed",
            sourceImagePath: null,
            crop: null,
            removeImage: true));

        var action = Assert.Single(manager.GetUndoManager().GetHistory(50));
        Assert.Equal(UndoActionType.AssetEdited, action.Type);
        Assert.Equal("Renamed", asset.Name);
        Assert.Null(asset.ImagePath);
        Assert.False(File.Exists(originalImagePath));

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal("Original", asset.Name);
        Assert.NotNull(asset.ImagePath);
        Assert.Equal(originalImageBytes, File.ReadAllBytes(originalImagePath!));
        Assert.False(manager.GetUndoManager().CanUndo);
    }

    [Fact]
    public async Task Favorite_SaveFailureRollsBackMemoryAndUndo()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "Favorite", AddonState.Enabled, "100");
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.SetAssetFavoriteAsync(asset.Id, true));

            Assert.False(asset.IsFavorite);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task CreateAsset_SaveFailureRollsBackMemoryAndUndo()
    {
        using var manager = await CreateInitializedManager();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.CreateAssetAsync("Must not remain"));

            Assert.DoesNotContain(
                manager.GetConfiguration().Assets,
                asset => asset.Name == "Must not remain");
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task AssetEdit_SaveFailureRestoresNameImageAndUndo()
    {
        using var manager = await CreateInitializedManager();
        manager.CreateAsset("Original edit");
        var asset = manager.GetConfiguration().Assets
            .Single(current => current.Name == "Original edit");
        var originalBytes = new byte[] { 9, 8, 7 };
        manager.SetAssetImage(asset.Id, originalBytes);
        await manager.SaveConfigurationImmediatelyAsync();
        var originalPath = manager.ResolveAssetImagePath(asset);
        Assert.NotNull(originalPath);
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.ApplyAssetEditAsync(
                    asset.Id,
                    "Changed edit",
                    sourceImagePath: null,
                    crop: null,
                    removeImage: true));

            Assert.Equal("Original edit", asset.Name);
            Assert.NotNull(asset.ImagePath);
            Assert.Equal(originalBytes, File.ReadAllBytes(originalPath!));
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task AssetState_SaveFailureRollsBackMemoryAndUndo()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "State", AddonState.Enabled, "100");
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.ApplyAssetDefaultStateAsync(
                    asset.Id,
                    AddonState.Excluded));

            Assert.Equal(AddonState.Enabled, asset.GetWholeState());
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task AssetState_RuntimeDeferralDoesNotSuppressALaterUserUndo()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "State source", AddonState.Enabled, "100");
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        manager.GmodRunningProvider = () => true;
        manager.QueueRuntimeApplyProvider = () => manager.CreateAsset("Later action");

        await manager.ApplyAssetDefaultStateAsync(asset.Id, AddonState.Excluded);

        var history = manager.GetUndoManager().GetHistory(50);
        Assert.Equal(2, history.Count);
        Assert.Equal(UndoActionType.AssetCreated, history[0].Type);
        Assert.Equal("Later action", history[0].AssetName);
        Assert.Equal(UndoActionType.AssetExcluded, history[1].Type);
    }

    [Fact]
    public async Task Membership_SaveFailureRollsBackMemoryAndUndo()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "Members", AddonState.Enabled, "100");
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            Assert.ThrowsAny<Exception>(
                () => manager.AddAddonsToAssetBatch(asset.Id, ["200"]));

            Assert.Equal(["100"], asset.Addons);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task DeleteAsset_SaveFailureRestoresDefinitionAndUndo()
    {
        using var manager = await CreateInitializedManager();
        var asset = AddAsset(manager, "Delete rollback", AddonState.Excluded, "100");
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.DeleteAssetAsync(asset.Id));

            Assert.Contains(asset, manager.GetConfiguration().Assets);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task Reset_SaveFailureRestoresConfigurationAndUndoHistory()
    {
        using var manager = await CreateInitializedManager();
        manager.CreateAsset("Keep me");
        var keep = manager.GetConfiguration().Assets
            .Single(asset => asset.Name == "Keep me");
        keep.IsFavorite = true;
        var actionId = manager.GetUndoManager().PeekLastAction()!.Id;
        await manager.SaveConfigurationImmediatelyAsync();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(manager.ResetManagerAsync);

            Assert.Contains(keep, manager.GetConfiguration().Assets);
            Assert.True(keep.IsFavorite);
            Assert.Equal(
                actionId,
                manager.GetUndoManager().PeekLastAction()!.Id);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task Dispose_WaitsBehindInFlightDebouncedSaveAndPersistsLatestSnapshot()
    {
        var manager = await CreateInitializedManager();
        var saveGate = (SemaphoreSlim)typeof(AddonManager)
            .GetField("_configurationSaveGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        var saveRequestedField = typeof(AddonManager)
            .GetField("_saveRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var gateHeld = false;

        try
        {
            await saveGate.WaitAsync();
            gateHeld = true;
            manager.SaveDebounceMilliseconds = 100;
            manager.CreateAsset("Before rename");
            var asset = manager.GetConfiguration().Assets
                .Single(current => current.Name == "Before rename");
            manager.RenameAsset(asset.Id, "Persisted on close");
            await manager.SaveConfigurationAsync();

            Assert.True(SpinWait.SpinUntil(
                () => !(bool)saveRequestedField.GetValue(manager)!,
                TimeSpan.FromSeconds(2)));

            var disposeTask = Task.Run(manager.Dispose);
            await Task.Delay(100);
            Assert.False(disposeTask.IsCompleted);

            saveGate.Release();
            gateHeld = false;
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            var persisted = JObject.Parse(
                File.ReadAllText(Path.Combine(appDataPath, "config.json")));
            Assert.Contains(
                persisted["assets"]!.Children<JObject>(),
                current => current.Value<string>("name") == "Persisted on close");
        }
        finally
        {
            if (gateHeld)
            {
                saveGate.Release();
            }
            manager.Dispose();
        }
    }

    private async Task<AddonManager> CreateInitializedManager()
    {
        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [workshopManifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
        await manager.InitializeAsync();
        return manager;
    }

    private void AddKnownAddon(AddonManager manager, string addonId)
    {
        manager.GetConfiguration().AddonMetadata[addonId] =
            new WorkshopAddon(addonId, Path.Combine(workshopPath, addonId));
    }

    private static Asset AddAsset(
        AddonManager manager,
        string name,
        AddonState state,
        params string[] addonIds)
    {
        var asset = new Asset(name)
        {
            Addons = addonIds.ToList()
        };
        asset.SetWholeState(state);
        manager.GetConfiguration().Assets.Add(asset);
        return asset;
    }

    private static string BlockConfigurationSave(AddonManager manager)
    {
        var configPath = Path.Combine(manager.GetManagerPath(), "config.json");
        File.Delete(configPath);
        Directory.CreateDirectory(configPath);
        return configPath;
    }

    private static async Task UnblockConfigurationSaveAsync(
        AddonManager manager,
        string configPath)
    {
        if (Directory.Exists(configPath))
        {
            Directory.Delete(configPath);
        }
        await manager.SaveConfigurationImmediatelyAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
