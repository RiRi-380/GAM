using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AssetGroupServiceTests
{
    private readonly AssetGroupService service = new();

    [Fact]
    public void CreateGroup_AllowsEmptyAndRejectsGlobalCaseInsensitiveCollision()
    {
        var configuration = CreateConfiguration();
        configuration.Assets.Add(new Asset("FPS") { Id = "fps" });

        var group = service.CreateGroup(
            configuration,
            "  Utility  ",
            memberAssetIds: null,
            out var undo);

        Assert.Equal("Utility", group.Name);
        Assert.Equal(AddonState.Enabled, group.DefaultChildState);
        Assert.Equal(AssetGroupDisplayState.Enabled, service.GetDisplayState(configuration, group.Id));
        Assert.Equal(UndoActionType.AssetGroupCreated, undo.Type);
        Assert.Equal(group.Id, undo.GroupId);
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateGroup(configuration, "fps", null, out _));
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateGroup(configuration, "UTILITY", null, out _));
        Assert.Throws<ArgumentException>(() =>
            service.CreateGroup(configuration, "bad\u0001name", null, out _));
        Assert.Throws<ArgumentException>(() =>
            service.CreateGroup(configuration, new string('x', 201), null, out _));
    }

    [Fact]
    public void CreateGroup_RejectsSystemOrAlreadyGroupedMemberWithoutPartialMutation()
    {
        var configuration = CreateConfiguration();
        var first = service.CreateGroup(configuration, "First", null, out _);
        var child = new Asset("Child") { Id = "child", ParentGroupId = first.Id };
        configuration.Assets.Add(child);
        var groupCount = configuration.AssetGroups.Count;

        Assert.Throws<InvalidOperationException>(() =>
            service.CreateGroup(
                configuration,
                "Invalid system",
                [SystemAssetDefinitions.SubscribeId],
                out _));
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateGroup(configuration, "Invalid parent", [child.Id], out _));

        Assert.Equal(groupCount, configuration.AssetGroups.Count);
        Assert.Equal(first.Id, child.ParentGroupId);
    }

    [Fact]
    public void GroupState_IsDerivedAndBulkApplyReturnsOneCompleteUndoPayload()
    {
        var configuration = CreateConfiguration();
        var enabled = new Asset("Enabled") { Id = "enabled", State = AddonState.Enabled };
        var excluded = new Asset("Excluded") { Id = "excluded", State = AddonState.Excluded };
        configuration.Assets.AddRange([enabled, excluded]);
        var group = service.CreateGroup(
            configuration,
            "Play",
            [enabled.Id, excluded.Id],
            out _);

        Assert.Equal(AssetGroupDisplayState.Mixed, service.GetDisplayState(configuration, group.Id));

        var undo = Assert.IsType<UndoAction>(
            service.ApplyGroupState(configuration, group.Id, AddonState.Disabled));

        Assert.Equal(UndoActionType.AssetGroupStateChanged, undo.Type);
        Assert.Equal(AddonState.Enabled, undo.PreviousGroupDefaultState);
        Assert.Equal(AddonState.Enabled, undo.PreviousAssetStates![enabled.Id]);
        Assert.Equal(AddonState.Excluded, undo.PreviousAssetStates[excluded.Id]);
        Assert.Equal(AddonState.Disabled, group.DefaultChildState);
        Assert.All([enabled, excluded], asset => Assert.Equal(AddonState.Disabled, asset.State));
        Assert.Equal(AssetGroupDisplayState.Disabled, service.GetDisplayState(configuration, group.Id));
        Assert.Null(service.ApplyGroupState(configuration, group.Id, AddonState.Disabled));
    }

    [Theory]
    [InlineData(AddonState.Enabled)]
    [InlineData(AddonState.Disabled)]
    [InlineData(AddonState.Excluded)]
    public void NewAsset_InheritsGroupDefaultWhileRootAlwaysStartsEnabled(AddonState groupState)
    {
        var configuration = CreateConfiguration();
        var group = service.CreateGroup(configuration, "Target", null, out _);
        service.ApplyGroupState(configuration, group.Id, groupState);
        var grouped = new Asset("Grouped") { Id = "grouped", State = AddonState.Excluded };
        var root = new Asset("Root") { Id = "root", State = AddonState.Excluded };

        service.AddNewAsset(configuration, grouped, group.Id);
        service.AddNewAsset(configuration, root);

        Assert.Equal(group.Id, grouped.ParentGroupId);
        Assert.Equal(groupState, grouped.State);
        Assert.Null(root.ParentGroupId);
        Assert.Equal(AddonState.Enabled, root.State);
    }

    [Fact]
    public void NewAsset_InheritsCurrentUniformStateButMixedFallsBackToGroupDefault()
    {
        var configuration = CreateConfiguration();
        var first = new Asset("First") { Id = "first", State = AddonState.Disabled };
        var second = new Asset("Second") { Id = "second", State = AddonState.Disabled };
        configuration.Assets.AddRange([first, second]);
        var group = service.CreateGroup(
            configuration,
            "Derived",
            [first.Id, second.Id],
            out _);
        Assert.Equal(AddonState.Enabled, group.DefaultChildState);

        var uniformChild = new Asset("Uniform child") { Id = "uniform-child" };
        service.AddNewAsset(configuration, uniformChild, group.Id);
        Assert.Equal(AddonState.Disabled, uniformChild.State);

        first.State = AddonState.Excluded;
        var mixedChild = new Asset("Mixed child") { Id = "mixed-child" };
        service.AddNewAsset(configuration, mixedChild, group.Id);
        Assert.Equal(AddonState.Enabled, mixedChild.State);
    }

    [Fact]
    public void MovingExistingAsset_PreservesStateAndEnforcesSingleParent()
    {
        var configuration = CreateConfiguration();
        var source = service.CreateGroup(configuration, "Source", null, out _);
        var destination = service.CreateGroup(configuration, "Destination", null, out _);
        service.ApplyGroupState(configuration, destination.Id, AddonState.Excluded);
        var asset = new Asset("Existing")
        {
            Id = "existing",
            State = AddonState.Disabled
        };
        configuration.Assets.Add(asset);

        service.MoveAsset(configuration, asset.Id, source.Id);
        var undo = Assert.IsType<UndoAction>(
            service.MoveAsset(configuration, asset.Id, destination.Id));

        Assert.Equal(destination.Id, asset.ParentGroupId);
        Assert.Equal(AddonState.Disabled, asset.State);
        Assert.Equal(source.Id, undo.PreviousAssetParentGroupIds![asset.Id]);
        Assert.Single(
            configuration.AssetGroups,
            group => group.Id == asset.ParentGroupId);
    }

    [Fact]
    public void SetMembers_RejectsImplicitStealAndUnselectedChildrenReturnToRoot()
    {
        var configuration = CreateConfiguration();
        var a = new Asset("A") { Id = "a", State = AddonState.Excluded };
        var b = new Asset("B") { Id = "b", State = AddonState.Disabled };
        configuration.Assets.AddRange([a, b]);
        var first = service.CreateGroup(configuration, "First", [a.Id], out _);
        var second = service.CreateGroup(configuration, "Second", [b.Id], out _);

        Assert.Throws<InvalidOperationException>(() =>
            service.SetGroupMembers(configuration, first.Id, [a.Id, b.Id]));
        Assert.Equal(second.Id, b.ParentGroupId);

        var undo = Assert.IsType<UndoAction>(
            service.SetGroupMembers(configuration, first.Id, []));
        Assert.Null(a.ParentGroupId);
        Assert.Equal(AddonState.Excluded, a.State);
        Assert.Equal(first.Id, undo.PreviousAssetParentGroupIds![a.Id]);
    }

    [Fact]
    public void DeleteGroup_UnwrapsChildrenWithoutChangingTheirState()
    {
        var configuration = CreateConfiguration();
        var favorite = new Asset("Favorite")
        {
            Id = "favorite",
            IsFavorite = true,
            State = AddonState.Enabled
        };
        var normal = new Asset("Normal")
        {
            Id = "normal",
            State = AddonState.Excluded
        };
        configuration.Assets.AddRange([favorite, normal]);
        var group = service.CreateGroup(
            configuration,
            "Temporary",
            [favorite.Id, normal.Id],
            out _);

        var undo = service.DeleteGroup(configuration, group.Id);

        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.Null(favorite.ParentGroupId);
        Assert.Null(normal.ParentGroupId);
        Assert.Equal(AddonState.Enabled, favorite.State);
        Assert.Equal(AddonState.Excluded, normal.State);
        Assert.Same(group, undo.DeletedAssetGroup);
        Assert.Equal(group.Id, undo.PreviousAssetParentGroupIds![normal.Id]);
    }

    [Fact]
    public void DeleteGroup_DeleteAssetsIsAtomicAndUndoRestoresCompleteDefinitions()
    {
        var configuration = CreateConfiguration();
        var fixedAsset = new Asset("Fixed")
        {
            Id = "fixed",
            SortOrder = 5,
            State = AddonState.Disabled,
            CurrentVersion = 3
        };
        fixedAsset.AddAddon("100");
        fixedAsset.VersionHistory.Add(new AssetVersion(3, ["100"]));
        var smartAsset = new Asset("Smart")
        {
            Id = "smart",
            SortOrder = 8,
            State = AddonState.Excluded,
            MembershipRule = new AssetMembershipRule(
                AssetMembershipRuleKind.Tag,
                "Fun")
        };
        configuration.Assets.AddRange([fixedAsset, smartAsset]);
        var group = service.CreateGroup(
            configuration,
            "Disposable",
            [fixedAsset.Id, smartAsset.Id],
            out _);
        var fixedOrder = fixedAsset.SortOrder;
        var smartOrder = smartAsset.SortOrder;

        var action = service.DeleteGroup(
            configuration,
            group.Id,
            AssetGroupDeleteMode.DeleteAssets);

        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.DoesNotContain(configuration.Assets, asset => asset.Id == fixedAsset.Id);
        Assert.DoesNotContain(configuration.Assets, asset => asset.Id == smartAsset.Id);
        Assert.Equal(UndoActionType.AssetGroupDeleted, action.Type);
        Assert.Equal(2, action.DeletedAssets!.Count);
        Assert.Contains(fixedAsset, action.DeletedAssets);
        Assert.Contains(smartAsset, action.DeletedAssets);

        Assert.True(service.TryUndo(configuration, action, out var undo));
        Assert.True(undo!.RequiresRuntimeReconcile);
        Assert.Same(group, undo.RestoredGroup);
        Assert.Contains(fixedAsset, configuration.Assets);
        Assert.Contains(smartAsset, configuration.Assets);
        Assert.Equal(group.Id, fixedAsset.ParentGroupId);
        Assert.Equal(group.Id, smartAsset.ParentGroupId);
        Assert.Equal(fixedOrder, fixedAsset.SortOrder);
        Assert.Equal(smartOrder, smartAsset.SortOrder);
        Assert.Equal(3, fixedAsset.CurrentVersion);
        Assert.Single(fixedAsset.VersionHistory);
        Assert.NotNull(smartAsset.MembershipRule);

        undo.Rollback();
        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.DoesNotContain(configuration.Assets, asset => asset.Id == fixedAsset.Id);
        Assert.DoesNotContain(configuration.Assets, asset => asset.Id == smartAsset.Id);
    }

    [Theory]
    [InlineData(AssetGroupDeleteMode.KeepAssets)]
    [InlineData(AssetGroupDeleteMode.DeleteAssets)]
    public void DeleteGroup_RejectsProtectedChildWithoutPartialMutation(
        AssetGroupDeleteMode deleteMode)
    {
        var configuration = CreateConfiguration();
        var group = service.CreateGroup(configuration, "Corrupt", null, out _);
        var subscribe = configuration.Assets.Single(asset =>
            asset.Id == SystemAssetDefinitions.SubscribeId);
        subscribe.ParentGroupId = group.Id;

        Assert.Throws<InvalidOperationException>(() =>
            service.DeleteGroup(configuration, group.Id, deleteMode));

        Assert.Contains(group, configuration.AssetGroups);
        Assert.Contains(subscribe, configuration.Assets);
        Assert.Equal(group.Id, subscribe.ParentGroupId);
    }

    [Fact]
    public void TryUndo_GroupAndAssetsDeleteRejectsNameCollisionAtomically()
    {
        var configuration = CreateConfiguration();
        var child = new Asset("Collision") { Id = "deleted-child" };
        configuration.Assets.Add(child);
        var group = service.CreateGroup(
            configuration,
            "Deleted group",
            [child.Id],
            out _);
        var action = service.DeleteGroup(
            configuration,
            group.Id,
            AssetGroupDeleteMode.DeleteAssets);
        configuration.Assets.Add(new Asset("collision") { Id = "replacement" });

        Assert.False(service.TryUndo(configuration, action, out var undo));
        Assert.Null(undo);
        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.DoesNotContain(configuration.Assets, asset => asset.Id == child.Id);
        Assert.Contains(configuration.Assets, asset => asset.Id == "replacement");
    }

    [Fact]
    public void Reorder_MixesAssetAndGroupButClampsFavoriteBands()
    {
        var configuration = CreateConfiguration();
        var favoriteAsset = new Asset("Favorite Asset")
        {
            Id = "favorite-asset",
            IsFavorite = true,
            SortOrder = 0
        };
        var normalAsset = new Asset("Normal Asset")
        {
            Id = "normal-asset",
            SortOrder = 0
        };
        configuration.Assets.AddRange([favoriteAsset, normalAsset]);
        var favoriteGroup = service.CreateGroup(configuration, "Favorite Group", null, out _);
        service.SetFavorite(configuration, AssetListEntryKind.Group, favoriteGroup.Id, true);
        var normalGroup = service.CreateGroup(configuration, "Normal Group", null, out _);
        service.NormalizeConfiguration(configuration);

        var movedWithinFavorite = Assert.IsType<UndoAction>(service.ReorderEntry(
            configuration,
            AssetListEntryKind.Group,
            favoriteGroup.Id,
            targetIndex: 0,
            parentGroupId: null));
        Assert.Equal(UndoActionType.AssetOrderChanged, movedWithinFavorite.Type);
        Assert.Equal(0, favoriteGroup.SortOrder);
        Assert.Equal(1, favoriteAsset.SortOrder);

        service.ReorderEntry(
            configuration,
            AssetListEntryKind.Group,
            favoriteGroup.Id,
            targetIndex: 99,
            parentGroupId: null);
        Assert.Equal(1, favoriteGroup.SortOrder);
        Assert.Equal(0, favoriteAsset.SortOrder);
        Assert.Equal(0, normalAsset.SortOrder);
        Assert.Equal(1, normalGroup.SortOrder);
        Assert.Throws<InvalidOperationException>(() => service.ReorderEntry(
            configuration,
            AssetListEntryKind.Asset,
            SystemAssetDefinitions.SubscribeId,
            0,
            null));
    }

    [Fact]
    public void Reorder_RejectsStaleContainerAndSupportsChildOrder()
    {
        var configuration = CreateConfiguration();
        var a = new Asset("A") { Id = "a" };
        var b = new Asset("B") { Id = "b" };
        configuration.Assets.AddRange([a, b]);
        var group = service.CreateGroup(configuration, "Group", [a.Id, b.Id], out _);

        Assert.Throws<InvalidOperationException>(() => service.ReorderEntry(
            configuration,
            AssetListEntryKind.Asset,
            a.Id,
            1,
            parentGroupId: null));

        service.ReorderEntry(
            configuration,
            AssetListEntryKind.Asset,
            a.Id,
            1,
            group.Id);
        Assert.Equal(0, b.SortOrder);
        Assert.Equal(1, a.SortOrder);
    }

    [Fact]
    public void NormalizeConfiguration_RepairsParentsNamesStatesAndMixedOrderIdempotently()
    {
        var configuration = CreateConfiguration();
        var group = new AssetGroup("DUPLICATE")
        {
            Id = "group",
            DefaultChildState = (AddonState)99,
            IsFavorite = true,
            SortOrder = -1
        };
        configuration.AssetGroups.Add(group);
        var custom = new Asset("duplicate")
        {
            Id = "custom",
            ParentGroupId = "missing",
            SortOrder = -1
        };
        configuration.Assets.Add(custom);
        configuration.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.SubscribeId).ParentGroupId = group.Id;

        Assert.True(service.NormalizeConfiguration(configuration));
        Assert.Null(custom.ParentGroupId);
        Assert.All(configuration.Assets.Where(asset => asset.IsSystem), asset =>
            Assert.Null(asset.ParentGroupId));
        Assert.Equal(AddonState.Enabled, group.DefaultChildState);
        Assert.NotEqual(custom.Name, group.Name, StringComparer.OrdinalIgnoreCase);
        Assert.True(custom.SortOrder >= 0);
        Assert.True(group.SortOrder >= 0);
        Assert.False(service.NormalizeConfiguration(configuration));
    }

    [Fact]
    public void NormalizeConfiguration_SanitizesPortableNamesAndBoundsCollisionSuffixes()
    {
        var configuration = CreateConfiguration();
        var longName = new string('x', 210) + "\u0001";
        configuration.Assets.Add(new Asset(longName) { Id = "long-asset" });
        configuration.AssetGroups.Add(new AssetGroup(longName) { Id = "long-group" });

        service.NormalizeConfiguration(configuration);

        var assetName = configuration.Assets.Single(asset => asset.Id == "long-asset").Name;
        var groupName = configuration.AssetGroups.Single(group => group.Id == "long-group").Name;
        Assert.InRange(assetName.Length, 1, GamAssetDocumentCodec.MaximumAssetNameLength);
        Assert.InRange(groupName.Length, 1, GamAssetDocumentCodec.MaximumAssetNameLength);
        Assert.DoesNotContain(assetName, char.IsControl);
        Assert.DoesNotContain(groupName, char.IsControl);
        Assert.NotEqual(assetName, groupName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeConfiguration_TruncatesNamesAtUnicodeTextElementBoundaries()
    {
        var configuration = CreateConfiguration();
        var boundaryName = new string('A', 199) + "😀";
        configuration.Assets.Add(new Asset(boundaryName) { Id = "unicode-asset" });
        configuration.AssetGroups.Add(new AssetGroup(boundaryName) { Id = "unicode-group" });

        service.NormalizeConfiguration(configuration);

        var assetName = configuration.Assets.Single(asset => asset.Id == "unicode-asset").Name;
        var groupName = configuration.AssetGroups.Single(group => group.Id == "unicode-group").Name;
        Assert.Equal(new string('A', 199), assetName);
        Assert.EndsWith(" (2)", groupName, StringComparison.Ordinal);
        Assert.DoesNotContain(assetName, char.IsSurrogate);
        Assert.DoesNotContain(groupName, char.IsSurrogate);
        Assert.True(groupName.Length <= GamAssetDocumentCodec.MaximumAssetNameLength);
    }

    [Fact]
    public void TryUndo_GroupStateAppliesInverseAndProvidesSaveFailureRollback()
    {
        var configuration = CreateConfiguration();
        var child = new Asset("Child")
        {
            Id = "child",
            State = AddonState.Excluded
        };
        configuration.Assets.Add(child);
        var group = service.CreateGroup(configuration, "Group", [child.Id], out _);
        var action = Assert.IsType<UndoAction>(
            service.ApplyGroupState(configuration, group.Id, AddonState.Disabled));

        Assert.True(service.TryUndo(configuration, action, out var mutation));
        Assert.NotNull(mutation);
        Assert.True(mutation!.RequiresRuntimeReconcile);
        Assert.Equal(AddonState.Excluded, child.State);
        Assert.Equal(AddonState.Enabled, group.DefaultChildState);

        mutation.Rollback();
        Assert.Equal(AddonState.Disabled, child.State);
        Assert.Equal(AddonState.Disabled, group.DefaultChildState);
    }

    [Fact]
    public void TryUndo_GroupCreateAndDeleteRestoreExactStructureAndRollback()
    {
        var configuration = CreateConfiguration();
        var child = new Asset("Child")
        {
            Id = "child",
            SortOrder = 4,
            State = AddonState.Disabled
        };
        configuration.Assets.Add(child);
        var group = service.CreateGroup(
            configuration,
            "Group",
            [child.Id],
            out var createAction);

        Assert.True(service.TryUndo(configuration, createAction, out var createUndo));
        Assert.Null(child.ParentGroupId);
        Assert.Equal(4, child.SortOrder);
        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.Same(group, createUndo!.RemovedGroup);

        createUndo.Rollback();
        Assert.Equal(group.Id, child.ParentGroupId);
        Assert.Contains(configuration.AssetGroups, candidate => candidate.Id == group.Id);

        var deleteAction = service.DeleteGroup(configuration, group.Id);
        Assert.True(service.TryUndo(configuration, deleteAction, out var deleteUndo));
        Assert.Equal(group.Id, child.ParentGroupId);
        Assert.Same(group, deleteUndo!.RestoredGroup);

        deleteUndo.Rollback();
        Assert.Null(child.ParentGroupId);
        Assert.DoesNotContain(configuration.AssetGroups, candidate => candidate.Id == group.Id);
        Assert.Equal(AddonState.Disabled, child.State);
    }

    [Fact]
    public void TryUndo_MembershipOrderAndFavoriteRestoreWithoutRuntimeReconcile()
    {
        var configuration = CreateConfiguration();
        var a = new Asset("A") { Id = "a", SortOrder = 0 };
        var b = new Asset("B") { Id = "b", SortOrder = 1 };
        configuration.Assets.AddRange([a, b]);
        var group = service.CreateGroup(configuration, "Group", null, out _);

        var moveAction = Assert.IsType<UndoAction>(
            service.MoveAsset(configuration, a.Id, group.Id));
        Assert.True(service.TryUndo(configuration, moveAction, out var moveUndo));
        Assert.Null(a.ParentGroupId);
        Assert.False(moveUndo!.RequiresRuntimeReconcile);
        moveUndo.Rollback();
        Assert.Equal(group.Id, a.ParentGroupId);

        service.MoveAsset(configuration, a.Id, destinationGroupId: null);
        var reorderAction = Assert.IsType<UndoAction>(service.ReorderEntry(
            configuration,
            AssetListEntryKind.Asset,
            a.Id,
            0,
            parentGroupId: null));
        Assert.Equal(0, a.SortOrder);
        Assert.True(service.TryUndo(configuration, reorderAction, out var reorderUndo));
        Assert.Equal(3, a.SortOrder);
        reorderUndo!.Rollback();
        Assert.Equal(0, a.SortOrder);

        var favoriteAction = Assert.IsType<UndoAction>(service.SetFavorite(
            configuration,
            AssetListEntryKind.Asset,
            b.Id,
            true));
        Assert.True(service.TryUndo(configuration, favoriteAction, out var favoriteUndo));
        Assert.False(b.IsFavorite);
        favoriteUndo!.Rollback();
        Assert.True(b.IsFavorite);
    }

    [Fact]
    public void NestedGroups_EnforceConfiguredDepthAndRejectCyclesWithoutPartialMutation()
    {
        var configuration = CreateConfiguration();
        var root = service.CreateGroup(configuration, "Root", null, out _);
        var child = service.CreateGroup(
            configuration,
            "Child",
            root.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);

        Assert.Equal(1, configuration.MaxNestedGroupDepth);
        Assert.Equal(1, service.GetActualMaxNestedGroupDepth(configuration));
        Assert.Throws<InvalidOperationException>(() => service.CreateGroup(
            configuration,
            "Too deep",
            child.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _));
        Assert.Equal(2, configuration.AssetGroups.Count);

        var depthUndo = Assert.IsType<UndoAction>(
            service.SetMaxNestedGroupDepth(configuration, 2));
        var grandchild = service.CreateGroup(
            configuration,
            "Grandchild",
            child.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);

        Assert.Equal(2, service.GetActualMaxNestedGroupDepth(configuration));
        Assert.Throws<InvalidOperationException>(() =>
            service.SetMaxNestedGroupDepth(configuration, 1));
        Assert.Equal(2, configuration.MaxNestedGroupDepth);
        Assert.Throws<InvalidOperationException>(() =>
            service.MoveGroup(configuration, root.Id, grandchild.Id));
        Assert.Null(root.ParentGroupId);

        Assert.False(service.TryUndo(configuration, depthUndo, out _));
        Assert.Equal(2, configuration.MaxNestedGroupDepth);
    }

    [Fact]
    public void NestedGroupState_RecursesThroughEveryLeafAndUndoRestoresExactDefaults()
    {
        var configuration = CreateConfiguration();
        var root = service.CreateGroup(configuration, "Root", null, out _);
        var child = service.CreateGroup(
            configuration,
            "Child",
            root.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);
        var rootLeaf = new Asset("Root leaf")
        {
            Id = "root-leaf",
            State = AddonState.Disabled
        };
        var childLeaf = new Asset("Child leaf")
        {
            Id = "child-leaf",
            State = AddonState.Excluded
        };
        configuration.Assets.AddRange([rootLeaf, childLeaf]);
        service.MoveAsset(configuration, rootLeaf.Id, root.Id);
        service.MoveAsset(configuration, childLeaf.Id, child.Id);
        root.DefaultChildState = AddonState.Enabled;
        child.DefaultChildState = AddonState.Excluded;

        var action = Assert.IsType<UndoAction>(
            service.ApplyGroupState(configuration, root.Id, AddonState.Enabled));

        Assert.Equal(AddonState.Enabled, root.DefaultChildState);
        Assert.Equal(AddonState.Enabled, child.DefaultChildState);
        Assert.Equal(AddonState.Enabled, rootLeaf.State);
        Assert.Equal(AddonState.Enabled, childLeaf.State);
        Assert.Equal(AssetGroupDisplayState.Enabled,
            service.GetDisplayState(configuration, root.Id));

        Assert.True(service.TryUndo(configuration, action, out var mutation));
        Assert.True(mutation!.RequiresRuntimeReconcile);
        Assert.Equal(AddonState.Enabled, root.DefaultChildState);
        Assert.Equal(AddonState.Excluded, child.DefaultChildState);
        Assert.Equal(AddonState.Disabled, rootLeaf.State);
        Assert.Equal(AddonState.Excluded, childLeaf.State);
        Assert.Equal(AssetGroupDisplayState.Mixed,
            service.GetDisplayState(configuration, root.Id));

        mutation.Rollback();
        Assert.Equal(AddonState.Enabled, child.DefaultChildState);
        Assert.All([rootLeaf, childLeaf], leaf =>
            Assert.Equal(AddonState.Enabled, leaf.State));
    }

    [Fact]
    public void NestedContainer_ReordersAssetsAndGroupsInsideTheSameFavoriteBands()
    {
        var configuration = CreateConfiguration();
        var parent = service.CreateGroup(configuration, "Parent", null, out _);
        var childGroup = service.CreateGroup(configuration, "Child group", null, out _);
        var first = new Asset("First") { Id = "first", IsFavorite = true };
        var second = new Asset("Second") { Id = "second", IsFavorite = true };
        configuration.Assets.AddRange([first, second]);
        service.MoveAsset(configuration, first.Id, parent.Id);
        service.MoveAsset(configuration, second.Id, parent.Id);
        service.MoveGroup(configuration, childGroup.Id, parent.Id);
        service.SetFavorite(
            configuration,
            AssetListEntryKind.Group,
            childGroup.Id,
            isFavorite: true);

        service.ReorderEntry(
            configuration,
            AssetListEntryKind.Group,
            childGroup.Id,
            targetIndex: 1,
            parent.Id);

        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, childGroup.SortOrder);
        Assert.Equal(2, second.SortOrder);
        Assert.Equal(parent.Id, childGroup.ParentGroupId);
        Assert.DoesNotContain(
            service.GetOrderedChildren(configuration, parent.Id),
            asset => !asset.IsFavorite);
    }

    [Fact]
    public void DeleteNestedGroup_KeepAssetsPromotesDirectEntriesAndOneUndoRestoresTree()
    {
        var configuration = CreateConfiguration();
        configuration.MaxNestedGroupDepth = 2;
        var parent = service.CreateGroup(configuration, "Parent", null, out _);
        var child = service.CreateGroup(
            configuration,
            "Child",
            parent.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);
        var grandchild = service.CreateGroup(
            configuration,
            "Grandchild",
            child.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);
        var directLeaf = new Asset("Direct") { Id = "direct", SortOrder = 7 };
        var nestedLeaf = new Asset("Nested") { Id = "nested", SortOrder = 9 };
        configuration.Assets.AddRange([directLeaf, nestedLeaf]);
        service.MoveAsset(configuration, directLeaf.Id, parent.Id);
        service.MoveAsset(configuration, nestedLeaf.Id, grandchild.Id);
        var directOrder = directLeaf.SortOrder;
        var childOrder = child.SortOrder;

        var action = service.DeleteGroup(
            configuration,
            parent.Id,
            AssetGroupDeleteMode.KeepAssets);

        Assert.DoesNotContain(parent, configuration.AssetGroups);
        Assert.Null(directLeaf.ParentGroupId);
        Assert.Null(child.ParentGroupId);
        Assert.Equal(child.Id, grandchild.ParentGroupId);
        Assert.Equal(grandchild.Id, nestedLeaf.ParentGroupId);

        Assert.True(service.TryUndo(configuration, action, out var mutation));
        Assert.Same(parent, mutation!.RestoredGroup);
        Assert.Equal(parent.Id, directLeaf.ParentGroupId);
        Assert.Equal(parent.Id, child.ParentGroupId);
        Assert.Equal(child.Id, grandchild.ParentGroupId);
        Assert.Equal(directOrder, directLeaf.SortOrder);
        Assert.Equal(childOrder, child.SortOrder);

        mutation.Rollback();
        Assert.DoesNotContain(parent, configuration.AssetGroups);
        Assert.Null(directLeaf.ParentGroupId);
        Assert.Null(child.ParentGroupId);
        Assert.Equal(child.Id, grandchild.ParentGroupId);
    }

    [Fact]
    public void DeleteNestedGroup_DeleteAssetsRemovesAndRestoresWholeSubtreeAtomically()
    {
        var configuration = CreateConfiguration();
        configuration.MaxNestedGroupDepth = 2;
        var root = service.CreateGroup(configuration, "Root", null, out _);
        var child = service.CreateGroup(
            configuration,
            "Child",
            root.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);
        var grandchild = service.CreateGroup(
            configuration,
            "Grandchild",
            child.Id,
            memberAssetIds: null,
            childGroupIds: null,
            out _);
        var rootLeaf = new Asset("Root leaf") { Id = "root-leaf" };
        var childLeaf = new Asset("Child leaf") { Id = "child-leaf" };
        var grandchildLeaf = new Asset("Grandchild leaf") { Id = "grandchild-leaf" };
        configuration.Assets.AddRange([rootLeaf, childLeaf, grandchildLeaf]);
        service.MoveAsset(configuration, rootLeaf.Id, root.Id);
        service.MoveAsset(configuration, childLeaf.Id, child.Id);
        service.MoveAsset(configuration, grandchildLeaf.Id, grandchild.Id);

        var action = service.DeleteGroup(
            configuration,
            root.Id,
            AssetGroupDeleteMode.DeleteAssets);

        Assert.DoesNotContain(configuration.AssetGroups, group =>
            group.Id == root.Id || group.Id == child.Id || group.Id == grandchild.Id);
        Assert.DoesNotContain(configuration.Assets, asset =>
            asset.Id == rootLeaf.Id ||
            asset.Id == childLeaf.Id ||
            asset.Id == grandchildLeaf.Id);
        Assert.Equal(3, action.DeletedAssetGroups!.Count);
        Assert.Equal(3, action.DeletedAssets!.Count);

        Assert.True(service.TryUndo(configuration, action, out var mutation));
        Assert.True(mutation!.RequiresRuntimeReconcile);
        Assert.Contains(root, configuration.AssetGroups);
        Assert.Contains(child, configuration.AssetGroups);
        Assert.Contains(grandchild, configuration.AssetGroups);
        Assert.Contains(rootLeaf, configuration.Assets);
        Assert.Contains(childLeaf, configuration.Assets);
        Assert.Contains(grandchildLeaf, configuration.Assets);
        Assert.Null(root.ParentGroupId);
        Assert.Equal(root.Id, child.ParentGroupId);
        Assert.Equal(child.Id, grandchild.ParentGroupId);

        mutation.Rollback();
        Assert.DoesNotContain(root, configuration.AssetGroups);
        Assert.DoesNotContain(rootLeaf, configuration.Assets);
    }

    [Fact]
    public void AssetAndGroupMemo_NormalizeNullAndUndoExactPreviousValue()
    {
        var configuration = CreateConfiguration();
        var asset = new Asset("Asset") { Id = "asset", Memo = "before asset" };
        configuration.Assets.Add(asset);
        var group = service.CreateGroup(configuration, "Group", null, out _);
        group.Memo = "before group";

        var assetAction = Assert.IsType<UndoAction>(
            service.SetAssetMemo(configuration, asset.Id, null));
        var groupAction = Assert.IsType<UndoAction>(
            service.SetGroupMemo(configuration, group.Id, "after group"));

        Assert.Equal(string.Empty, asset.Memo);
        Assert.Equal("after group", group.Memo);
        Assert.Throws<InvalidOperationException>(() => service.SetAssetMemo(
            configuration,
            SystemAssetDefinitions.SubscribeId,
            "forbidden"));

        Assert.True(service.TryUndo(configuration, groupAction, out _));
        Assert.Equal("before group", group.Memo);
        Assert.True(service.TryUndo(configuration, assetAction, out _));
        Assert.Equal("before asset", asset.Memo);
    }

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration();
        configuration.CreateDefaultAssets();
        return configuration;
    }
}
