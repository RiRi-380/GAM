using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class DisableManifestImportServiceTests
{
    [Fact]
    public async Task ImportAsync_ValidManifest_CreatesActiveExcludedAssetAndWritesNoMountFile()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: new\n" +
            "# name: Weapon Cleanup\n" +
            "104479467 # Door STool\n" +
            "104483020\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);

        var result = await service.ImportAsync(manifestPath, new DisableManifestImportOptions());

        var asset = manager.GetConfiguration().Assets.Single(a => a.Id == result.AssetId);
        Assert.True(asset.Enabled);
        Assert.False(asset.IsSystem);
        Assert.Equal(AddonState.Excluded, asset.DefaultAddonState);
        Assert.Equal("Weapon Cleanup", asset.Name);
        Assert.Equal(new[] { "104479467", "104483020" }, asset.Addons);
        Assert.All(asset.Addons, addonId => Assert.Equal(AddonState.Excluded, asset.AddonStates[addonId]));
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey("104479467"));
        Assert.True(manager.GetConfiguration().AddonMetadata.ContainsKey("104483020"));
        Assert.Equal("Workshop-104479467", manager.GetConfiguration().AddonMetadata["104479467"].Title);
        Assert.True(File.Exists(env.NoMountPath));
        var noMountText = File.ReadAllText(env.NoMountPath);
        Assert.Contains("104479467", noMountText, StringComparison.Ordinal);
        Assert.Contains("104483020", noMountText, StringComparison.Ordinal);
        Assert.True(result.AppliedImmediately);
        Assert.False(result.QueuedPendingApply);
        Assert.False(result.CreatedDisabledAsset);
    }

    [Fact]
    public async Task ImportAsync_ExistingAssetName_CreatesUniqueActiveAsset()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: new\n" +
            "# name: \u524a\u9664\u5019\u88dc\n" +
            "104479467\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        manager.GetConfiguration().Assets.Add(new Asset("Cleanup Candidates") { Enabled = true });
        var service = new DisableManifestImportService(manager);

        var result = await service.ImportAsync(manifestPath, new DisableManifestImportOptions());

        Assert.Equal("Cleanup Candidates (2)", result.AssetName);
        var importedAsset = manager.GetConfiguration().Assets.Single(a => a.Id == result.AssetId);
        Assert.True(importedAsset.Enabled);
    }

    [Theory]
    [InlineData(DisableManifestMode.Merge)]
    [InlineData(DisableManifestMode.Replace)]
    public async Task ImportAsync_MergeOrReplaceOptions_StillCreateNewActiveAsset(DisableManifestMode mode)
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: merge\n" +
            "104479467\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var existingAsset = new Asset("Existing")
        {
            Id = DisableManifestImportServiceConstants.AssetId,
            Enabled = true
        };
        existingAsset.Addons.Add("999");
        manager.GetConfiguration().Assets.Add(existingAsset);
        var service = new DisableManifestImportService(manager);

        var result = await service.ImportAsync(
            manifestPath,
            new DisableManifestImportOptions { Mode = mode });

        Assert.NotEqual(existingAsset.Id, result.AssetId);
        Assert.Contains(manager.GetConfiguration().Assets, a => a.Id == existingAsset.Id);
        Assert.Contains(manager.GetConfiguration().Assets, a => a.Id == result.AssetId && a.Enabled);
    }

    [Fact]
    public async Task SetAssetEnabledAsync_ImportedDisableAsset_PreservesExcludedStatesAndWritesNoMountFile()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: new\n" +
            "104479467\n" +
            "104483020\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);
        var result = await service.ImportAsync(manifestPath, new DisableManifestImportOptions());

        await manager.SetAssetEnabledAsync(result.AssetId, enabled: true);

        var asset = manager.GetConfiguration().Assets.Single(a => a.Id == result.AssetId);
        Assert.True(asset.Enabled);
        Assert.Equal(AddonState.Excluded, asset.DefaultAddonState);
        Assert.All(asset.Addons, addonId => Assert.Equal(AddonState.Excluded, asset.AddonStates[addonId]));

        var noMountText = File.ReadAllText(env.NoMountPath);
        Assert.Contains("104479467", noMountText, StringComparison.Ordinal);
        Assert.Contains("104483020", noMountText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAssetEnabledAsync_DisablingImportedDisableAsset_PreservesExcludedStatesAndClearsNoMountFile()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "104479467\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);
        var result = await service.ImportAsync(manifestPath, new DisableManifestImportOptions());

        await manager.SetAssetEnabledAsync(result.AssetId, enabled: true);
        await manager.SetAssetEnabledAsync(result.AssetId, enabled: false);

        var asset = manager.GetConfiguration().Assets.Single(a => a.Id == result.AssetId);
        Assert.False(asset.Enabled);
        Assert.Equal(AddonState.Excluded, asset.DefaultAddonState);
        Assert.Equal(AddonState.Excluded, asset.AddonStates["104479467"]);

        var noMountText = File.ReadAllText(env.NoMountPath);
        Assert.DoesNotContain("104479467", noMountText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableImportedAsset_ThenUpdateStates_WritesNoMountFile()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: new\n" +
            "104479467\n" +
            "104483020\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);
        var result = await service.ImportAsync(manifestPath, new DisableManifestImportOptions());
        var asset = manager.GetConfiguration().Assets.Single(a => a.Id == result.AssetId);

        asset.Enabled = true;
        await manager.UpdateAddonStatesAsync();

        var noMountText = File.ReadAllText(env.NoMountPath);
        Assert.Contains("104479467", noMountText, StringComparison.Ordinal);
        Assert.Contains("104483020", noMountText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewAsync_ValidManifest_ReportsNewDisabledAssetPlan()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "# mode: replace\n" +
            "104479467\n" +
            "104479467 # duplicate\n" +
            "invalid line\n");

        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);

        var preview = await service.PreviewAsync(manifestPath);

        Assert.Equal(1, preview.ValidCount);
        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(1, preview.InvalidCount);
        Assert.Equal(DisableManifestMode.New, preview.Mode);
        Assert.Equal("Cleanup Candidates", preview.AssetName);
        Assert.True(preview.IsSoftMode);
        Assert.False(preview.CreatesDisabledAsset);
        Assert.False(preview.WillRequirePendingApply);
    }

    [Fact]
    public async Task ImportAsync_HardMode_IsRejected()
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(
            "# GAM-DISABLE v1\n" +
            "# appid: 4000\n" +
            "# action: exclude\n" +
            "104479467\n");

        using var manager = env.CreateManager(DisableMode.Hard);
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(manifestPath, new DisableManifestImportOptions()));

        Assert.Contains("Soft disable mode", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# GAM-DISABLE v1\n# appid: 4000\n# action: delete\n104479467\n")]
    [InlineData("# GAM-DISABLE v1\n# appid: 9999\n# action: exclude\n104479467\n")]
    [InlineData("# appid: 4000\n# action: exclude\n104479467\n")]
    public async Task ImportAsync_UnsupportedManifest_IsRejected(string manifestText)
    {
        using var env = new TestEnvironment();
        var manifestPath = env.WriteManifest(manifestText);
        using var manager = env.CreateManager();
        await manager.InitializeAsync();
        var service = new DisableManifestImportService(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(manifestPath, new DisableManifestImportOptions()));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public TestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-disable-import-tests-" + Guid.NewGuid().ToString("N"));
            WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
            AppDataPath = Path.Combine(rootPath, "appdata");
            GmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
            NoMountPath = Path.Combine(GmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
            Directory.CreateDirectory(WorkshopPath);
            Directory.CreateDirectory(AppDataPath);
            Directory.CreateDirectory(GmodRootPath);
        }

        public string WorkshopPath { get; }
        public string AppDataPath { get; }
        public string GmodRootPath { get; }
        public string NoMountPath { get; }

        public AddonManager CreateManager(DisableMode disableMode = DisableMode.Soft)
        {
            var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = WorkshopPath,
                CustomAppDataPath = AppDataPath,
                DisableMode = disableMode,
                DisableCacheScan = true,
                CustomWorkshopCacheFilePaths = Array.Empty<string>(),
                ScanCacheTtl = TimeSpan.Zero
            });
            manager.StateMatchTimeout = TimeSpan.Zero;
            return manager;
        }

        public string WriteManifest(string text)
        {
            var path = Path.Combine(rootPath, "disable-list.gamdisable");
            File.WriteAllText(path, text);
            return path;
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
