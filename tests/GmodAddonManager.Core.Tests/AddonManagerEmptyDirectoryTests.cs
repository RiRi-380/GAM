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
    public async Task ScanWorkshopFolderAsync_EmptyDirectoryAfterPayload_RemovesStaleMetadata()
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
        Assert.False(manager.GetConfiguration().AddonMetadata.ContainsKey(env.AddonId));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public TestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-empty-dir-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            AppDataPath = Path.Combine(rootPath, "appdata");
            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(AppDataPath);
        }

        public string AddonId { get; } = "123456789";
        public string WorkshopPath { get; }
        public string AppDataPath { get; }
        public string AddonDirectoryPath => Path.Combine(WorkshopPath, AddonId);
        public string PayloadPath => Path.Combine(AddonDirectoryPath, "addon.txt");

        public AddonManager CreateManager()
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = Array.Empty<string>(),
                ScanCacheTtl = TimeSpan.Zero
            });
        }

        public void WriteAddonPayload()
        {
            Directory.CreateDirectory(AddonDirectoryPath);
            File.WriteAllText(PayloadPath, "payload");
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
