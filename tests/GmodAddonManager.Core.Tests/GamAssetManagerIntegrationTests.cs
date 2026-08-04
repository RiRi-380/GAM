using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class GamAssetManagerIntegrationTests : IDisposable
{
    private const string SubscribedAddonId = "100";
    private const string MissingAddonId = "999";

    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;

    public GamAssetManagerIntegrationTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-asset-manager-tests-" + Guid.NewGuid().ToString("N"));
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
        WriteWorkshopPayload(SubscribedAddonId, "weapon", ["Fun"]);
        manifestPath = WorkshopManifestTestData.Write(rootPath, SubscribedAddonId);
    }

    [Fact]
    public async Task LegacyPreviewAndImport_CreateNewEnabledAssetAndRetainMissingIds()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.CreateAssetAsync("Shared setup");
        var manifestBefore = File.ReadAllBytes(manifestPath);
        var legacyPath = Path.Combine(rootPath, "shared.gam");
        File.WriteAllText(
            legacyPath,
            "# GAM Collection Export v1\n" +
            "# Title: Shared setup\n" +
            "# Count: 2\n" +
            SubscribedAddonId + "\n" +
            MissingAddonId + "\n",
            new UTF8Encoding(false));

        var preview = await manager.PreviewGamAssetImportAsync(legacyPath);

        Assert.True(preview.IsLegacyV1);
        Assert.True(preview.SubscriptionStatusKnown);
        Assert.Equal("Shared setup (2)", preview.SuggestedAssetName);
        Assert.Equal([SubscribedAddonId, MissingAddonId], preview.ReferencedAddonIds);
        Assert.Equal([MissingAddonId], preview.MissingSubscriptionAddonIds);

        var imported = await manager.ImportGamAssetAsync(
            preview,
            preview.SuggestedAssetName);

        Assert.NotEqual(
            manager.GetConfiguration().Assets.Single(a => a.Name == "Shared setup").Id,
            imported.Id);
        Assert.Equal(AddonState.Enabled, imported.State);
        Assert.False(imported.IsSmart);
        Assert.True(imported.RetainMissingReferences);
        Assert.Equal([SubscribedAddonId, MissingAddonId], imported.Addons);
        Assert.Equal(1, imported.SortOrder);
        Assert.False(
            manager.GetConfiguration().AddonMetadata[MissingAddonId].IsAvailable);
        Assert.False(
            manager.GetConfiguration().AddonMetadata[MissingAddonId].IsDownloadPending);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task Import_AppendsWithinRootOrderWithoutUsingNestedGroupOrder()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.CreateAssetAsync("Existing root Asset");
        var rootGroup = await manager.CreateAssetGroupAsync(
            "Existing root Group",
            Array.Empty<string>());
        var nestedGroup = await manager.CreateAssetGroupAsync(
            "Nested Group",
            rootGroup.Id,
            memberAssetIds: null,
            childGroupIds: null);
        nestedGroup.SortOrder = 1000;
        await manager.SaveConfigurationImmediatelyAsync();

        var importPath = Path.Combine(rootPath, "root-order.gam");
        File.WriteAllText(
            importPath,
            "# GAM Collection Export v1\n" +
            "# Title: Imported root Asset\n" +
            "# Count: 1\n" +
            SubscribedAddonId + "\n",
            new UTF8Encoding(false));
        var preview = await manager.PreviewGamAssetImportAsync(importPath);

        var imported = await manager.ImportGamAssetAsync(
            preview,
            preview.SuggestedAssetName);

        Assert.Null(imported.ParentGroupId);
        Assert.Equal(2, imported.SortOrder);
    }

    [Fact]
    public async Task ExportFixedAsset_PreservesStateAndIncludesImageOnlyWhenRequested()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = new Asset("Excluded setup")
        {
            Addons = [MissingAddonId, SubscribedAddonId],
            ImagePath = Path.Combine("asset-images", "export-source.png")
        };
        asset.SetWholeState(AddonState.Excluded);
        manager.GetConfiguration().Assets.Add(asset);
        var imagePath = Path.Combine(appDataPath, asset.ImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, CreateOnePixelPng());
        await manager.SaveConfigurationImmediatelyAsync();

        var withoutImagePath = Path.Combine(rootPath, "without-image.gam");
        await manager.ExportAssetToGamFileAsync(
            asset.Id,
            withoutImagePath,
            includeImage: false);
        var withoutImage = await new GamAssetFileService().ReadAsync(withoutImagePath);

        Assert.Equal(GamAssetDocumentState.Excluded, withoutImage.State);
        Assert.Equal(
            [SubscribedAddonId, MissingAddonId],
            withoutImage.Membership.AddonIds);
        Assert.Null(withoutImage.ImageBytes);

        var withImagePath = Path.Combine(rootPath, "with-image.gam");
        await manager.ExportAssetToGamFileAsync(
            asset.Id,
            withImagePath,
            includeImage: true);
        var withImage = await new GamAssetFileService().ReadAsync(withImagePath);

        Assert.NotNull(withImage.ImageBytes);
        Assert.True(withImage.ImageBytes!.Length > 0);
    }

    [Fact]
    public async Task ImportSmartAsset_UsesCurrentRuleMatchesNotExportSnapshot()
    {
        WriteWorkshopPayload(SubscribedAddonId, "map", ["Scenic"]);
        using var manager = CreateManager();
        await manager.InitializeAsync();
        manager.InvalidateWorkshopScanCache();
        await manager.ScanWorkshopFolderAsync();
        var path = Path.Combine(rootPath, "smart.gam");
        var document = new GamAssetDocument(
            "Map automation",
            GamAssetDocumentState.Disabled,
            GamAssetDocumentMembership.Smart(
                new GamAssetDocumentRule(GamAssetDocumentRuleKind.Type, "Map"),
                [MissingAddonId]));
        await new GamAssetFileService().WriteAsync(path, document);

        var preview = await manager.PreviewGamAssetImportAsync(path);
        Assert.Equal([MissingAddonId], preview.ReferencedAddonIds);
        Assert.Empty(preview.MissingSubscriptionAddonIds);
        Assert.Equal(
            [MissingAddonId],
            preview.Document.Membership.SnapshotAddonIds);
        var imported = await manager.ImportGamAssetAsync(
            preview,
            preview.SuggestedAssetName);

        Assert.True(imported.IsSmart);
        Assert.Equal(AddonState.Disabled, imported.State);
        Assert.Equal(AssetMembershipRuleKind.Type, imported.MembershipRule!.Kind);
        Assert.Equal("Map", imported.MembershipRule.Value);
        Assert.Equal([SubscribedAddonId], imported.Addons);
        Assert.DoesNotContain(MissingAddonId, imported.Addons);
        Assert.False(imported.RetainMissingReferences);
    }

    [Fact]
    public async Task DuplicateImportName_IsRejectedWithoutProfileMutation()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.CreateAssetAsync("Already exists");
        var assetCountBefore = manager.GetConfiguration().Assets.Count;
        var path = Path.Combine(rootPath, "duplicate.gam");
        await new GamAssetFileService().WriteAsync(
            path,
            new GamAssetDocument(
                "Portable",
                GamAssetDocumentState.Enabled,
                GamAssetDocumentMembership.Fixed([SubscribedAddonId])));
        var preview = await manager.PreviewGamAssetImportAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ImportGamAssetAsync(preview, "Already exists"));

        Assert.Equal(assetCountBefore, manager.GetConfiguration().Assets.Count);
    }

    [Fact]
    public async Task ImportWithImage_IsOneUndoAndUndoRemovesItsManagedImage()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var path = Path.Combine(rootPath, "image-import.gam");
        await new GamAssetFileService().WriteAsync(
            path,
            new GamAssetDocument(
                "Image import",
                GamAssetDocumentState.Enabled,
                GamAssetDocumentMembership.Fixed([SubscribedAddonId]),
                CreateOnePixelPng()));
        var preview = await manager.PreviewGamAssetImportAsync(path);

        var imported = await manager.ImportGamAssetAsync(
            preview,
            preview.SuggestedAssetName);
        var imagePath = manager.ResolveAssetImagePath(imported);

        Assert.NotNull(imagePath);
        Assert.True(File.Exists(imagePath));
        Assert.Equal(
            UndoActionType.AssetCreated,
            manager.GetUndoManager().PeekLastAction()!.Type);

        Assert.True(await manager.UndoLastActionAsync());
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Id == imported.Id);
        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public async Task ImportSaveFailure_RestoresProfileWithoutReplacingConfiguration()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        await manager.CreateAssetAsync("Existing Asset");
        manager.GetUndoManager().Clear();
        var configuration = manager.GetConfiguration();
        var assetIdsBefore = configuration.Assets
            .Select(asset => asset.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var path = Path.Combine(rootPath, "save-failure.gam");
        await new GamAssetFileService().WriteAsync(
            path,
            new GamAssetDocument(
                "Will fail",
                GamAssetDocumentState.Enabled,
                GamAssetDocumentMembership.Fixed([MissingAddonId])));
        var preview = await manager.PreviewGamAssetImportAsync(path);
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.ImportGamAssetAsync(
                    preview,
                    preview.SuggestedAssetName));

            Assert.Same(configuration, manager.GetConfiguration());
            Assert.Equal(
                assetIdsBefore,
                configuration.Assets
                    .Select(asset => asset.Id)
                    .OrderBy(id => id, StringComparer.Ordinal));
            Assert.DoesNotContain(
                configuration.Assets,
                asset => asset.Name == "Will fail");
            Assert.False(manager.GetUndoManager().CanUndo);
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task SmartImportSaveFailure_RestoresExistingEntityIdentitiesAndMembership()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var existingSmart = await manager.CreateSmartAssetAsync(
            "Existing Weapon Rule",
            new AssetMembershipRule(AssetMembershipRuleKind.Type, "Weapon"));
        Assert.Equal([SubscribedAddonId], existingSmart.Addons);

        var configuration = manager.GetConfiguration();
        var assetCollection = configuration.Assets;
        var metadataCollection = configuration.AddonMetadata;
        var existingMetadata = configuration.AddonMetadata[SubscribedAddonId];
        Assert.Equal("weapon", existingMetadata.Type);

        WriteWorkshopPayload(SubscribedAddonId, "map", ["Scenic"]);
        File.SetLastWriteTimeUtc(
            Path.Combine(workshopPath, SubscribedAddonId, "addon.json"),
            DateTime.UtcNow.AddMinutes(1));

        var path = Path.Combine(rootPath, "smart-save-failure.gam");
        await new GamAssetFileService().WriteAsync(
            path,
            new GamAssetDocument(
                "Imported Map Rule",
                GamAssetDocumentState.Enabled,
                GamAssetDocumentMembership.Smart(
                    new GamAssetDocumentRule(GamAssetDocumentRuleKind.Type, "Map"))));
        var preview = await manager.PreviewGamAssetImportAsync(path);
        var configPath = BlockConfigurationSave(manager);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.ImportGamAssetAsync(preview, preview.SuggestedAssetName));

            Assert.Same(configuration, manager.GetConfiguration());
            Assert.Same(assetCollection, configuration.Assets);
            Assert.Same(metadataCollection, configuration.AddonMetadata);
            Assert.Same(
                existingSmart,
                configuration.Assets.Single(asset => asset.Id == existingSmart.Id));
            Assert.Same(
                existingMetadata,
                configuration.AddonMetadata[SubscribedAddonId]);
            Assert.Equal([SubscribedAddonId], existingSmart.Addons);
            Assert.Equal("weapon", existingMetadata.Type);
            Assert.DoesNotContain(
                configuration.Assets,
                asset => asset.Name == "Imported Map Rule");
        }
        finally
        {
            await UnblockConfigurationSaveAsync(manager, configPath);
        }
    }

    [Fact]
    public async Task AssetCreation_UsesTheSamePortableNameContractAsGamExport()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var tooLong = new string('A', GamAssetDocumentCodec.MaximumAssetNameLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateAssetAsync(tooLong));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateSmartAssetAsync(
                tooLong,
                new AssetMembershipRule(AssetMembershipRuleKind.Type, "Weapon")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateAssetAsync("Control\u0001Name"));

        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Name == tooLong || asset.Name.Contains('\u0001'));
    }

    [Theory]
    [InlineData("local_123")]
    [InlineData("*")]
    public async Task FixedExport_RejectsReferencesTheGamFormatCannotRepresent(
        string unsupportedReference)
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("Not portable");
        asset.Addons = [SubscribedAddonId, unsupportedReference];
        var path = Path.Combine(rootPath, "unsupported.gam");

        var exception = await Assert.ThrowsAsync<GamAssetDocumentException>(() =>
            manager.ExportAssetToGamFileAsync(
                asset.Id,
                path,
                includeImage: false));

        Assert.Contains("non-Workshop", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
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

    private static byte[] CreateOnePixelPng()
    {
        using var bitmap = new SKBitmap(
            8,
            8,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(data);
        return data.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
