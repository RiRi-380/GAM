using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerEmptyDirectoryTests
{
    [Fact]
    public async Task ScanAddonAsync_EmptyNumericDirectory_IsIgnored()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.AddonDirectoryPath);

        using var manager = env.CreateManager();

        var addon = await manager.ScanAddonAsync(env.AddonDirectoryPath);

        Assert.Null(addon);
    }

    [Fact]
    public async Task ScanForNewAddonsAsync_EmptyNumericDirectory_IsIgnored()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.AddonDirectoryPath);

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var addons = await manager.ScanForNewAddonsAsync();

        Assert.DoesNotContain(addons, addon => addon.Id == env.AddonId);
    }

    [Fact]
    public async Task ScanForNewAddonsAsync_MarkerOnlyDirectory_IsIgnored()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.AddonDirectoryPath);
        File.WriteAllText(Path.Combine(env.AddonDirectoryPath, ".gam_disabled"), string.Empty);

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var addons = await manager.ScanForNewAddonsAsync();

        Assert.DoesNotContain(addons, addon => addon.Id == env.AddonId);
    }

    [Fact]
    public async Task ScanForNewAddonsAsync_MarkerAndPayloadDirectory_IsIncluded()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();
        File.WriteAllText(Path.Combine(env.AddonDirectoryPath, ".gam_disabled"), string.Empty);

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var addons = await manager.ScanForNewAddonsAsync();

        Assert.Contains(addons, addon => addon.Id == env.AddonId);
    }

    [Fact]
    public async Task ScanForNewAddonsAsync_PendingDownloadIsAggregateOnly()
    {
        using var env = new TestEnvironment();
        var cachePath = env.WriteWorkshopCache((env.AddonId, "Door STool"));

        using var manager = env.CreateManager(new[] { cachePath });
        await manager.InitializeAsync();

        var addons = await manager.ScanForNewAddonsAsync();
        Assert.DoesNotContain(addons, addon => addon.Id == env.AddonId);
        Assert.Equal(1, manager.PendingDownloadCount);
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey(env.AddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_EmptyDirectoryAfterPayload_HidesCardButPreservesMetadataWithoutSubscriptionAuthority()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var initialAddons = await manager.ScanWorkshopFolderAsync();
        Assert.Contains(initialAddons, addon => addon.Id == env.AddonId);
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey(env.AddonId));

        File.Delete(env.PayloadPath);
        manager.InvalidateWorkshopScanCache();

        var rescannedAddons = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(rescannedAddons, addon => addon.Id == env.AddonId);
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey(env.AddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_UnsubscribedReference_IsRemovedByDefault()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();
        var cachePath = env.WriteWorkshopCache((env.AddonId, "Door STool"));
        using var manager = env.CreateManager(new[] { cachePath });
        await manager.InitializeAsync();
        var custom = new Asset("Custom");
        custom.Addons.Add("999999999");
        manager.GetConfiguration().Assets.Add(custom);
        await manager.SaveConfigurationAsync();

        await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain("999999999", custom.Addons);
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey("999999999"));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_UnsubscribedReference_CanBeRetainedAsUnavailable()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();
        var cachePath = env.WriteWorkshopCache((env.AddonId, "Door STool"));
        using var manager = env.CreateManager(new[] { cachePath });
        await manager.InitializeAsync();
        var custom = new Asset("Custom");
        custom.Addons.Add("999999999");
        var configuration = manager.GetConfiguration();
        configuration.Assets.Add(custom);
        configuration.RetainMissingAssetReferences = true;
        await manager.SaveConfigurationAsync();

        await manager.ScanWorkshopFolderAsync();

        Assert.Contains("999999999", custom.Addons);
        var metadata = Assert.IsType<WorkshopAddon>(
            configuration.AddonMetadata["999999999"]);
        Assert.False(metadata.IsAvailable);
        Assert.False(metadata.IsDownloadPending);
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_ZeroByteCacheGmaIsHiddenAndPreserved()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.GmodCachePath);
        var zeroByteGma = Path.Combine(env.GmodCachePath, env.AddonId + ".gma");
        File.WriteAllBytes(zeroByteGma, Array.Empty<byte>());

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().AddonMetadata[env.AddonId] =
            new WorkshopAddon(env.AddonId, zeroByteGma)
            {
                IsGmaFile = true
            };
        manager.InvalidateWorkshopScanCache();

        var addons = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(addons, addon => addon.Id == env.AddonId);
        Assert.True(File.Exists(zeroByteGma));
        Assert.Equal(0, new FileInfo(zeroByteGma).Length);
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_AppliesWorkshopUpdateTimestampFromManifest()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();
        var cachePath = env.WriteWorkshopCache((env.AddonId, "Door STool"));

        using var manager = env.CreateManager([cachePath]);
        await manager.InitializeAsync();
        var addon = Assert.Single(await manager.ScanWorkshopFolderAsync());

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime,
            addon.WorkshopUpdatedAtUtc);
    }

    [Fact]
    public async Task ScanWorkshopFolderAsync_RefreshesSizeWhenDirectoryChanges()
    {
        using var env = new TestEnvironment();
        env.WriteAddonPayload();

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var first = Assert.Single(await manager.ScanWorkshopFolderAsync());
        var firstSize = first.Size;
        var firstTimestamp = first.LastUpdated;

        File.WriteAllBytes(
            Path.Combine(env.AddonDirectoryPath, "new-payload.bin"),
            new byte[128]);
        Directory.SetLastWriteTimeUtc(
            env.AddonDirectoryPath,
            firstTimestamp.AddSeconds(2));
        manager.InvalidateWorkshopScanCache();

        var refreshed = Assert.Single(await manager.ScanWorkshopFolderAsync());

        Assert.True(refreshed.Size >= firstSize + 128);
        Assert.True(refreshed.LastUpdated > firstTimestamp);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public TestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-empty-dir-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            AppDataPath = Path.Combine(rootPath, "appdata");
            GmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(AppDataPath);
            Directory.CreateDirectory(GmodRootPath);
        }

        public string AddonId { get; } = "123456789";
        public string WorkshopPath { get; }
        public string AppDataPath { get; }
        public string GmodRootPath { get; }
        public string GmodCachePath =>
            Path.Combine(GmodRootPath, "garrysmod", "cache", "workshop");
        public string AddonDirectoryPath => Path.Combine(WorkshopPath, AddonId);
        public string PayloadPath => Path.Combine(AddonDirectoryPath, "lua", "autorun.lua");

        public AddonManager CreateManager(IReadOnlyList<string>? workshopCacheFilePaths = null)
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                CustomGmodInstallPath = GmodRootPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = workshopCacheFilePaths ?? Array.Empty<string>(),
                ScanCacheTtl = TimeSpan.Zero
            });
        }

        public void WriteAddonPayload()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PayloadPath)!);
            File.WriteAllText(PayloadPath, "payload");
        }

        public string WriteWorkshopCache(params (string Id, string Title)[] addons)
        {
            var cachePath = Path.Combine(rootPath, "appworkshop_4000.acf");
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"AppWorkshop\"");
            builder.AppendLine("{");
            builder.AppendLine("    \"WorkshopItemsInstalled\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.Append("        \"").Append(addon.Id).AppendLine("\"");
                builder.AppendLine("        {");
                builder.AppendLine("            \"size\" \"4096\"");
                builder.AppendLine("            \"timeupdated\" \"1700000000\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("    \"WorkshopItemDetails\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.Append("        \"").Append(addon.Id).AppendLine("\"");
                builder.AppendLine("        {");
                builder.Append("            \"title\" \"").Append(addon.Title).AppendLine("\"");
                builder.AppendLine("            \"timeupdated\" \"1700000000\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            File.WriteAllText(cachePath, builder.ToString());
            return cachePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                DeleteDirectoryWithRetry(rootPath);
            }
        }

        private static void DeleteDirectoryWithRetry(string path)
        {
            const int maxAttempts = 10;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
