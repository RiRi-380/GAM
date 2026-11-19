using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Tests;

public sealed partial class AddonManagerWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchSets_MigrateAnExistingSchemaSevenProfileWithoutChangingItsState(bool explicitNull)
    {
        var path = Path.Combine(appDataPath, "config.json");
        var runtime = File.ReadAllBytes(noMountPath);
        using var manager = await CreateInitializedManager();
        var config = manager.GetConfiguration();
        var subscribe = config.Assets.Single(a => a.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.SetWholeState(AddonState.Excluded);
        config.InitialRuntimeImportCompleted = false;
        config.SubscriptionBaselineInitialized = true;
        config.KnownSubscribedAddonIds = ["100", "200"];
        await manager.SaveConfigurationImmediatelyAsync();
        var raw = JObject.Parse(File.ReadAllText(path));
        if (explicitNull)
            raw["switchSets"] = JValue.CreateNull();
        else
            raw.Remove("switchSets");
        File.WriteAllText(path, raw.ToString());

        await manager.LoadConfigurationAsync();
        var migrated = JObject.Parse(File.ReadAllText(path));
        Assert.Empty(Assert.IsType<JArray>(migrated["switchSets"]));
        Assert.Equal(7, migrated.Value<int>("schemaVersion"));
        Assert.False(migrated.Value<bool>("initialRuntimeImportCompleted"));
        Assert.True(migrated.Value<bool>("subscriptionBaselineInitialized"));
        Assert.Equal(["100", "200"], migrated["knownSubscribedAddonIds"]!.Values<string>());
        Assert.Equal(AddonState.Excluded,
            manager.GetConfiguration().Assets.Single(a => a.Id == SystemAssetDefinitions.SubscribeId).GetWholeState());
        Assert.Equal(runtime, File.ReadAllBytes(noMountPath));
    }

    [Fact]
    public async Task SwitchSets_RestoreMatchingAssetsAndSubscribeExcludeAllAfterGameExitWithOneUndo()
    {
        string setId;
        string childId;
        string removedId;
        using (var first = await CreateInitializedManager())
        {
            var subscribe = first.GetConfiguration().Assets.Single(a => a.Id == SystemAssetDefinitions.SubscribeId);
            subscribe.SetWholeState(AddonState.Excluded);
            var child = AddAsset(first, "Scene", AddonState.Disabled, "100");
            var removed = AddAsset(first, "Later removed", AddonState.Enabled, "200");
            var group = await first.CreateAssetGroupAsync("Scene group", [child.Id]);
            group.DefaultChildState = AddonState.Enabled;
            var saved = await first.SaveSwitchSetAsync(" 撮影用 ");
            Assert.Equal("撮影用", saved.Name);
            Assert.DoesNotContain(SystemAssetDefinitions.GmodDisabledId, saved.AssetStates.Keys);
            setId = saved.Id;
            childId = child.Id;
            removedId = removed.Id;
            await first.RenameSwitchSetAsync(saved.Id, "Scene");
            await Assert.ThrowsAsync<ArgumentException>(() => first.SaveSwitchSetAsync(" scene "));
        }

        using var manager = await CreateInitializedManager();
        var config = manager.GetConfiguration();
        var set = Assert.Single(config.SwitchSets);
        Assert.Equal(setId, set.Id);
        Assert.Equal("Scene", set.Name);
        Assert.Equal(AddonState.Excluded, set.AssetStates[SystemAssetDefinitions.SubscribeId]);
        var subscribeAsset = config.Assets.Single(a => a.Id == SystemAssetDefinitions.SubscribeId);
        var scene = config.Assets.Single(a => a.Id == childId);
        var originalGroup = Assert.Single(config.AssetGroups);
        subscribeAsset.SetWholeState(AddonState.Enabled);
        scene.SetWholeState(AddonState.Enabled);
        config.Assets.RemoveAll(a => a.Id == removedId);
        var added = AddAsset(manager, "Later removed", AddonState.Enabled, "200");
        await manager.UpdateAddonStatesAsync();
        manager.GetUndoManager().Clear();
        var runtimeBefore = File.ReadAllBytes(noMountPath);
        var manifestBefore = File.ReadAllBytes(workshopManifestPath);
        var running = true;
        manager.GmodRunningProvider = () => running;
        var pending = new PendingChangeManager(manager, manager.GetManagerPath());

        Assert.Equal((0, 1, 1, 1, 1), manager.GetSwitchSetChanges(set));
        await manager.ApplySwitchSetAsync(setId);
        Assert.Equal(runtimeBefore, File.ReadAllBytes(noMountPath));
        Assert.Equal(1, pending.GetPendingChangeCount());
        Assert.Equal(AddonState.Excluded, subscribeAsset.GetWholeState());
        Assert.Equal(AddonState.Disabled, scene.GetWholeState());
        Assert.Equal(AddonState.Enabled, added.GetWholeState());
        Assert.DoesNotContain(config.Assets, a => a.Id == removedId);
        Assert.Equal(originalGroup.Id, scene.ParentGroupId);
        Assert.Equal(AddonState.Enabled, originalGroup.DefaultChildState);
        Assert.Equal(UndoActionType.AssetStatesChanged, manager.GetUndoManager().PeekLastAction()!.Type);
        var raw = JObject.Parse(File.ReadAllText(Path.Combine(appDataPath, "config.json")));
        Assert.Equal((int)AddonState.Excluded,
            (int)raw["assets"]!.Single(a => (string?)a["id"] == SystemAssetDefinitions.SubscribeId)["state"]!);

        running = false;
        await pending.ApplyPendingChangesAsync();
        Assert.False(pending.HasPendingChanges());
        var disabled = File.ReadAllText(noMountPath);
        Assert.Contains("\"100\"", disabled);
        Assert.Contains("\"200\"", disabled);
        Assert.Contains("\"999\"", disabled);
        await manager.ApplySwitchSetAsync(setId);
        Assert.Single(manager.GetUndoManager().GetHistory(10));
        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(AddonState.Enabled, subscribeAsset.GetWholeState());
        Assert.Equal(AddonState.Enabled, scene.GetWholeState());
        Assert.Equal(AddonState.Enabled, added.GetWholeState());
        Assert.DoesNotContain("\"100\"", File.ReadAllText(noMountPath));
        Assert.DoesNotContain("\"200\"", File.ReadAllText(noMountPath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(workshopManifestPath));
        await manager.DeleteSwitchSetAsync(setId);
        Assert.Empty(config.SwitchSets);
        Assert.Empty(JObject.Parse(File.ReadAllText(Path.Combine(appDataPath, "config.json")))["switchSets"]!);
    }

    [Fact]
    public async Task SwitchSets_ImportedBundleIdsStayIndependentFromSavedSourceIds()
    {
        using var manager = await CreateInitializedManager();
        var child = AddAsset(manager, "Source", AddonState.Disabled, "100");
        var group = await manager.CreateAssetGroupAsync("Group", [child.Id]);
        var set = await manager.SaveSwitchSetAsync("Before import");
        var path = Path.Combine(rootPath, "group.gam");
        await manager.ExportGamSelectionAsync([], [group.Id], path, includeImages: false);
        var preview = await manager.PreviewGamFileImportAsync(path);
        var imported = await manager.ImportGamFileAsync(preview);
        var copy = Assert.Single(imported.Assets);
        var copiedGroup = Assert.Single(imported.Groups);
        copy.SetWholeState(AddonState.Enabled);
        child.SetWholeState(AddonState.Enabled);

        Assert.Equal((0, 1, 0, 1, 0), manager.GetSwitchSetChanges(set));
        await manager.ApplySwitchSetAsync(set.Id);

        Assert.Equal(AddonState.Disabled, child.GetWholeState());
        Assert.Equal(AddonState.Enabled, copy.GetWholeState());
        Assert.Equal(copiedGroup.Id, copy.ParentGroupId);
        Assert.Equal(group.Id, child.ParentGroupId);
        Assert.Equal(["100"], copy.Addons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchSets_UndoWaitsForInFlightApplyAndHonorsConfirmedIdentity(bool undoPreviouslyConfirmedAction)
    {
        using var manager = await CreateInitializedManager();
        var scene = AddAsset(manager, "Scene", AddonState.Excluded, "100");
        var target = await manager.SaveSwitchSetAsync("Exclude");
        scene.SetWholeState(AddonState.Enabled);
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var previous = await manager.CreateAssetAsync("Other asset");
        var confirmed = Assert.IsType<UndoAction>(manager.GetUndoManager().PeekLastAction());
        var saveGate = (SemaphoreSlim)typeof(AddonManager)
            .GetField("_configurationSaveGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!;

        await saveGate.WaitAsync();
        Task apply;
        Task<bool> undo;
        try
        {
            apply = manager.ApplySwitchSetAsync(target.Id);
            Assert.Equal(AddonState.Excluded, scene.GetWholeState());
            undo = undoPreviouslyConfirmedAction
                ? manager.UndoLastActionAsync(confirmed)
                : manager.UndoLastActionAsync();
        }
        finally
        {
            saveGate.Release();
        }

        await apply.WaitAsync(TimeSpan.FromSeconds(10));
        var undone = await undo.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(!undoPreviouslyConfirmedAction, undone);
        Assert.Contains(previous, manager.GetConfiguration().Assets);
        if (undoPreviouslyConfirmedAction)
        {
            Assert.Equal(AddonState.Excluded, scene.GetWholeState());
            Assert.Equal(UndoActionType.AssetStatesChanged, manager.GetUndoManager().PeekLastAction()!.Type);
        }
        else
        {
            Assert.Equal(AddonState.Enabled, scene.GetWholeState());
            Assert.Same(confirmed, manager.GetUndoManager().PeekLastAction());
            Assert.DoesNotContain("\"100\"", File.ReadAllText(noMountPath));
        }
    }

    [Fact]
    public async Task SwitchSets_FailedApplyAndUndoKeepAssetStatesAndHistoryAtLastSuccessfulSave()
    {
        using var manager = await CreateInitializedManager();
        var scene = AddAsset(manager, "Scene", AddonState.Excluded, "100");
        var set = await manager.SaveSwitchSetAsync("Exclude");
        scene.SetWholeState(AddonState.Enabled);
        await manager.UpdateAddonStatesAsync();
        manager.GetUndoManager().Clear();
        var runtime = File.ReadAllBytes(noMountPath);
        var path = BlockConfigurationSave(manager);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => manager.ApplySwitchSetAsync(set.Id));
            Assert.Equal(AddonState.Enabled, scene.GetWholeState());
            Assert.False(manager.GetUndoManager().CanUndo);
            Assert.Equal(runtime, File.ReadAllBytes(noMountPath));
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, path);
        }

        await manager.ApplySwitchSetAsync(set.Id);
        var undo = Assert.Single(manager.GetUndoManager().GetHistory(10));
        runtime = File.ReadAllBytes(noMountPath);
        path = BlockConfigurationSave(manager);
        try
        {
            Assert.False(await manager.UndoLastActionAsync());
            Assert.Equal(AddonState.Excluded, scene.GetWholeState());
            Assert.Equal(undo.Id, manager.GetUndoManager().PeekLastAction()!.Id);
            Assert.Equal(runtime, File.ReadAllBytes(noMountPath));
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, path);
        }

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(AddonState.Enabled, scene.GetWholeState());
    }

    [Fact]
    public async Task SwitchSets_PersistenceErrorsPreserveSavedDefinitionAndResetClearsIt()
    {
        using var manager = await CreateInitializedManager();
        var saved = await manager.SaveSwitchSetAsync("Original");
        var configPath = BlockConfigurationSave(manager);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => manager.RenameSwitchSetAsync(saved.Id, "Renamed"));
            await Assert.ThrowsAnyAsync<Exception>(() => manager.DeleteSwitchSetAsync(saved.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveSwitchSetAsync("New"));
            await Assert.ThrowsAnyAsync<Exception>(() => manager.ResetManagerAsync());
            var survivor = Assert.Single(manager.GetConfiguration().SwitchSets);
            Assert.Equal("Original", survivor.Name);
            Assert.Equal(saved.Id, survivor.Id);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }

        await manager.ResetManagerAsync();
        Assert.Empty(manager.GetConfiguration().SwitchSets);
        var reset = JObject.Parse(File.ReadAllText(Path.Combine(appDataPath, "config.json")));
        Assert.Empty(Assert.IsType<JArray>(reset["switchSets"]));
    }
}
