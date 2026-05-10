using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonDisableStatePipelineTests : IDisposable
{
    private const string AddonId = "104479467";
    private readonly string rootPath;

    public AddonDisableStatePipelineTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "gam-disable-state-tests-" + Guid.NewGuid().ToString("N"));
        WorkshopPath = Path.Combine(rootPath, "steamapps", "workshop", "content", "4000");
        AppDataPath = Path.Combine(rootPath, "appdata");
        GmodRootPath = Path.Combine(rootPath, "steamapps", "common", "GarrysMod");
        NoMountPath = Path.Combine(GmodRootPath, "garrysmod", "cfg", "addonnomount.txt");
        Directory.CreateDirectory(WorkshopPath);
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(GmodRootPath);
    }

    private string WorkshopPath { get; }
    private string AppDataPath { get; }
    private string GmodRootPath { get; }
    private string NoMountPath { get; }

    [Fact]
    public async Task ApplyAssetDefaultStateAsync_Disabled_LeavesAssetActiveAndWritesNoMountFile()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var asset = AddCustomAsset(manager, "Disabled Asset", AddonState.Enabled);

        await manager.ApplyAssetDefaultStateAsync(asset.Id, AddonState.Disabled);

        Assert.True(asset.Enabled);
        Assert.Equal(AddonState.Disabled, asset.DefaultAddonState);
        Assert.Equal(AddonState.Disabled, asset.AddonStates[AddonId]);
        Assert.False(manager.GetFinalAddonStates()[AddonId]);
        Assert.Contains(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAddonStatesBatch_Disabled_BeatsSubscribedEnabledState()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        var asset = AddCustomAsset(manager, "Selection Disabled Asset", AddonState.Enabled);

        manager.SetAddonStatesBatch(asset.Id, new List<string> { AddonId }, AddonState.Disabled);
        await manager.UpdateAddonStatesAsync();

        Assert.True(asset.Enabled);
        Assert.Equal(AddonState.Disabled, asset.AddonStates[AddonId]);
        Assert.False(manager.GetFinalAddonStates()[AddonId]);
        Assert.Contains(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledAddonInAnyActiveAsset_BeatsEnabledAddonInAnotherAsset()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        AddKnownAddon(manager, AddonId);
        AddCustomAsset(manager, "Enabled Asset", AddonState.Enabled);
        AddCustomAsset(manager, "Disabled Asset", AddonState.Disabled);

        await manager.UpdateAddonStatesAsync();

        Assert.False(manager.GetFinalAddonStates()[AddonId]);
        Assert.Contains(AddonId, File.ReadAllText(NoMountPath), StringComparison.Ordinal);
    }

    private AddonManager CreateManager()
    {
        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = WorkshopPath,
            CustomAppDataPath = AppDataPath,
            CustomGmodInstallPath = GmodRootPath,
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true,
            CustomWorkshopCacheFilePaths = Array.Empty<string>(),
            ScanCacheTtl = TimeSpan.Zero
        });
        manager.StateMatchTimeout = TimeSpan.Zero;
        return manager;
    }

    private void AddKnownAddon(AddonManager manager, string addonId)
    {
        manager.GetConfiguration().AddonMetadata[addonId] = new WorkshopAddon(
            addonId,
            Path.Combine(WorkshopPath, addonId));
    }

    private static Asset AddCustomAsset(AddonManager manager, string name, AddonState addonState)
    {
        var asset = new Asset(name)
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = true,
            DefaultAddonState = AddonState.Enabled
        };
        asset.AddAddon(AddonId, addonState);
        manager.GetConfiguration().Assets.Add(asset);
        return asset;
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
