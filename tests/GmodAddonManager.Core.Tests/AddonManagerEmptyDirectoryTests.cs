using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using Xunit;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerEmptyDirectoryTests
{
    private const string AddonId = "9223372036854775806";

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

        public TestEnvironment(string addonId)
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            WorkshopCachePath = Path.Combine(rootPath, "steamapps", "workshop", "appworkshop_4000.acf");
            AppDataPath = Path.Combine(rootPath, "appdata");
            AddonDirectoryPath = Path.Combine(WorkshopPath, addonId);

            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(Path.GetDirectoryName(WorkshopCachePath)!);
            Directory.CreateDirectory(AppDataPath);
        }

        public string WorkshopPath { get; }
        public string WorkshopCachePath { get; }
        public string AppDataPath { get; }
        public string AddonDirectoryPath { get; }

        public AddonManager CreateManager(bool includeWorkshopCache = false)
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = includeWorkshopCache ? new[] { WorkshopCachePath } : null
            });
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
                builder.AppendLine($"        \"{addon.Id}\" \"1\"");
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

            return File.WriteAllTextAsync(WorkshopCachePath, builder.ToString());
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
