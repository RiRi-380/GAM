using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerEmptyDirectoryTests
{
    private const string AddonId = "9223372036854775806";

    [Fact]
    public async Task InitializeAndScanMissingWorkshopPathTreatsItAsEmpty()
    {
        using var env = new TestEnvironment(AddonId, createWorkshopPath: false);

        Assert.False(Directory.Exists(env.WorkshopPath));

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        Assert.False(Directory.Exists(env.WorkshopPath));
        Assert.Empty(await manager.ScanWorkshopFolderAsync());
        Assert.Empty(await manager.ScanForNewAddonsAsync());
        Assert.Empty(manager.GetEnabledAddons());
    }

    [Fact]
    public async Task ScanForNewAddonsAsyncIgnoresDetailsOnlyWorkshopCacheItems()
    {
        using var env = new TestEnvironment(AddonId);
        await env.WriteWorkshopCacheDetailsOnlyAsync((AddonId, "Stale details-only addon"));

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var discovered = await manager.ScanForNewAddonsAsync();

        Assert.DoesNotContain(discovered, addon => addon.Id == AddonId);
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey(AddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncRemovesStaleDetailsOnlyPendingAddon()
    {
        using var env = new TestEnvironment(AddonId, createWorkshopPath: false);
        await env.WriteWorkshopCacheDetailsOnlyAsync((AddonId, "Stale details-only addon"));

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var config = manager.GetConfiguration();
        config.AddonMetadata[AddonId] = new GmodAddonManager.Core.Models.WorkshopAddon
        {
            Id = AddonId,
            Title = $"Workshop-{AddonId} (Pending Download)",
            IsEnabled = false,
            NeedsTitleUpdate = true
        };
        var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");
        subscribeAsset.AddAddon(AddonId, GmodAddonManager.Core.Models.AddonState.Disabled);

        await manager.ScanWorkshopFolderAsync();

        Assert.False(config.AddonMetadata.ContainsKey(AddonId));
        Assert.DoesNotContain(AddonId, subscribeAsset.Addons);
    }

    [Fact]
    public async Task InitializeAsyncPrunesStaleCacheOnlyMetadataWhenSubscriptionTruthExists()
    {
        var currentId = "9223372036854775805";
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.GmodCachePath);
        var staleCacheFile = Path.Combine(env.GmodCachePath, $"{AddonId}.gma");
        await File.WriteAllTextAsync(staleCacheFile, "stale cache payload", TestContext.Current.CancellationToken);
        await env.WriteWorkshopCacheAsync((currentId, "Current subscribed addon"));
        await env.WriteConfigAsync(config =>
        {
            config.AddonMetadata[AddonId] = new WorkshopAddon(AddonId, staleCacheFile)
            {
                Title = "Stale cache-only addon",
                IsGmaFile = true,
                IsEnabled = true
            };
            config.AddonMetadata[currentId] = new WorkshopAddon(currentId, string.Empty)
            {
                Title = "Current subscribed addon",
                IsGmaFile = false,
                IsEnabled = false,
                NeedsTitleUpdate = true
            };

            var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");
            subscribeAsset.AddAddon(AddonId, AddonState.Enabled);
            subscribeAsset.AddAddon(currentId, AddonState.Disabled);
        });

        using var manager = env.CreateManager(includeWorkshopCache: true, disableCacheScan: false);
        await manager.InitializeAsync();

        var config = manager.GetConfiguration();
        var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");

        Assert.False(config.AddonMetadata.ContainsKey(AddonId));
        Assert.True(config.AddonMetadata.ContainsKey(currentId));
        Assert.DoesNotContain(AddonId, subscribeAsset.Addons);
        Assert.Contains(currentId, subscribeAsset.Addons);
        Assert.True(File.Exists(staleCacheFile));
    }

    [Fact]
    public async Task InitializeAsyncKeepsUserAssetMetadataButRemovesStaleIdFromSubscribeAsset()
    {
        using var env = new TestEnvironment(AddonId);
        await env.WriteWorkshopCacheAsync();
        await env.WriteConfigAsync(config =>
        {
            config.AddonMetadata[AddonId] = new WorkshopAddon(AddonId, string.Empty)
            {
                Title = "GPT disable metadata",
                IsGmaFile = false,
                IsEnabled = false,
                NeedsTitleUpdate = true
            };

            var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");
            subscribeAsset.AddAddon(AddonId, AddonState.Disabled);

            var disableAsset = new Asset("GPT Disable List")
            {
                Id = DisableManifestImportService.AssetId,
                Enabled = true,
                IsSystem = false,
                DefaultAddonState = AddonState.Disabled
            };
            disableAsset.AddAddon(AddonId, AddonState.Excluded);
            config.Assets.Add(disableAsset);
        });

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var config = manager.GetConfiguration();
        var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");
        var disableAsset = config.Assets.Single(asset => asset.Id == DisableManifestImportService.AssetId);

        Assert.True(config.AddonMetadata.ContainsKey(AddonId));
        Assert.DoesNotContain(AddonId, subscribeAsset.Addons);
        Assert.False(subscribeAsset.AddonStates.ContainsKey(AddonId));
        Assert.Contains(AddonId, disableAsset.Addons);
        Assert.Equal(AddonState.Excluded, disableAsset.AddonStates[AddonId]);
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncEmptyNumericDirectoryIsIgnored()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var scanned = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(scanned, addon => addon.Id == AddonId);
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey(AddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncEmptyDirectoryAfterPayloadRemovesStaleMetadata()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(env.AddonDirectoryPath, "addon.txt"), "payload");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var firstScan = await manager.ScanWorkshopFolderAsync();
        Assert.Contains(firstScan, addon => addon.Id == AddonId);
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey(AddonId));

        File.Delete(Path.Combine(env.AddonDirectoryPath, "addon.txt"));

        var secondScan = await manager.ScanWorkshopFolderAsync();
        var config = manager.GetConfiguration();
        var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");

        Assert.DoesNotContain(secondScan, addon => addon.Id == AddonId);
        Assert.False(config.AddonMetadata.ContainsKey(AddonId));
        Assert.DoesNotContain(AddonId, subscribeAsset.Addons);
    }

    [Fact]
    public async Task ScanForNewAddonsAsyncEmptyNumericDirectoryIsIgnored()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var discovered = await manager.ScanForNewAddonsAsync();

        Assert.DoesNotContain(discovered, addon => addon.Id == AddonId);
    }

    [Fact]
    public async Task ScanForNewAddonsAsyncEmptySubscribedDirectoryIsReturnedAsPendingDownload()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);
        await env.WriteWorkshopCacheAsync((AddonId, "Subscribed empty folder"));

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var discovered = await manager.ScanForNewAddonsAsync();
        var addon = Assert.Single(discovered.Where(addon => addon.Id == AddonId));

        Assert.False(addon.IsEnabled);
        Assert.Equal("Subscribed empty folder", addon.Title);
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncKeepsPendingSubscribedMetadataWithoutPayload()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);
        await env.WriteWorkshopCacheAsync((AddonId, "Subscribed empty folder"));

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var discovered = await manager.ScanForNewAddonsAsync();
        await manager.RegisterNewAddonsAsync(discovered);

        var config = manager.GetConfiguration();
        Assert.True(config.AddonMetadata.ContainsKey(AddonId));

        var scanned = await manager.ScanWorkshopFolderAsync();
        var subscribeAsset = config.Assets.Single(asset => asset.Id == "subscribe-system-asset");

        Assert.True(config.AddonMetadata.ContainsKey(AddonId));
        Assert.Contains(AddonId, subscribeAsset.Addons);
        Assert.DoesNotContain(scanned, addon => addon.Id == AddonId);
    }

    [Fact]
    public async Task FirstRunScanAndRegistrationIncludesSubscribedAddonsWithoutFolders()
    {
        using var env = new TestEnvironment(AddonId);
        var localAddonId = "9223372036854775805";
        var pendingAddonId = "9223372036854775804";
        var localAddonPath = Path.Combine(env.WorkshopPath, localAddonId);
        Directory.CreateDirectory(localAddonPath);
        await File.WriteAllTextAsync(Path.Combine(localAddonPath, "addon.txt"), "payload");
        await env.WriteWorkshopCacheAsync(
            (localAddonId, "Local subscribed addon"),
            (pendingAddonId, "Pending subscribed addon"));

        using var manager = env.CreateManager(includeWorkshopCache: true);
        await manager.InitializeAsync();

        var localAddons = await manager.ScanWorkshopFolderAsync();
        var newAddons = await manager.ScanForNewAddonsAsync();
        await manager.RegisterNewAddonsAsync(newAddons);

        var config = manager.GetConfiguration();

        Assert.Contains(localAddons, addon => addon.Id == localAddonId);
        Assert.Contains(newAddons, addon => addon.Id == pendingAddonId);
        Assert.True(config.AddonMetadata.ContainsKey(localAddonId));
        Assert.True(config.AddonMetadata.ContainsKey(pendingAddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncMarkerOnlyDirectoryIsIgnored()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(env.AddonDirectoryPath, ".gam_disabled"), "disabled by GAM");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var scanned = await manager.ScanWorkshopFolderAsync();

        Assert.DoesNotContain(scanned, addon => addon.Id == AddonId);
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey(AddonId));
    }

    [Fact]
    public async Task ScanWorkshopFolderAsyncMarkerAndPayloadDirectoryIsIncluded()
    {
        using var env = new TestEnvironment(AddonId);
        Directory.CreateDirectory(env.AddonDirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(env.AddonDirectoryPath, ".gam_disabled"), "disabled by GAM");
        await File.WriteAllTextAsync(Path.Combine(env.AddonDirectoryPath, "payload.txt"), "payload");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();

        var scanned = await manager.ScanWorkshopFolderAsync();

        Assert.Contains(scanned, addon => addon.Id == AddonId);
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey(AddonId));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public TestEnvironment(string addonId, bool createWorkshopPath = true)
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            WorkshopCachePath = Path.Combine(rootPath, "steamapps", "workshop", "appworkshop_4000.acf");
            GmodCachePath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod", "garrysmod", "cache", "workshop");
            AppDataPath = Path.Combine(rootPath, "appdata");
            AddonDirectoryPath = Path.Combine(WorkshopPath, addonId);

            if (createWorkshopPath)
            {
                Directory.CreateDirectory(WorkshopPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(WorkshopCachePath)!);
            Directory.CreateDirectory(AppDataPath);
        }

        public string WorkshopPath { get; }
        public string WorkshopCachePath { get; }
        public string GmodCachePath { get; }
        public string AppDataPath { get; }
        public string AddonDirectoryPath { get; }

        public AddonManager CreateManager(bool includeWorkshopCache = false, bool disableCacheScan = true)
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = disableCacheScan,
                CustomGmodCachePath = disableCacheScan ? null : GmodCachePath,
                CustomWorkshopCacheFilePaths = includeWorkshopCache
                    ? new[] { WorkshopCachePath }
                    : Array.Empty<string>()
            });
        }

        public Task WriteConfigAsync(Action<Configuration> configure)
        {
            var config = new Configuration();
            config.CreateDefaultAssets();
            configure(config);

            var path = Path.Combine(AppDataPath, "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            return File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        }

        public Task WriteWorkshopCacheAsync(params (string Id, string Title)[] addons)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"AppWorkshop\"");
            builder.AppendLine("{");
            builder.AppendLine("    \"WorkshopItemsInstalled\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.AppendLine($"        \"{addon.Id}\"");
                builder.AppendLine("        {");
                builder.AppendLine("            \"size\" \"4096\"");
                builder.AppendLine("            \"timeupdated\" \"1700000000\"");
                builder.AppendLine("            \"manifest\" \"1234567890123456789\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("    \"WorkshopItemDetails\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.AppendLine($"        \"{addon.Id}\"");
                builder.AppendLine("        {");
                builder.AppendLine($"            \"title\" \"{addon.Title}\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");

            return File.WriteAllTextAsync(WorkshopCachePath, builder.ToString(), TestContext.Current.CancellationToken);
        }

        public Task WriteWorkshopCacheDetailsOnlyAsync(params (string Id, string Title)[] addons)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"AppWorkshop\"");
            builder.AppendLine("{");
            builder.AppendLine("    \"WorkshopItemsInstalled\"");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine("    \"WorkshopItemDetails\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.AppendLine($"        \"{addon.Id}\"");
                builder.AppendLine("        {");
                builder.AppendLine($"            \"title\" \"{addon.Title}\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");

            return File.WriteAllTextAsync(WorkshopCachePath, builder.ToString(), TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }
    }
}
