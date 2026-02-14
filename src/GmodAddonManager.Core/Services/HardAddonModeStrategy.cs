using System.Collections.Generic;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    internal sealed class HardAddonModeStrategy : IAddonModeStrategy
    {
        public DisableMode Mode => DisableMode.Hard;
        public bool RequiresAdmin => true;

        public Task InitializeAsync(AddonManager manager)
        {
            manager.EnsureManagerDirectories();
            manager.WarnIfCacheOnDifferentDrive();
            manager.EnsureCacheManagerDirectories();
            return Task.CompletedTask;
        }

        public Task<List<WorkshopAddon>> ScanWorkshopFolderAsync(AddonManager manager)
        {
            return manager.ScanWorkshopFolderHardAsync();
        }

        public Task MigrateExistingAddonsAsync(AddonManager manager, HashSet<string>? addonIdsToProcess)
        {
            return manager.MigrateExistingAddonsHardAsync(addonIdsToProcess);
        }

        public void EnableAddon(AddonManager manager, string addonId)
        {
            manager.EnableAddonHard(addonId);
        }

        public void DisableAddon(AddonManager manager, string addonId)
        {
            manager.DisableAddonHard(addonId);
        }

        public Task ValidateSystemIntegrityAsync(AddonManager manager)
        {
            return manager.ValidateSystemIntegrityHardAsync();
        }

        public Task CleanupUnsubscribedAddonsAsync(AddonManager manager)
        {
            return manager.CleanupUnsubscribedAddonsHardAsync();
        }
    }
}
