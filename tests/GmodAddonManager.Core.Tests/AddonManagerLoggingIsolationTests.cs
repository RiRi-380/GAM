using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerLoggingIsolationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-log-isolation-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CustomAppDataRoutesDefaultAndExperimentLogsToTheIsolatedRoot()
    {
        var appDataPath = Path.Combine(rootPath, "appdata");
        var libraryPath = Path.Combine(rootPath, "SteamLibrary");
        var gmodPath = Path.Combine(
            libraryPath,
            "steamapps",
            "common",
            "GarrysMod");
        var workshopPath = Path.Combine(
            libraryPath,
            "steamapps",
            "workshop",
            "content",
            "4000");
        var cachePath = Path.Combine(
            gmodPath,
            "garrysmod",
            "cache",
            "workshop");

        Directory.CreateDirectory(Path.Combine(gmodPath, "garrysmod", "cfg"));
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(cachePath);

        using (var manager = new AddonManager(new AddonManagerOptions
               {
                   CustomAppDataPath = appDataPath,
                   CustomGmodInstallPath = gmodPath,
                   CustomWorkshopPath = workshopPath,
                   CustomGmodCachePath = cachePath,
                   CustomWorkshopCacheFilePaths = Array.Empty<string>(),
                   DisableCacheScan = true
               }))
        {
            await manager.InitializeAsync();
        }

        var logPath = Path.Combine(appDataPath, "logs");
        Assert.NotEmpty(Directory.GetFiles(logPath, "info_*.log"));
        Assert.True(File.Exists(Path.Combine(logPath, "experiment_events.jsonl")));
    }

    [Fact]
    public async Task ManagersSharingAppDataSerializeConfigurationWrites()
    {
        var appDataPath = Path.Combine(rootPath, "shared-appdata");
        var libraryPath = Path.Combine(rootPath, "SharedSteamLibrary");
        var gmodPath = Path.Combine(
            libraryPath,
            "steamapps",
            "common",
            "GarrysMod");
        var workshopPath = Path.Combine(
            libraryPath,
            "steamapps",
            "workshop",
            "content",
            "4000");
        var cachePath = Path.Combine(
            gmodPath,
            "garrysmod",
            "cache",
            "workshop");

        Directory.CreateDirectory(Path.Combine(gmodPath, "garrysmod", "cfg"));
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(cachePath);

        AddonManager CreateManager() => new(new AddonManagerOptions
        {
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodPath,
            CustomWorkshopPath = workshopPath,
            CustomGmodCachePath = cachePath,
            CustomWorkshopCacheFilePaths = Array.Empty<string>(),
            DisableCacheScan = true
        });

        using var first = CreateManager();
        using var second = CreateManager();
        await first.InitializeAsync();
        await second.InitializeAsync();

        var saves = Enumerable.Range(0, 40)
            .Select(index => (index & 1) == 0
                ? first.SaveConfigurationImmediatelyAsync()
                : second.SaveConfigurationImmediatelyAsync());
        await Task.WhenAll(saves);

        var persisted = Newtonsoft.Json.Linq.JObject.Parse(
            File.ReadAllText(Path.Combine(appDataPath, "config.json")));
        Assert.NotNull(persisted["assets"]);
        Assert.Empty(Directory.GetFiles(appDataPath, "config.json.*.tmp"));
        Assert.False(File.Exists(Path.Combine(appDataPath, "config.json.tmp")));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
