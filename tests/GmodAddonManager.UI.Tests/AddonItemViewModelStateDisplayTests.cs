using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using Xunit;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonItemViewModelStateDisplayTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "gam-ui-state-tests",
        Guid.NewGuid().ToString("N"));

    static AddonItemViewModelStateDisplayTests()
    {
        LocalizationManager.Instance.ChangeLanguage("en-US");
    }

    [Fact]
    public async Task DisabledAssetExcludedAddonDoesNotShowEffectiveRedBorder()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: false, AddonState.Excluded);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.False(addon.IsExcludedAnywhere);
        Assert.Equal("#303030", addon.BorderColor);
        Assert.Equal("Inactive", addon.StateText);
    }

    [Fact]
    public async Task EnabledAssetExcludedAddonShowsEffectiveRedBorder()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Excluded);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.True(addon.IsExcludedAnywhere);
        Assert.Equal("#F44336", addon.BorderColor);
        Assert.Equal("Excluded", addon.StateText);
    }

    [Fact]
    public async Task DisabledAssetDisabledAddonDoesNotShowLocalOrangeBorder()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: false, AddonState.Disabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);

        Assert.False(addon.IsExcludedAnywhere);
        Assert.Equal("#303030", addon.BorderColor);
        Assert.Equal("Inactive", addon.StateText);
    }

    [Fact]
    public async Task ClearingCurrentAssetClearsStaleLocalState()
    {
        using var manager = await CreateManagerAsync();
        var asset = CreateAsset(manager, enabled: true, AddonState.Disabled);
        var addon = CreateAddonViewModel(manager);
        using var assetViewModel = CreateAssetViewModel(asset, manager);

        addon.SetCurrentAsset(assetViewModel);
        Assert.Equal("#FF9800", addon.BorderColor);
        Assert.Equal("Disabled", addon.StateText);

        addon.SetCurrentAsset(null);

        Assert.Equal("#303030", addon.BorderColor);
        Assert.Equal("Unknown", addon.StateText);
    }

    private async Task<AddonManager> CreateManagerAsync()
    {
        var workshopPath = Path.Combine(rootPath, "workshop", "content", "4000");
        var appDataPath = Path.Combine(rootPath, "appdata");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);

        var manager = new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            DisableMode = DisableMode.Soft,
            DisableCacheScan = true
        });

        await manager.InitializeAsync();
        return manager;
    }

    private static Asset CreateAsset(AddonManager manager, bool enabled, AddonState state)
    {
        var asset = new Asset("Test Asset")
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = enabled,
            DefaultAddonState = AddonState.Enabled
        };
        asset.AddAddon("addon-1", state);
        manager.GetConfiguration().Assets.Add(asset);
        return asset;
    }

    private static AddonItemViewModel CreateAddonViewModel(AddonManager manager)
    {
        return new AddonItemViewModel(
            new WorkshopAddon
            {
                Id = "addon-1",
                Title = "Test Addon",
                FolderPath = string.Empty,
                NeedsTitleUpdate = false
            },
            manager);
    }

    private static AssetItemViewModel CreateAssetViewModel(Asset asset, AddonManager manager)
    {
        return new AssetItemViewModel(asset, manager, null!, null!, showExclusiveApply: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, true);
        }
    }
}
