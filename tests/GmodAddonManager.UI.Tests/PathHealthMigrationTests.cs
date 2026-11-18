using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class PathHealthMigrationTests
{
    [Fact]
    public async Task MigrateAddonNoMountAsync_RequiresNewPreviewWhenSourceGainsAnId()
    {
        var root = Directory.CreateTempSubdirectory("gam-migration-preview-");
        try
        {
            var game = root.CreateSubdirectory("current");
            game.CreateSubdirectory("garrysmod/cfg");
            var current = new GmodAddonStateStore(game.FullName);
            var old = new GmodAddonStateStore(Path.Combine(root.FullName, "previous"));
            old.WriteDisabledIds(new[] { "100" });
            current.WriteDisabledIds(new[] { "900" });
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomAppDataPath = root.CreateSubdirectory("profile").FullName,
                CustomWorkshopPath = root.CreateSubdirectory("workshop").FullName,
                CustomGmodInstallPath = game.FullName,
                CustomWorkshopCacheFilePaths = [Path.Combine(root.FullName, "fixture-manifest.acf")],
                DisableCacheScan = true
            });
            manager.GmodRunningProvider = () => false;
            await manager.InitializeAsync();
            manager.GetConfiguration().PathState.PreviousDetectedSnapshot = new PathSnapshot { AddonNoMountPath = old.NoMountFilePath };
            var viewModel = new PathHealthViewModel(manager);
            Assert.Equal(1, viewModel.AddonNoMountMigrationCount);
            var approved = viewModel.AddonNoMountMigrationPlan;

            old.WriteDisabledIds(new[] { "100", "200" });
            viewModel.Refresh();
            var stale = await viewModel.MigrateAddonNoMountAsync(approved);

            Assert.True(stale.RequiresNewPreview);
            Assert.Equal(1, stale.SkippedCount);
            Assert.Equal(new[] { "900" }, current.ReadSnapshot().DisabledIds);
            Assert.Equal(2, viewModel.AddonNoMountMigrationCount);
            Assert.Equal(2, (await viewModel.MigrateAddonNoMountAsync()).ChangedCount);
            Assert.Equal(new[] { "100", "200", "900" }, current.ReadSnapshot().DisabledIds);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
