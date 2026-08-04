using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class GamAssetBundleManagerIntegrationTests : IDisposable
{
    private const string WeaponAddonId = "100";
    private const string MapAddonId = "200";
    private const string MissingAddonId = "999";

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-bundle-manager-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;

    public GamAssetBundleManagerIntegrationTests()
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
        WriteWorkshopPayload(WeaponAddonId, "weapon", ["Fun"]);
        WriteWorkshopPayload(MapAddonId, "map", ["Scenic"]);
        manifestPath = WorkshopManifestTestData.Write(
            rootPath,
            WeaponAddonId,
            MapAddonId);
    }

    [Fact]
    public async Task MixedSelection_RoundTripsWithFreshHierarchyAndOneUndoWithoutSteamMutation()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();

        var fixedChild = await manager.CreateAssetAsync("Fixed Child");
        fixedChild.Addons = [MissingAddonId, WeaponAddonId];
        fixedChild.SetWholeState(AddonState.Disabled);

        var smartChild = await manager.CreateSmartAssetAsync(
            "Smart Child",
            new AssetMembershipRule(AssetMembershipRuleKind.Type, "Weapon"));
        smartChild.SetWholeState(AddonState.Excluded);

        var looseAsset = await manager.CreateAssetAsync("Loose Asset");
        looseAsset.Addons = [MapAddonId];
        looseAsset.SetWholeState(AddonState.Enabled);

        var sourceGroup = await manager.CreateAssetGroupAsync(
            "Bundle Group",
            [fixedChild.Id, smartChild.Id]);
        sourceGroup.DefaultChildState = AddonState.Excluded;
        smartChild.SortOrder = 3;
        fixedChild.SortOrder = 19;
        await manager.SaveConfigurationImmediatelyAsync();
        manager.GetUndoManager().Clear();

        var sourceAssetIds = new HashSet<string>(
            [fixedChild.Id, smartChild.Id, looseAsset.Id],
            StringComparer.Ordinal);
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var exportPath = Path.Combine(rootPath, "mixed-selection.gam");

        // fixedChild is selected both directly and through the Group. It must
        // still occur only once in the portable bundle.
        await manager.ExportGamSelectionAsync(
            [fixedChild.Id, looseAsset.Id],
            [sourceGroup.Id],
            exportPath,
            includeImages: false);

        var exported = await new GamAssetFileService().ReadAnyAsync(exportPath);
        Assert.Equal(GamAssetFileContentKind.Bundle, exported.Kind);
        Assert.Equal(4, exported.SourceFormatVersion);
        var bundle = Assert.IsType<GamAssetBundleDocument>(exported.Bundle);
        Assert.Equal(3, bundle.Assets.Count);
        var portableGroup = Assert.Single(bundle.Groups);
        Assert.Equal("Bundle Group", portableGroup.Name);
        Assert.Equal(GamAssetDocumentState.Excluded, portableGroup.DefaultChildState);
        Assert.Equal(
            ["Smart Child", "Fixed Child"],
            portableGroup.ChildAssetLocalIds
                .Select(localId => bundle.Assets.Single(asset => asset.LocalId == localId).Name));

        var portableFixed = bundle.Assets.Single(asset => asset.Name == "Fixed Child");
        Assert.Equal(GamAssetDocumentState.Disabled, portableFixed.State);
        Assert.Equal(GamAssetDocumentMembershipKind.Fixed, portableFixed.Membership.Kind);
        Assert.Equal(
            [WeaponAddonId, MissingAddonId],
            portableFixed.Membership.AddonIds);

        var portableSmart = bundle.Assets.Single(asset => asset.Name == "Smart Child");
        Assert.Equal(GamAssetDocumentState.Excluded, portableSmart.State);
        Assert.Equal(GamAssetDocumentMembershipKind.Smart, portableSmart.Membership.Kind);
        Assert.Equal(GamAssetDocumentRuleKind.Type, portableSmart.Membership.Rule!.Kind);
        Assert.Equal("Weapon", portableSmart.Membership.Rule.Value);

        var preview = await manager.PreviewGamFileImportAsync(exportPath);
        Assert.True(preview.IsBundle);
        Assert.Equal(3, preview.AssetCount);
        Assert.Equal(1, preview.GroupCount);
        Assert.Equal(0, preview.ImageCount);
        Assert.True(preview.SubscriptionStatusKnown);
        Assert.Equal([MissingAddonId], preview.MissingSubscriptionAddonIds);

        var imported = await manager.ImportGamFileAsync(preview);

        Assert.True(imported.IsBundle);
        Assert.Equal(3, imported.Assets.Count);
        var importedGroup = Assert.Single(imported.Groups);
        Assert.Equal("Bundle Group (2)", importedGroup.Name);
        Assert.NotEqual(sourceGroup.Id, importedGroup.Id);
        Assert.Equal(AddonState.Excluded, importedGroup.DefaultChildState);
        Assert.False(importedGroup.IsFavorite);

        Assert.All(
            imported.Assets,
            asset =>
            {
                Assert.DoesNotContain(asset.Id, sourceAssetIds);
                Assert.DoesNotContain(
                    bundle.Assets,
                    portable => portable.LocalId == asset.Id);
                Assert.False(asset.IsFavorite);
            });

        var importedFixed = imported.Assets.Single(asset => asset.Name == "Fixed Child (2)");
        var importedSmart = imported.Assets.Single(asset => asset.Name == "Smart Child (2)");
        var importedLoose = imported.Assets.Single(asset => asset.Name == "Loose Asset (2)");
        Assert.Equal(AddonState.Disabled, importedFixed.State);
        Assert.Equal([WeaponAddonId, MissingAddonId], importedFixed.Addons);
        Assert.True(importedFixed.RetainMissingReferences);
        Assert.Equal(AddonState.Excluded, importedSmart.State);
        Assert.Equal(AssetMembershipRuleKind.Type, importedSmart.MembershipRule!.Kind);
        Assert.Equal("Weapon", importedSmart.MembershipRule.Value);
        Assert.Equal([WeaponAddonId], importedSmart.Addons);
        Assert.False(importedSmart.RetainMissingReferences);
        Assert.Equal(AddonState.Enabled, importedLoose.State);
        Assert.Equal([MapAddonId], importedLoose.Addons);
        Assert.Null(importedLoose.ParentGroupId);

        var importedChildren = manager.GetOrderedAssetGroupChildren(importedGroup.Id);
        Assert.Equal(
            [importedSmart.Id, importedFixed.Id],
            importedChildren.Select(asset => asset.Id));
        Assert.All(
            importedChildren,
            child => Assert.Equal(importedGroup.Id, child.ParentGroupId));
        Assert.Equal([0, 1], importedChildren.Select(child => child.SortOrder));
        Assert.True(importedLoose.SortOrder < importedGroup.SortOrder);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
        Assert.Equal(
            UndoActionType.GamBundleImported,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => imported.Assets.Any(candidate => candidate.Id == asset.Id));
        Assert.DoesNotContain(
            manager.GetConfiguration().AssetGroups,
            group => group.Id == importedGroup.Id);
        Assert.Contains(
            manager.GetConfiguration().Assets,
            asset => asset.Id == fixedChild.Id);
        Assert.Contains(
            manager.GetConfiguration().AssetGroups,
            group => group.Id == sourceGroup.Id);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task NestedSelection_ExportsWholeSubtreeOnceWithExactMixedOrderAndOptionalMemos()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.SetMaxNestedGroupDepthAsync(2);

        var first = await manager.CreateAssetAsync("First");
        var nested = await manager.CreateAssetAsync("Nested");
        var last = await manager.CreateAssetAsync("Last");
        first.Memo = "first memo";
        nested.Memo = "nested memo";
        last.Memo = "last memo";

        var root = await manager.CreateAssetGroupAsync(
            "Root Group",
            [first.Id, nested.Id, last.Id]);
        root.Memo = "root memo";
        var child = await manager.CreateAssetGroupAsync(
            "Child Group",
            root.Id,
            [nested.Id],
            childGroupIds: null);
        child.Memo = "child memo";
        first.SortOrder = 0;
        child.SortOrder = 1;
        last.SortOrder = 2;
        nested.SortOrder = 0;
        await manager.SaveConfigurationImmediatelyAsync();

        var path = Path.Combine(rootPath, "nested-export.gam");
        await manager.ExportGamSelectionAsync(
            [nested.Id],
            [root.Id, child.Id],
            path,
            includeImages: false,
            includeMemos: true);

        var exported = await new GamAssetFileService().ReadAnyAsync(path);
        var bundle = Assert.IsType<GamAssetBundleDocument>(exported.Bundle);
        Assert.Equal(4, exported.SourceFormatVersion);
        Assert.Equal(3, bundle.Assets.Count);
        Assert.Equal(2, bundle.Groups.Count);
        var rootReference = Assert.Single(bundle.RootChildren);
        Assert.Equal(GamAssetBundleEntryKind.Group, rootReference.Kind);

        var portableRoot = bundle.Groups.Single(group => group.Name == "Root Group");
        var portableChild = bundle.Groups.Single(group => group.Name == "Child Group");
        Assert.Equal("root memo", portableRoot.Memo);
        Assert.Equal("child memo", portableChild.Memo);
        Assert.Equal(
            ["First", "Child Group", "Last"],
            portableRoot.Children.Select(reference => ResolvePortableName(bundle, reference)));
        Assert.Equal(
            ["Nested"],
            portableChild.Children.Select(reference => ResolvePortableName(bundle, reference)));
        Assert.Equal(
            "nested memo",
            bundle.Assets.Single(asset => asset.Name == "Nested").Memo);

        var noMemoPath = Path.Combine(rootPath, "nested-export-no-memo.gam");
        await manager.ExportGamSelectionAsync(
            Array.Empty<string>(),
            [root.Id],
            noMemoPath,
            includeImages: false,
            includeMemos: false);
        var withoutMemos = (await new GamAssetFileService().ReadAnyAsync(noMemoPath)).Bundle!;
        Assert.All(withoutMemos.Assets, asset => Assert.Null(asset.Memo));
        Assert.All(withoutMemos.Groups, group => Assert.Null(group.Memo));
    }

    [Fact]
    public async Task DeepBundleImport_RaisesConfiguredDepthAtomicallyAndUndoRestoresIt()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        Assert.Equal(1, manager.GetConfiguration().MaxNestedGroupDepth);

        var path = Path.Combine(rootPath, "deep-import.gam");
        await new GamAssetFileService().WriteBundleAsync(
            path,
            new GamAssetBundleDocument(
                [
                    new GamAssetBundleAsset(
                        "asset-root",
                        "Root Asset",
                        GamAssetDocumentState.Enabled,
                        GamAssetDocumentMembership.Fixed([WeaponAddonId]),
                        memo: "root asset memo"),
                    new GamAssetBundleAsset(
                        "asset-deep",
                        "Deep Asset",
                        GamAssetDocumentState.Disabled,
                        GamAssetDocumentMembership.Fixed([MapAddonId]),
                        memo: "deep asset memo")
                ],
                [
                    new GamAssetBundleGroup(
                        "group-root",
                        "Imported Root",
                        GamAssetDocumentState.Enabled,
                        [
                            GamAssetBundleEntryReference.Asset("asset-root"),
                            GamAssetBundleEntryReference.Group("group-child")
                        ],
                        memo: "root group memo"),
                    new GamAssetBundleGroup(
                        "group-child",
                        "Imported Child",
                        GamAssetDocumentState.Disabled,
                        [GamAssetBundleEntryReference.Group("group-grandchild")],
                        memo: "child group memo"),
                    new GamAssetBundleGroup(
                        "group-grandchild",
                        "Imported Grandchild",
                        GamAssetDocumentState.Excluded,
                        [GamAssetBundleEntryReference.Asset("asset-deep")],
                        memo: "grandchild group memo")
                ],
                [GamAssetBundleEntryReference.Group("group-root")]));

        var preview = await manager.PreviewGamFileImportAsync(path);
        Assert.Equal(2, preview.RequiredNestedGroupDepth);
        var blockedConfigPath = BlockConfigurationSave(manager);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => manager.ImportGamFileAsync(preview));
            Assert.Equal(1, manager.GetConfiguration().MaxNestedGroupDepth);
            Assert.DoesNotContain(
                manager.GetConfiguration().AssetGroups,
                group => group.Name == "Imported Root");
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, blockedConfigPath);
        }
        manager.GetUndoManager().Clear();

        var imported = await manager.ImportGamFileAsync(preview);

        Assert.Equal(2, manager.GetConfiguration().MaxNestedGroupDepth);
        var importedRoot = imported.Groups.Single(group => group.Name == "Imported Root");
        var importedChild = imported.Groups.Single(group => group.Name == "Imported Child");
        var importedGrandchild = imported.Groups.Single(group => group.Name == "Imported Grandchild");
        var rootAsset = imported.Assets.Single(asset => asset.Name == "Root Asset");
        var deepAsset = imported.Assets.Single(asset => asset.Name == "Deep Asset");
        Assert.Null(importedRoot.ParentGroupId);
        Assert.Equal(importedRoot.Id, importedChild.ParentGroupId);
        Assert.Equal(importedChild.Id, importedGrandchild.ParentGroupId);
        Assert.Equal(importedRoot.Id, rootAsset.ParentGroupId);
        Assert.Equal(importedGrandchild.Id, deepAsset.ParentGroupId);
        Assert.Equal("root group memo", importedRoot.Memo);
        Assert.Equal("deep asset memo", deepAsset.Memo);
        Assert.Equal(
            ["Root Asset", "Imported Child"],
            manager.GetConfiguration().Assets
                .Where(asset => asset.ParentGroupId == importedRoot.Id)
                .Select(asset => (Name: asset.Name, Order: asset.SortOrder))
                .Concat(manager.GetConfiguration().AssetGroups
                    .Where(group => group.ParentGroupId == importedRoot.Id)
                    .Select(group => (Name: group.Name, Order: group.SortOrder)))
                .OrderBy(entry => entry.Order)
                .Select(entry => entry.Name));

        Assert.True(await manager.UndoLastActionAsync());
        Assert.Equal(1, manager.GetConfiguration().MaxNestedGroupDepth);
        Assert.DoesNotContain(
            manager.GetConfiguration().AssetGroups,
            group => imported.Groups.Any(candidate => candidate.Id == group.Id));
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => imported.Assets.Any(candidate => candidate.Id == asset.Id));
    }

    [Fact]
    public async Task SelectionFormatAndImages_AreOptInAndBundleUndoRemovesManagedCopies()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();

        var leaf = await manager.CreateAssetAsync("Image Leaf");
        leaf.Addons = [WeaponAddonId];
        WriteManagedSourceImage(leaf, "leaf-source.png", SKColors.CornflowerBlue);

        var child = await manager.CreateAssetAsync("Image Child");
        child.Addons = [MapAddonId];
        WriteManagedSourceImage(child, "child-source.png", SKColors.MediumPurple);
        var group = await manager.CreateAssetGroupAsync("Image Group", [child.Id]);
        WriteManagedSourceImage(group, "group-source.png", SKColors.SeaGreen);
        await manager.SaveConfigurationImmediatelyAsync();

        var singleWithoutImagesPath = Path.Combine(rootPath, "single-no-image.gam");
        await manager.ExportGamSelectionAsync(
            [leaf.Id],
            Array.Empty<string>(),
            singleWithoutImagesPath,
            includeImages: false);
        var singleWithoutImages = await new GamAssetFileService().ReadAnyAsync(
            singleWithoutImagesPath);
        Assert.Equal(GamAssetFileContentKind.SingleAsset, singleWithoutImages.Kind);
        Assert.Equal(3, singleWithoutImages.SourceFormatVersion);
        Assert.Null(singleWithoutImages.SingleAsset!.ImageBytes);

        var singleWithImagesPath = Path.Combine(rootPath, "single-with-image.gam");
        await manager.ExportGamSelectionAsync(
            [leaf.Id],
            Array.Empty<string>(),
            singleWithImagesPath,
            includeImages: true);
        var singleWithImages = await new GamAssetFileService().ReadAnyAsync(
            singleWithImagesPath);
        Assert.Equal(GamAssetFileContentKind.SingleAsset, singleWithImages.Kind);
        Assert.Equal(3, singleWithImages.SourceFormatVersion);
        Assert.NotNull(singleWithImages.SingleAsset!.ImageBytes);

        var bundleWithoutImagesPath = Path.Combine(rootPath, "bundle-no-images.gam");
        await manager.ExportGamSelectionAsync(
            Array.Empty<string>(),
            [group.Id],
            bundleWithoutImagesPath,
            includeImages: false);
        var bundleWithoutImages = await new GamAssetFileService().ReadAnyAsync(
            bundleWithoutImagesPath);
        Assert.Equal(GamAssetFileContentKind.Bundle, bundleWithoutImages.Kind);
        Assert.Null(Assert.Single(bundleWithoutImages.Bundle!.Assets).ImageBytes);
        Assert.Null(Assert.Single(bundleWithoutImages.Bundle.Groups).ImageBytes);

        var bundleWithImagesPath = Path.Combine(rootPath, "bundle-with-images.gam");
        await manager.ExportGamSelectionAsync(
            Array.Empty<string>(),
            [group.Id],
            bundleWithImagesPath,
            includeImages: true);
        var preview = await manager.PreviewGamFileImportAsync(bundleWithImagesPath);
        Assert.True(preview.IsBundle);
        Assert.Equal(2, preview.ImageCount);

        manager.GetUndoManager().Clear();
        var imported = await manager.ImportGamFileAsync(preview);
        var importedAsset = Assert.Single(imported.Assets);
        var importedGroup = Assert.Single(imported.Groups);
        var importedAssetImagePath = manager.ResolveAssetImagePath(importedAsset);
        var importedGroupImagePath = manager.ResolveAssetGroupImagePath(importedGroup);
        Assert.NotNull(importedAssetImagePath);
        Assert.NotNull(importedGroupImagePath);
        Assert.True(File.Exists(importedAssetImagePath));
        Assert.True(File.Exists(importedGroupImagePath));
        Assert.NotEqual(manager.ResolveAssetImagePath(child), importedAssetImagePath);
        Assert.NotEqual(manager.ResolveAssetGroupImagePath(group), importedGroupImagePath);
        Assert.Equal(
            UndoActionType.GamBundleImported,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Id == importedAsset.Id);
        Assert.DoesNotContain(
            manager.GetConfiguration().AssetGroups,
            candidate => candidate.Id == importedGroup.Id);
        Assert.False(File.Exists(importedAssetImagePath));
        Assert.False(File.Exists(importedGroupImagePath));
        Assert.True(File.Exists(manager.ResolveAssetImagePath(child)));
        Assert.True(File.Exists(manager.ResolveAssetGroupImagePath(group)));
    }

    [Fact]
    public async Task BundleImportSaveFailure_RestoresLiveProfileAndRemovesManagedImages()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var existingAsset = await manager.CreateAssetAsync("Existing Asset");
        var existingGroup = await manager.CreateAssetGroupAsync(
            "Existing Group",
            [existingAsset.Id]);
        manager.GetUndoManager().Clear();

        var configuration = manager.GetConfiguration();
        var liveAssets = configuration.Assets;
        var liveGroups = configuration.AssetGroups;
        var imageDirectory = Path.Combine(appDataPath, "asset-images");
        var imagesBefore = Directory.Exists(imageDirectory)
            ? Directory.GetFiles(imageDirectory).OrderBy(path => path).ToArray()
            : Array.Empty<string>();
        var manifestBefore = File.ReadAllBytes(manifestPath);

        var bundlePath = Path.Combine(rootPath, "save-failure-bundle.gam");
        var imageBytes = CreatePngBytes(SKColors.Goldenrod);
        await new GamAssetFileService().WriteBundleAsync(
            bundlePath,
            new GamAssetBundleDocument(
                [
                    new GamAssetBundleAsset(
                        "asset-1",
                        "Imported Asset",
                        GamAssetDocumentState.Disabled,
                        GamAssetDocumentMembership.Fixed([MissingAddonId]),
                        imageBytes)
                ],
                [
                    new GamAssetBundleGroup(
                        "group-1",
                        "Imported Group",
                        GamAssetDocumentState.Excluded,
                        ["asset-1"],
                        imageBytes)
                ]));
        var preview = await manager.PreviewGamFileImportAsync(bundlePath);
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.ImportGamFileAsync(preview));

            Assert.Same(configuration, manager.GetConfiguration());
            Assert.Same(liveAssets, configuration.Assets);
            Assert.Same(liveGroups, configuration.AssetGroups);
            Assert.Same(
                existingAsset,
                configuration.Assets.Single(asset => asset.Id == existingAsset.Id));
            Assert.Same(
                existingGroup,
                configuration.AssetGroups.Single(group => group.Id == existingGroup.Id));
            Assert.DoesNotContain(
                configuration.Assets,
                asset => asset.Name == "Imported Asset");
            Assert.DoesNotContain(
                configuration.AssetGroups,
                group => group.Name == "Imported Group");
            Assert.Equal(
                imagesBefore,
                Directory.Exists(imageDirectory)
                    ? Directory.GetFiles(imageDirectory).OrderBy(path => path).ToArray()
                    : Array.Empty<string>());
            Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task BundleUndoSaveFailure_KeepsEntitiesImagesAndUndoAvailable()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.GetUndoManager().Clear();

        var bundlePath = Path.Combine(rootPath, "undo-save-failure-bundle.gam");
        var imageBytes = CreatePngBytes(SKColors.Tomato);
        await new GamAssetFileService().WriteBundleAsync(
            bundlePath,
            new GamAssetBundleDocument(
                [
                    new GamAssetBundleAsset(
                        "asset-1",
                        "Undo Asset",
                        GamAssetDocumentState.Enabled,
                        GamAssetDocumentMembership.Fixed([WeaponAddonId]),
                        imageBytes)
                ],
                [
                    new GamAssetBundleGroup(
                        "group-1",
                        "Undo Group",
                        GamAssetDocumentState.Enabled,
                        ["asset-1"],
                        imageBytes)
                ]));
        var preview = await manager.PreviewGamFileImportAsync(bundlePath);
        var imported = await manager.ImportGamFileAsync(preview);
        var importedAsset = Assert.Single(imported.Assets);
        var importedGroup = Assert.Single(imported.Groups);
        var assetImagePath = manager.ResolveAssetImagePath(importedAsset);
        var groupImagePath = manager.ResolveAssetGroupImagePath(importedGroup);
        Assert.NotNull(assetImagePath);
        Assert.NotNull(groupImagePath);

        var configPath = BlockConfigurationSave(manager);
        try
        {
            Assert.False(await manager.UndoLastActionAsync());

            Assert.Same(
                importedAsset,
                manager.GetConfiguration().Assets.Single(asset =>
                    asset.Id == importedAsset.Id));
            Assert.Same(
                importedGroup,
                manager.GetConfiguration().AssetGroups.Single(group =>
                    group.Id == importedGroup.Id));
            Assert.True(File.Exists(assetImagePath));
            Assert.True(File.Exists(groupImagePath));
            Assert.True(manager.GetUndoManager().CanUndo);
            Assert.Equal(
                UndoActionType.GamBundleImported,
                manager.GetUndoManager().PeekLastAction()!.Type);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }

        Assert.True(await manager.UndoLastActionAsync());
        Assert.False(File.Exists(assetImagePath));
        Assert.False(File.Exists(groupImagePath));
    }

    [Fact]
    public async Task ProgrammaticInvalidBundlePreview_IsRejectedBeforeMutation()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.GetUndoManager().Clear();
        var configuration = manager.GetConfiguration();
        var assetIdsBefore = configuration.Assets.Select(asset => asset.Id).ToArray();
        var groupIdsBefore = configuration.AssetGroups.Select(group => group.Id).ToArray();
        var invalidBundle = new GamAssetBundleDocument(
            [
                new GamAssetBundleAsset(
                    "asset-1",
                    "Duplicate name",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed([WeaponAddonId]))
            ],
            [
                new GamAssetBundleGroup(
                    "group-1",
                    "Duplicate name",
                    GamAssetDocumentState.Enabled,
                    ["asset-1"])
            ]);
        var preview = new GamAssetFileImportPreview(
            GamAssetFileReadResult.FromBundle(invalidBundle),
            singleAssetPreview: null,
            subscriptionStatusKnown: true,
            referencedAddonIds: [WeaponAddonId],
            missingSubscriptionAddonIds: Array.Empty<string>());

        await Assert.ThrowsAsync<GamAssetDocumentException>(() =>
            manager.ImportGamFileAsync(preview));

        Assert.Equal(assetIdsBefore, configuration.Assets.Select(asset => asset.Id));
        Assert.Equal(groupIdsBefore, configuration.AssetGroups.Select(group => group.Id));
        Assert.False(manager.GetUndoManager().CanUndo);
    }

    [Fact]
    public async Task CollisionSuffixing_PreservesUnicodeTextElementsAndGlobalNamespace()
    {
        var requestedName = new string('A', 195) + "😀XYZ";
        var secondName = new string('A', 195) + " (2)";
        var expectedImportedName = new string('A', 195) + " (3)";
        string importedAssetId;
        string importedGroupId;

        using (var manager = CreateManager())
        {
            await manager.InitializeAsync();
            await manager.CreateAssetAsync(requestedName);
            await manager.CreateAssetGroupAsync(secondName, Array.Empty<string>());
            await manager.CreateAssetAsync("Cross-kind collision");

            var bundlePath = Path.Combine(rootPath, "unicode-collision.gam");
            await new GamAssetFileService().WriteBundleAsync(
                bundlePath,
                new GamAssetBundleDocument(
                    [
                        new GamAssetBundleAsset(
                            "asset-1",
                            requestedName,
                            GamAssetDocumentState.Enabled,
                            GamAssetDocumentMembership.Fixed([WeaponAddonId]))
                    ],
                    [
                        new GamAssetBundleGroup(
                            "group-1",
                            "Cross-kind collision",
                            GamAssetDocumentState.Enabled,
                            ["asset-1"])
                    ]));
            var preview = await manager.PreviewGamFileImportAsync(bundlePath);
            var imported = await manager.ImportGamFileAsync(preview);
            var importedAsset = Assert.Single(imported.Assets);
            var importedGroup = Assert.Single(imported.Groups);

            Assert.Equal(expectedImportedName, importedAsset.Name);
            Assert.Equal("Cross-kind collision (2)", importedGroup.Name);
            _ = new UTF8Encoding(false, true).GetBytes(importedAsset.Name);
            importedAssetId = importedAsset.Id;
            importedGroupId = importedGroup.Id;
        }

        using var reloaded = CreateManager();
        await reloaded.InitializeAsync();
        Assert.Equal(
            expectedImportedName,
            reloaded.GetConfiguration().Assets.Single(asset =>
                asset.Id == importedAssetId).Name);
        Assert.Equal(
            "Cross-kind collision (2)",
            reloaded.GetConfiguration().AssetGroups.Single(group =>
                group.Id == importedGroupId).Name);
    }

    [Fact]
    public async Task SmartBundleSaveFailure_RestoresExistingSmartAndMetadataIdentities()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var existingSmart = await manager.CreateSmartAssetAsync(
            "Existing Weapon Rule",
            new AssetMembershipRule(AssetMembershipRuleKind.Type, "Weapon"));
        Assert.Equal([WeaponAddonId], existingSmart.Addons);
        var configuration = manager.GetConfiguration();
        var existingMetadata = configuration.AddonMetadata[WeaponAddonId];
        Assert.Equal("weapon", existingMetadata.Type);

        WriteWorkshopPayload(WeaponAddonId, "map", ["Scenic"]);
        File.SetLastWriteTimeUtc(
            Path.Combine(workshopPath, WeaponAddonId, "addon.json"),
            DateTime.UtcNow.AddMinutes(1));

        var bundlePath = Path.Combine(rootPath, "smart-save-failure-bundle.gam");
        await new GamAssetFileService().WriteBundleAsync(
            bundlePath,
            new GamAssetBundleDocument(
                [
                    new GamAssetBundleAsset(
                        "asset-1",
                        "Imported Map Rule",
                        GamAssetDocumentState.Enabled,
                        GamAssetDocumentMembership.Smart(
                            new GamAssetDocumentRule(
                                GamAssetDocumentRuleKind.Type,
                                "Map")))
                ],
                Array.Empty<GamAssetBundleGroup>()));
        var preview = await manager.PreviewGamFileImportAsync(bundlePath);
        manager.GetUndoManager().Clear();
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.ImportGamFileAsync(preview));

            Assert.Same(
                existingSmart,
                configuration.Assets.Single(asset => asset.Id == existingSmart.Id));
            Assert.Same(
                existingMetadata,
                configuration.AddonMetadata[WeaponAddonId]);
            Assert.Equal([WeaponAddonId], existingSmart.Addons);
            Assert.Equal("weapon", existingMetadata.Type);
            Assert.DoesNotContain(
                configuration.Assets,
                asset => asset.Name == "Imported Map Rule");
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    private static string ResolvePortableName(
        GamAssetBundleDocument bundle,
        GamAssetBundleEntryReference reference)
    {
        return reference.Kind == GamAssetBundleEntryKind.Asset
            ? bundle.Assets.Single(asset => asset.LocalId == reference.LocalId).Name
            : bundle.Groups.Single(group => group.LocalId == reference.LocalId).Name;
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

    private void WriteWorkshopPayload(
        string addonId,
        string type,
        IEnumerable<string> tags)
    {
        var addonPath = Path.Combine(workshopPath, addonId);
        Directory.CreateDirectory(Path.Combine(addonPath, "lua"));
        File.WriteAllText(
            Path.Combine(addonPath, "lua", "payload.txt"),
            "payload",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(addonPath, "addon.json"),
            "{\"title\":\"Test addon\",\"type\":\"" + type +
            "\",\"tags\":[" +
            string.Join(",", tags.Select(tag => "\"" + tag + "\"")) +
            "]}",
            new UTF8Encoding(false));
    }

    private void WriteManagedSourceImage(
        Asset asset,
        string fileName,
        SKColor color)
    {
        asset.ImagePath = Path.Combine("asset-images", fileName);
        WritePng(Path.Combine(appDataPath, asset.ImagePath), color);
    }

    private void WriteManagedSourceImage(
        AssetGroup group,
        string fileName,
        SKColor color)
    {
        group.ImagePath = Path.Combine("asset-images", fileName);
        WritePng(Path.Combine(appDataPath, group.ImagePath), color);
    }

    private static void WritePng(string path, SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(
            24,
            24,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(data);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static byte[] CreatePngBytes(SKColor color)
    {
        using var bitmap = new SKBitmap(
            24,
            24,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(color);
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
