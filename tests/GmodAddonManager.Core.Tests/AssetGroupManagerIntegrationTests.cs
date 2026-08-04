using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class AssetGroupManagerIntegrationTests : IDisposable
{
    private const string TestAddonId = "104479467";
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-group-manager-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;

    public AssetGroupManagerIntegrationTests()
    {
        workshopPath = Path.Combine(
            rootPath,
            "steamapps",
            "workshop",
            "content",
            "4000");
        appDataPath = Path.Combine(rootPath, "appdata");
        gmodRootPath = Path.Combine(
            rootPath,
            "steamapps",
            "common",
            "GarrysMod");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(gmodRootPath);
        manifestPath = WorkshopManifestTestData.Write(rootPath, TestAddonId);
    }

    [Fact]
    public async Task GroupBulkStateAndCreationAreSingleUndoableManagerOperations()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var first = await manager.CreateAssetAsync("First");
        var second = await manager.CreateAssetAsync("Second");

        var group = await manager.CreateAssetGroupAsync(
            "Play setup",
            [first.Id, second.Id]);
        Assert.All([first, second], asset => Assert.Equal(group.Id, asset.ParentGroupId));
        Assert.Equal(
            UndoActionType.AssetGroupCreated,
            manager.GetUndoManager().PeekLastAction()!.Type);

        await manager.ApplyAssetGroupStateAsync(group.Id, AddonState.Disabled);
        Assert.All([first, second], asset => Assert.Equal(AddonState.Disabled, asset.State));
        Assert.Equal(AddonState.Disabled, group.DefaultChildState);
        Assert.Equal(
            UndoActionType.AssetGroupStateChanged,
            manager.GetUndoManager().PeekLastAction()!.Type);

        var child = await manager.CreateAssetInGroupAsync(
            "New child",
            group.Id,
            rule: null);
        Assert.Equal(group.Id, child.ParentGroupId);
        Assert.Equal(AddonState.Disabled, child.State);
        Assert.Equal(
            UndoActionType.AssetCreated,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Id == child.Id);
        Assert.True(await manager.UndoLastActionAsync());
        Assert.All([first, second], asset => Assert.Equal(AddonState.Enabled, asset.State));
        Assert.Equal(AddonState.Enabled, group.DefaultChildState);
    }

    [Fact]
    public async Task CreatingGroupPreservesAssetStateResolvedSourceAndExpectedState()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().AddonMetadata[TestAddonId] = new WorkshopAddon(
            TestAddonId,
            Path.Combine(workshopPath, TestAddonId));
        var subscribe = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        await manager.ApplyAssetDefaultStateAsync(
            subscribe.Id,
            AddonState.Disabled);
        var enabledAsset = await manager.CreateAssetAsync("Enabled source");
        enabledAsset.AddAddon(TestAddonId);
        await manager.SaveConfigurationImmediatelyAsync();

        var before = manager.GetResolvedAddonState(TestAddonId);
        var expectedBefore = manager.GetFinalAddonStates()[TestAddonId];

        var group = await manager.CreateAssetGroupAsync(
            "Container only",
            [enabledAsset.Id]);

        var after = manager.GetResolvedAddonState(TestAddonId);
        Assert.Equal(group.Id, enabledAsset.ParentGroupId);
        Assert.Equal(AddonState.Enabled, enabledAsset.GetWholeState());
        Assert.True(expectedBefore);
        Assert.True(manager.GetFinalAddonStates()[TestAddonId]);
        Assert.Equal(before.DesiredEnabled, after.DesiredEnabled);
        Assert.Equal(before.EnabledBySubscribe, after.EnabledBySubscribe);
        Assert.Equal(before.Reason, after.Reason);
        Assert.Equal(
            before.EnabledByAssets.Select(source => source.AssetId),
            after.EnabledByAssets.Select(source => source.AssetId));
        Assert.Equal([enabledAsset.Id], after.EnabledByAssets.Select(source => source.AssetId));
    }

    [Fact]
    public async Task DeletingGroupUnwrapsChildrenAndUndoRestoresHierarchy()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var first = await manager.CreateAssetAsync("First");
        var second = await manager.CreateAssetAsync("Second");
        var group = await manager.CreateAssetGroupAsync(
            "Container",
            [first.Id, second.Id]);

        Assert.True(await manager.DeleteAssetGroupAsync(group.Id));
        Assert.DoesNotContain(
            manager.GetConfiguration().AssetGroups,
            candidate => candidate.Id == group.Id);
        Assert.All([first, second], asset => Assert.Null(asset.ParentGroupId));

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Contains(
            manager.GetConfiguration().AssetGroups,
            candidate => candidate.Id == group.Id);
        Assert.All([first, second], asset => Assert.Equal(group.Id, asset.ParentGroupId));
    }

    [Fact]
    public async Task DeletingGroupAndAssetsIsOneDurableUndoableRuntimeMutation()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().AddonMetadata[TestAddonId] = new WorkshopAddon(
            TestAddonId,
            Path.Combine(workshopPath, TestAddonId));
        var subscribe = manager.GetConfiguration().Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        await manager.ApplyAssetDefaultStateAsync(
            subscribe.Id,
            AddonState.Disabled);
        var fixedAsset = await manager.CreateAssetAsync("Fixed child");
        fixedAsset.AddAddon(TestAddonId);
        fixedAsset.CurrentVersion = 4;
        fixedAsset.VersionHistory.Add(new AssetVersion(4, [TestAddonId]));
        var smartAsset = await manager.CreateSmartAssetAsync(
            "Smart child",
            new AssetMembershipRule(AssetMembershipRuleKind.Tag, "Fun"));
        var group = await manager.CreateAssetGroupAsync(
            "Delete all",
            [fixedAsset.Id, smartAsset.Id]);
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var runtimeWrites = new List<bool>();
        manager.RuntimeWriteObserver = states =>
        {
            if (states.TryGetValue(TestAddonId, out var enabled))
            {
                runtimeWrites.Add(enabled);
            }
        };
        Assert.True(manager.GetFinalAddonStates()[TestAddonId]);

        Assert.True(await manager.DeleteAssetGroupAsync(
            group.Id,
            AssetGroupDeleteMode.DeleteAssets));

        Assert.DoesNotContain(
            manager.GetConfiguration().AssetGroups,
            candidate => candidate.Id == group.Id);
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Id == fixedAsset.Id || asset.Id == smartAsset.Id);
        var action = Assert.IsType<UndoAction>(
            manager.GetUndoManager().PeekLastAction());
        Assert.Equal(UndoActionType.AssetGroupDeleted, action.Type);
        Assert.Equal(2, action.DeletedAssets!.Count);
        Assert.False(manager.GetFinalAddonStates()[TestAddonId]);
        Assert.Contains(false, runtimeWrites);

        Assert.True(await manager.UndoLastActionAsync());

        var restoredGroup = Assert.Single(
            manager.GetConfiguration().AssetGroups,
            candidate => candidate.Id == group.Id);
        var restoredFixed = Assert.Single(
            manager.GetConfiguration().Assets,
            asset => asset.Id == fixedAsset.Id);
        var restoredSmart = Assert.Single(
            manager.GetConfiguration().Assets,
            asset => asset.Id == smartAsset.Id);
        Assert.Same(group, restoredGroup);
        Assert.Same(fixedAsset, restoredFixed);
        Assert.Same(smartAsset, restoredSmart);
        Assert.Equal(group.Id, restoredFixed.ParentGroupId);
        Assert.Equal(group.Id, restoredSmart.ParentGroupId);
        Assert.Equal(4, restoredFixed.CurrentVersion);
        Assert.Single(restoredFixed.VersionHistory);
        Assert.NotNull(restoredSmart.MembershipRule);
        Assert.True(manager.GetFinalAddonStates()[TestAddonId]);
        Assert.Contains(true, runtimeWrites);
    }

    [Fact]
    public async Task DeletingGroupAndAssets_SaveFailureRestoresEverythingAndUndoHistory()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var child = await manager.CreateAssetAsync("Rollback child");
        var group = await manager.CreateAssetGroupAsync(
            "Rollback group",
            [child.Id]);
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.DeleteAssetGroupAsync(
                    group.Id,
                    AssetGroupDeleteMode.DeleteAssets));

            var restoredGroup = Assert.Single(
                manager.GetConfiguration().AssetGroups,
                candidate => candidate.Id == group.Id);
            var restoredChild = Assert.Single(
                manager.GetConfiguration().Assets,
                asset => asset.Id == child.Id);
            Assert.Equal(restoredGroup.Id, restoredChild.ParentGroupId);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task GroupNameAndImageEditUndoRestoresBoth()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var group = await manager.CreateAssetGroupAsync("Before", Array.Empty<string>());
        var sourcePath = Path.Combine(rootPath, "group-source.png");
        await File.WriteAllBytesAsync(sourcePath, CreatePng());

        Assert.True(await manager.ApplyAssetGroupEditAsync(
            group.Id,
            "After",
            sourcePath,
            crop: null,
            removeImage: false));
        var imagePath = manager.ResolveAssetGroupImagePath(group);
        Assert.Equal("After", group.Name);
        Assert.NotNull(imagePath);
        Assert.True(File.Exists(imagePath));

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal("Before", group.Name);
        Assert.Null(group.ImagePath);
        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public async Task AssetAndGroupNamesShareOneCaseInsensitiveNamespace()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.CreateAssetAsync("Shared Name");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateAssetGroupAsync("shared name", Array.Empty<string>()));

        var group = await manager.CreateAssetGroupAsync(
            "Unique Group",
            Array.Empty<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateAssetAsync("UNIQUE GROUP"));
        Assert.True(manager.AssetNameExists(group.Name));
    }

    [Fact]
    public async Task GroupHierarchyAndOrderPersistAcrossManagerRestart()
    {
        string groupId;
        string childId;
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            var child = await manager.CreateAssetAsync("Persistent child");
            var group = await manager.CreateAssetGroupAsync(
                "Persistent group",
                [child.Id]);
            await manager.SetAssetGroupFavoriteAsync(group.Id, true);
            groupId = group.Id;
            childId = child.Id;
        }

        using var reloaded = CreateManager();
        await reloaded.InitializeAsync();
        var restoredGroup = Assert.Single(
            reloaded.GetConfiguration().AssetGroups,
            group => group.Id == groupId);
        var restoredChild = Assert.Single(
            reloaded.GetConfiguration().Assets,
            asset => asset.Id == childId);
        Assert.True(restoredGroup.IsFavorite);
        Assert.Equal(groupId, restoredChild.ParentGroupId);
    }

    [Fact]
    public async Task NestedGroupTreeStateMemosAndDepthPersistAcrossManagerRestart()
    {
        string rootId;
        string childId;
        string grandchildId;
        string leafId;
        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            await manager.SetMaxNestedGroupDepthAsync(2);
            var root = await manager.CreateAssetGroupAsync(
                "Root",
                parentGroupId: null,
                memberAssetIds: null,
                childGroupIds: null);
            var child = await manager.CreateAssetGroupAsync(
                "Child",
                root.Id,
                memberAssetIds: null,
                childGroupIds: null);
            var grandchild = await manager.CreateAssetGroupAsync(
                "Grandchild",
                child.Id,
                memberAssetIds: null,
                childGroupIds: null);
            var leaf = await manager.CreateAssetInGroupAsync(
                "Leaf",
                grandchild.Id,
                rule: null);

            Assert.True(await manager.UpdateAssetMemoAsync(leaf.Id, "leaf memo"));
            Assert.True(await manager.UpdateAssetGroupMemoAsync(root.Id, "root memo"));
            await manager.ApplyAssetGroupStateAsync(root.Id, AddonState.Excluded);

            Assert.Equal(AddonState.Excluded, leaf.State);
            Assert.All([root, child, grandchild], group =>
                Assert.Equal(AddonState.Excluded, group.DefaultChildState));
            Assert.Equal([child.Id], manager.GetOrderedAssetGroupChildGroups(root.Id)
                .Select(group => group.Id));

            rootId = root.Id;
            childId = child.Id;
            grandchildId = grandchild.Id;
            leafId = leaf.Id;
        }

        using var reloaded = CreateManager();
        await reloaded.InitializeAsync();
        Assert.Equal(2, reloaded.GetConfiguration().MaxNestedGroupDepth);
        var rootReloaded = Assert.Single(reloaded.GetConfiguration().AssetGroups,
            group => group.Id == rootId);
        var childReloaded = Assert.Single(reloaded.GetConfiguration().AssetGroups,
            group => group.Id == childId);
        var grandchildReloaded = Assert.Single(reloaded.GetConfiguration().AssetGroups,
            group => group.Id == grandchildId);
        var leafReloaded = Assert.Single(reloaded.GetConfiguration().Assets,
            asset => asset.Id == leafId);
        Assert.Equal("root memo", rootReloaded.Memo);
        Assert.Equal("leaf memo", leafReloaded.Memo);
        Assert.Null(rootReloaded.ParentGroupId);
        Assert.Equal(rootReloaded.Id, childReloaded.ParentGroupId);
        Assert.Equal(childReloaded.Id, grandchildReloaded.ParentGroupId);
        Assert.Equal(grandchildReloaded.Id, leafReloaded.ParentGroupId);
        Assert.Equal(AddonState.Excluded, leafReloaded.State);
    }

    [Fact]
    public async Task MemoAndDepthMutationsRollbackOnSaveFailureAndRejectInvalidChanges()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("Memo asset");
        var root = await manager.CreateAssetGroupAsync("Root", Array.Empty<string>());
        var child = await manager.CreateAssetGroupAsync(
            "Child",
            root.Id,
            memberAssetIds: null,
            childGroupIds: null);
        await manager.SetMaxNestedGroupDepthAsync(2);
        await manager.CreateAssetGroupAsync(
            "Grandchild",
            child.Id,
            memberAssetIds: null,
            childGroupIds: null);
        manager.GetUndoManager().Clear();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.SetMaxNestedGroupDepthAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.SetMaxNestedGroupDepthAsync(11));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SetMaxNestedGroupDepthAsync(1));
        Assert.Equal(2, manager.GetConfiguration().MaxNestedGroupDepth);
        Assert.Equal(root.Id, child.ParentGroupId);

        var configPath = BlockConfigurationSave(manager);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.UpdateAssetMemoAsync(asset.Id, "must roll back"));
            Assert.Equal(string.Empty, asset.Memo);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }

        configPath = BlockConfigurationSave(manager);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.SetMaxNestedGroupDepthAsync(3));
            Assert.Equal(2, manager.GetConfiguration().MaxNestedGroupDepth);
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }

        Assert.True(await manager.UpdateAssetMemoAsync(asset.Id, "saved"));
        Assert.False(await manager.UpdateAssetGroupMemoAsync(root.Id, null));
        Assert.Equal("saved", asset.Memo);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpdateAssetMemoAsync(
                SystemAssetDefinitions.SubscribeId,
                "forbidden"));
    }

    private AddonManager CreateManager()
    {
        return new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        })
        {
            StateMatchTimeout = TimeSpan.Zero
        };
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(
            24,
            24,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(data);
        return data.ToArray();
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
