using Avalonia.Headless.XUnit;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Tests;

public sealed class AddonGridSelectionTests
{
    [AvaloniaFact]
    public async Task FilteringKeepsSelectionAndClearingAlsoClearsHiddenCards()
    {
        var root = Path.Combine(Path.GetTempPath(), "gam-grid-selection", Guid.NewGuid().ToString("N"));
        var workshop = Path.Combine(root, "steamapps", "workshop", "content", "4000");
        var gmod = Path.Combine(root, "steamapps", "common", "GarrysMod");
        var manifest = Path.Combine(root, "appworkshop_4000.acf");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(gmod);
        File.WriteAllText(manifest, """ "AppWorkshop" { "WorkshopItemDetails" {} "WorkshopItemsInstalled" {} } """);
        try
        {
            using var manager = new AddonManager(new AddonManagerOptions
            {
                CustomWorkshopPath = workshop,
                CustomAppDataPath = Path.Combine(root, "profile"),
                CustomGmodInstallPath = gmod,
                CustomWorkshopCacheFilePaths = [manifest],
                DisableCacheScan = true
            });
            await manager.InitializeAsync();
            using var watcher = new GmodProcessWatcher();
            var pending = new PendingChangeManager(manager, manager.GetManagerPath());
            using var grid = new AddonGridViewModel(manager, pending, watcher, Path.Combine(root, "sort.json"));
            var alpha = Add("101", "Alpha");
            var bravo = Add("102", "Bravo");
            var charlie = Add("103", "Charlie");
            var local = Add("local:fixture", "Local", isLocal: true);

            grid.ApplyFilter();
            Assert.Contains(alpha, grid.FilteredAddons);
            grid.IsSelectionMode = true;
            grid.SelectAddon(alpha.AddonId);
            grid.SelectAddon(bravo.AddonId);
            grid.SelectAddon(local.AddonId);

            grid.FilterText = "Alpha";
            grid.ApplyFilter();
            Assert.Equal(2, grid.SelectedAddonsCount);
            Assert.Equal(1, grid.HiddenSelectedAddonsCount);
            Assert.True(grid.HasHiddenSelectedAddons);
            Assert.Equal(new[] { alpha, bravo }, grid.GetSelectedAddons());
            Assert.Equal(new[] { alpha.AddonId, bravo.AddonId }, grid.GetDraggedAddonIds(alpha));

            grid.ClearSelection();
            Assert.False(bravo.IsSelected);
            grid.FilterText = string.Empty;
            grid.ApplyFilter();
            grid.SelectAddon(charlie.AddonId);

            Assert.Equal(1, grid.SelectedAddonsCount);
            Assert.Equal(0, grid.HiddenSelectedAddonsCount);
            Assert.Equal(new[] { charlie }, grid.GetSelectedAddons());

            AddonItemViewModel Add(string id, string title, bool isLocal = false)
            {
                var item = new AddonItemViewModel(new WorkshopAddon
                {
                    Id = id,
                    Title = title,
                    IsLocal = isLocal,
                    NeedsTitleUpdate = false
                }, manager);
                grid.AllAddons.Add(item);
                return item;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
