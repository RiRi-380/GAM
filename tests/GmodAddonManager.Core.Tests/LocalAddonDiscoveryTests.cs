using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class LocalAddonDiscoveryTests : IDisposable
{
    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;
    private readonly string localAddonsPath;
    private readonly string manifestPath;

    public LocalAddonDiscoveryTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-local-discovery-tests-" + Guid.NewGuid().ToString("N"));
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
        localAddonsPath = Path.Combine(gmodRootPath, "garrysmod", "addons");

        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(localAddonsPath);
        manifestPath = WorkshopManifestTestData.Write(rootPath);
    }

    [Fact]
    public async Task Discovery_DefaultOff_HidesLocalAddonAndCreatesNoManagedRoot()
    {
        var localFolder = CreateLocalFolderAddon();
        using var manager = CreateManager(enableDiscovery: false);
        await manager.InitializeAsync();

        var inventory = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(inventory, addon => addon.IsLocal);
        Assert.True(Directory.Exists(localFolder));
        Assert.False(Directory.Exists(Path.Combine(appDataPath, "local-addons")));
    }

    [Fact]
    public async Task Discovery_OptInListsMountedPayloadWithoutMovingLinkingOrDeleting()
    {
        var localFolder = CreateLocalFolderAddon();
        var payloadPath = Path.Combine(localFolder, "lua", "payload.txt");
        var payloadBefore = await File.ReadAllBytesAsync(payloadPath);
        var legacyManagedPath = Path.Combine(appDataPath, "local-addons", "legacy");
        Directory.CreateDirectory(legacyManagedPath);
        var recoveryFile = Path.Combine(legacyManagedPath, "recovery.txt");
        await File.WriteAllTextAsync(recoveryFile, "preserve", Encoding.UTF8);
        using var manager = CreateManager(enableDiscovery: true);
        await manager.InitializeAsync();

        var inventory = await manager.ScanWorkshopFolderAsync();
        var local = Assert.Single(inventory, addon => addon.IsLocal);

        Assert.True(manager.EnableLocalAddonDiscovery);
        Assert.False(manager.EnableLocalAddonManagement);
        Assert.Equal("Local Test Addon", local.Title);
        Assert.Equal(Path.GetFullPath(localFolder), Path.GetFullPath(local.FolderPath));
        Assert.True(local.IsEnabled);
        Assert.Equal(payloadBefore, await File.ReadAllBytesAsync(payloadPath));
        Assert.True(File.Exists(recoveryFile));
        Assert.Equal("preserve", await File.ReadAllTextAsync(recoveryFile, Encoding.UTF8));
        Assert.DoesNotContain(
            manager.GetConfiguration().Assets,
            asset => asset.Addons.Contains(local.Id));
    }

    [Fact]
    public async Task Discovery_OptInPreservesDetectedLocalAddonReferencesDuringWorkshopReconciliation()
    {
        CreateLocalFolderAddon();
        using var manager = CreateManager(enableDiscovery: true);
        await manager.InitializeAsync();
        var firstInventory = await manager.ScanWorkshopFolderAsync();
        var local = Assert.Single(firstInventory, addon => addon.IsLocal);
        var custom = new Asset("Local profile")
        {
            Addons = [local.Id]
        };
        manager.GetConfiguration().Assets.Add(custom);
        manager.InvalidateWorkshopScanCache();

        var secondInventory = await manager.ScanWorkshopFolderAsync();

        Assert.Contains(secondInventory, addon => addon.Id == local.Id && addon.IsLocal);
        Assert.Contains(local.Id, custom.Addons);
    }

    [Fact]
    public async Task Discovery_DefaultOffDoesNotRetainAnUnobservedLocalReference()
    {
        CreateLocalFolderAddon();
        using var manager = CreateManager(enableDiscovery: false);
        await manager.InitializeAsync();
        var custom = new Asset("Unobserved local profile")
        {
            Addons = ["local_unobserved"]
        };
        manager.GetConfiguration().Assets.Add(custom);
        manager.InvalidateWorkshopScanCache();

        var inventory = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(inventory, addon => addon.IsLocal);
        Assert.DoesNotContain("local_unobserved", custom.Addons);
    }

    [Fact]
    public async Task Discovery_SuccessfulRescanClearsRemovedOrDeletedTypeAndTags()
    {
        var localFolder = CreateLocalFolderAddon();
        using var manager = CreateManager(enableDiscovery: true);
        await manager.InitializeAsync();

        var firstInventory = await manager.ScanWorkshopFolderAsync();
        var first = Assert.Single(firstInventory, addon => addon.IsLocal);
        Assert.Equal("weapon", first.Type);
        Assert.Equal(["Fun"], first.Tags);
        Assert.Equal(AddonClassificationMetadataStatus.Known, first.TypeMetadataStatus);
        Assert.Equal(AddonClassificationMetadataStatus.Known, first.TagsMetadataStatus);

        File.WriteAllText(
            Path.Combine(localFolder, "addon.json"),
            "{\"title\":\"Local Test Addon\"}",
            new UTF8Encoding(false));
        manager.InvalidateWorkshopScanCache();

        var secondInventory = await manager.ScanWorkshopFolderAsync();
        var second = Assert.Single(secondInventory, addon => addon.IsLocal);
        Assert.Empty(second.Type);
        Assert.Empty(second.Tags);
        Assert.Equal(AddonClassificationMetadataStatus.Known, second.TypeMetadataStatus);
        Assert.Equal(AddonClassificationMetadataStatus.Known, second.TagsMetadataStatus);

        File.WriteAllText(
            Path.Combine(localFolder, "addon.json"),
            "{\"title\":\"Local Test Addon\",\"type\":\"vehicle\",\"tags\":[\"Roleplay\"]}",
            new UTF8Encoding(false));
        manager.InvalidateWorkshopScanCache();
        var thirdInventory = await manager.ScanWorkshopFolderAsync();
        var third = Assert.Single(thirdInventory, addon => addon.IsLocal);
        Assert.Equal("vehicle", third.Type);
        Assert.Equal(["Roleplay"], third.Tags);

        File.Delete(Path.Combine(localFolder, "addon.json"));
        manager.InvalidateWorkshopScanCache();
        var fourthInventory = await manager.ScanWorkshopFolderAsync();
        var fourth = Assert.Single(fourthInventory, addon => addon.IsLocal);
        Assert.Empty(fourth.Type);
        Assert.Empty(fourth.Tags);
        Assert.Equal(AddonClassificationMetadataStatus.Known, fourth.TypeMetadataStatus);
        Assert.Equal(AddonClassificationMetadataStatus.Known, fourth.TagsMetadataStatus);
    }

    [Fact]
    public async Task Discovery_OversizedAddonJsonPreservesPriorClassificationAsUnknown()
    {
        var localFolder = CreateLocalFolderAddon();
        using var manager = CreateManager(enableDiscovery: true);
        await manager.InitializeAsync();

        var firstInventory = await manager.ScanWorkshopFolderAsync();
        var first = Assert.Single(firstInventory, addon => addon.IsLocal);
        Assert.Equal("weapon", first.Type);
        Assert.Equal(["Fun"], first.Tags);

        File.WriteAllText(
            Path.Combine(localFolder, "addon.json"),
            "{\"title\":\"Too large\",\"description\":\"" +
            new string('x', 1024 * 1024) + "\"}",
            new UTF8Encoding(false));
        manager.InvalidateWorkshopScanCache();

        var secondInventory = await manager.ScanWorkshopFolderAsync();
        var second = Assert.Single(secondInventory, addon => addon.IsLocal);
        Assert.Equal("weapon", second.Type);
        Assert.Equal(["Fun"], second.Tags);
        Assert.Equal(AddonClassificationMetadataStatus.Unknown, second.TypeMetadataStatus);
        Assert.Equal(AddonClassificationMetadataStatus.Unknown, second.TagsMetadataStatus);
    }

    [Fact]
    public async Task Discovery_DirectorySizeStreamsNestedFilesAndSkipsReparsePoints()
    {
        var localFolder = CreateLocalFolderAddon();
        var nestedPath = Path.Combine(localFolder, "materials", "nested.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
        await File.WriteAllBytesAsync(nestedPath, new byte[37]);

        var outsidePath = Path.Combine(rootPath, "outside.bin");
        await File.WriteAllBytesAsync(outsidePath, new byte[4096]);
        var linkPath = Path.Combine(localFolder, "outside-link.bin");
        TryCreateFileSymbolicLink(linkPath, outsidePath);

        var expectedSize = Directory
            .EnumerateFiles(localFolder, "*", SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(path, linkPath, StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
        using var manager = CreateManager(enableDiscovery: true);
        await manager.InitializeAsync();

        var inventory = await manager.ScanWorkshopFolderAsync();
        var local = Assert.Single(inventory, addon => addon.IsLocal);

        Assert.Equal(expectedSize, local.Size);
    }

    private string CreateLocalFolderAddon()
    {
        var localFolder = Path.Combine(localAddonsPath, "local-test");
        Directory.CreateDirectory(Path.Combine(localFolder, "lua"));
        File.WriteAllText(
            Path.Combine(localFolder, "lua", "payload.txt"),
            "payload",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(localFolder, "addon.json"),
            "{\"title\":\"Local Test Addon\",\"type\":\"weapon\",\"tags\":[\"Fun\"]}",
            new UTF8Encoding(false));
        return localFolder;
    }

    private AddonManager CreateManager(bool enableDiscovery)
    {
        return new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            CustomWorkshopCacheFilePaths = [manifestPath],
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            EnableLocalAddonDiscoveryExperimental = enableDiscovery,
            ScanCacheTtl = TimeSpan.Zero
        })
        {
            StateMatchTimeout = TimeSpan.Zero
        };
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is PlatformNotSupportedException ||
            ex is NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
