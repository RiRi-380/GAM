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
    public async Task ScanForNewAddonsAsync_CacheTitleForPendingAddon_ClearsTitleUpdate()
    {
        using var env = new TestEnvironment();
        var cachePath = env.WriteWorkshopCache((env.AddonId, "Door STool"));

        using var manager = env.CreateManager(new[] { cachePath });
        await manager.InitializeAsync();

        var addons = await manager.ScanForNewAddonsAsync();
        var addon = Assert.Single(addons, addon => addon.Id == env.AddonId);

        Assert.Equal("Door STool", addon.Title);
        Assert.False(addon.NeedsTitleUpdate);
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

        public AddonManager CreateManager(IReadOnlyList<string>? workshopCacheFilePaths = null)
        {
            return new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = DisableMode.Soft,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = workshopCacheFilePaths ?? Array.Empty<string>(),
                ScanCacheTtl = TimeSpan.Zero
            });
        }

        public void WriteAddonPayload()
        {
            Directory.CreateDirectory(AddonDirectoryPath);
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
                Directory.Delete(rootPath, true);
            }
        }
    }
}
