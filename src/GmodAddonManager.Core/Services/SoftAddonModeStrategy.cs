using System.Collections.Generic;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    internal sealed class SoftAddonModeStrategy : IAddonModeStrategy
    {
        public DisableMode Mode => DisableMode.Soft;
        public bool RequiresAdmin => false;

        public Task InitializeAsync(AddonManager manager)
        {
            manager.EnsureDataDirectory();
            manager.TryMigrateLegacyManagerData();
            return Task.CompletedTask;
        }

        public Task<List<WorkshopAddon>> ScanWorkshopFolderAsync(AddonManager manager)
        {
            return manager.ScanWorkshopFolderSoftAsync();
        }

        public Task MigrateExistingAddonsAsync(AddonManager manager, HashSet<string>? addonIdsToProcess)
        {
            _ = addonIdsToProcess;
            return Task.CompletedTask;
        }

        public void EnableAddon(AddonManager manager, string addonId)
        {
            manager.EnableAddonSoft(addonId);
        }

        public void DisableAddon(AddonManager manager, string addonId)
        {
            manager.DisableAddonSoft(addonId);
        }

        public Task ValidateSystemIntegrityAsync(AddonManager manager)
        {
            return Task.CompletedTask;
        }

        public Task CleanupUnsubscribedAddonsAsync(AddonManager manager)
        {
            return Task.CompletedTask;
        }
    }
}
