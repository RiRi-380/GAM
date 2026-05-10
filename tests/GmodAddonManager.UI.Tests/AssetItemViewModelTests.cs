using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class AssetItemViewModelTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "GAM_UI_AssetItemVM_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportedDisableManifestAsset_CanEditDefaultAddonState()
    {
        using var manager = await CreateManagerAsync();
        var asset = new Asset("Cleanup")
        {
            Id = DisableManifestImportServiceConstants.NewAssetIdPrefix + "test",
            Enabled = true,
            DefaultAddonState = AddonState.Excluded
        };
        asset.AddAddon("104479467", AddonState.Excluded);

        using var viewModel = new AssetItemViewModel(asset, manager, null!, null!, showExclusiveApply: true);

        Assert.True(viewModel.IsDisableManifestAsset);
        Assert.True(viewModel.CanEditAddonDefaultState);
        Assert.True(viewModel.IsExcludedState);
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
                Directory.Delete(path, recursive: true);
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
}
