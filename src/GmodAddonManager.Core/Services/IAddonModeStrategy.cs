using System.Collections.Generic;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    internal interface IAddonModeStrategy
    {
        DisableMode Mode { get; }
        bool RequiresAdmin { get; }
        Task InitializeAsync(AddonManager manager);
        Task<List<WorkshopAddon>> ScanWorkshopFolderAsync(AddonManager manager);
        Task MigrateExistingAddonsAsync(AddonManager manager, HashSet<string>? addonIdsToProcess);
        void EnableAddon(AddonManager manager, string addonId);
        void DisableAddon(AddonManager manager, string addonId);
        Task ValidateSystemIntegrityAsync(AddonManager manager);
        Task CleanupUnsubscribedAddonsAsync(AddonManager manager);
    }
}
