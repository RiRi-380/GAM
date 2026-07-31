using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SubscriptionAuthorityIntegrationTests : IDisposable
{
    private const string AddonId = "100";
    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string manifestPath;

    public SubscriptionAuthorityIntegrationTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-subscription-authority-tests-" + Guid.NewGuid().ToString("N"));
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
        manifestPath = WorkshopManifestTestData.Write(rootPath, AddonId);
    }

    [Fact]
    public async Task MalformedManifest_DoesNotRemoveExistingCustomAssetMembership()
    {
        using var manager = new AddonManager(new AddonManagerOptions
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
        await manager.InitializeAsync();

        var asset = new Asset("Keep Membership")
        {
            Addons = [AddonId]
        };
        asset.SetWholeState(AddonState.Enabled);
        manager.GetConfiguration().Assets.Add(asset);
        await manager.SaveConfigurationImmediatelyAsync();

        File.WriteAllText(
            manifestPath,
            "\"AppWorkshop\"\n" +
            "{\n" +
            "  \"WorkshopItemDetails\" { garbage }\n" +
            "  \"WorkshopItemsInstalled\" { }\n" +
            "}\n");

        await manager.ScanWorkshopFolderAsync();

        Assert.Equal([AddonId], asset.Addons);
        Assert.Contains(
            AddonId,
            manager.GetConfiguration().KnownSubscribedAddonIds);
    }

    [Fact]
    public async Task MalformedManifest_DoesNotRemoveUnreferencedMetadataWhenPayloadIsUnavailable()
    {
        using var manager = new AddonManager(new AddonManagerOptions
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
        await manager.InitializeAsync();

        manager.GetConfiguration().AddonMetadata[AddonId] = new WorkshopAddon(
            AddonId,
            Path.Combine(workshopPath, AddonId))
        {
            Title = "Preserve until Steam authority recovers",
            IsAvailable = false
        };
        await manager.SaveConfigurationImmediatelyAsync();

        File.WriteAllText(
            manifestPath,
            "\"AppWorkshop\"\n" +
            "{\n" +
            "  \"WorkshopItemDetails\" { garbage }\n" +
            "  \"WorkshopItemsInstalled\" { }\n" +
            "}\n");

        await manager.ScanWorkshopFolderAsync();

        Assert.Contains(AddonId, manager.GetConfiguration().AddonMetadata.Keys);
        Assert.Contains(
            AddonId,
            manager.GetConfiguration().KnownSubscribedAddonIds);
    }

    [Fact]
    public void InvalidCustomWorkshopPathIsNotAdoptedByAddonManager()
    {
        var invalidWorkshopPath = Path.Combine(rootPath, "missing-workshop-root");

        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = invalidWorkshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft
        });

        Assert.False(PathOverrideResolver.IsDirectoryUsable(invalidWorkshopPath));
        Assert.NotEqual(
            Path.GetFullPath(invalidWorkshopPath),
            Path.GetFullPath(manager.GetWorkshopPath()));
    }

    [Fact]
    public async Task MalformedManifest_DoesNotDeleteStaleWorkshopIconCache()
    {
        var iconsPath = Path.Combine(appDataPath, "icons");
        Directory.CreateDirectory(iconsPath);
        var iconPath = Path.Combine(iconsPath, AddonId + ".png");
        File.WriteAllBytes(iconPath, [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(iconsPath, "index.json"),
            $"{{\"{AddonId}\":0}}");
        File.WriteAllText(
            manifestPath,
            "\"AppWorkshop\"\n" +
            "{\n" +
            "  \"WorkshopItemDetails\" { garbage }\n" +
            "  \"WorkshopItemsInstalled\" { }\n" +
            "}\n");

        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        });
        await manager.InitializeAsync();

        Assert.True(File.Exists(iconPath));
    }

    [Fact]
    public async Task SubscribedButNotInstalledAddon_RemainsInMetadataAndCustomAsset()
    {
        File.WriteAllText(
            manifestPath,
            "\"AppWorkshop\"\n" +
            "{\n" +
            "  \"WorkshopItemDetails\"\n" +
            "  {\n" +
            $"    \"{AddonId}\" {{ \"subscribedby\" \"76561198000000000\" }}\n" +
            "  }\n" +
            "  \"WorkshopItemsInstalled\" { }\n" +
            "}\n");

        using var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        });
        await manager.InitializeAsync();

        var custom = new Asset("Pending download") { Addons = [AddonId] };
        manager.GetConfiguration().Assets.Add(custom);
        manager.GetConfiguration().AddonMetadata[AddonId] = new WorkshopAddon(
            AddonId,
            Path.Combine(workshopPath, AddonId))
        {
            Title = "Pending download",
            IsAvailable = false,
            IsDownloadPending = true
        };
        await manager.SaveConfigurationImmediatelyAsync();

        var visible = await manager.ScanWorkshopFolderAsync();

        Assert.Empty(visible);
        Assert.Equal(1, manager.PendingDownloadCount);
        Assert.Contains(AddonId, custom.Addons);
        Assert.Contains(AddonId, manager.GetConfiguration().AddonMetadata.Keys);
        Assert.Contains(
            AddonId,
            manager.GetConfiguration().KnownSubscribedAddonIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
