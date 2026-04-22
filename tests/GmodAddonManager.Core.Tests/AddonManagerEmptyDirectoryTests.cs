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
            AppDataPath = Path.Combine(rootPath, "appdata");
            AddonDirectoryPath = Path.Combine(WorkshopPath, addonId);

            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(AppDataPath);
        }

        public string WorkshopPath { get; }
        public string AppDataPath { get; }
        public string AddonDirectoryPath { get; }

        public AddonManager CreateManager()
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true
            });
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
