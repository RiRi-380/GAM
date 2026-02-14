using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.Services
{
    /// <summary>
    /// Web API + ローカルキャッシュを組み合わせたWorkshopサービス
    /// </summary>
    public class HybridWorkshopService
    {
        private readonly SteamWorkshopService _webApi;
        private readonly WorkshopInstallIndex _installIndex;

        public HybridWorkshopService(SteamWorkshopService webApi)
        {
            _webApi = webApi ?? throw new ArgumentNullException(nameof(webApi));
            _installIndex = new WorkshopInstallIndex(new SteamPathDetector());
        }

        public Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId)
        {
            return _webApi.GetWorkshopDetailsAsync(workshopId);
        }

        public Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId, bool treatAsHot)
        {
            return _webApi.GetWorkshopDetailsAsync(workshopId, treatAsHot);
        }

        public Task<Dictionary<string, WorkshopItemDetails>> GetWorkshopDetailsBatchAsync(List<string> workshopIds)
        {
            if (workshopIds == null || workshopIds.Count == 0)
            {
                return Task.FromResult(new Dictionary<string, WorkshopItemDetails>());
            }

            return _webApi.GetWorkshopDetailsBatchAsync(workshopIds);
        }

        public Task<Dictionary<string, WorkshopItemDetails>> GetWorkshopDetailsBatchAsync(List<string> workshopIds, bool treatAsHot)
        {
            if (workshopIds == null || workshopIds.Count == 0)
            {
                return Task.FromResult(new Dictionary<string, WorkshopItemDetails>());
            }

            return _webApi.GetWorkshopDetailsBatchAsync(workshopIds, default, treatAsHot);
        }

        public Task<List<string>> GetSubscribedItemsAsync()
        {
            return Task.Run(() => SteamWorkshopCacheReader.GetSubscribedAddonIds());
        }

        public bool IsItemInstalled(string workshopId)
        {
            return _installIndex.IsInstalled(workshopId);
        }

        public string? GetItemInstallPath(string workshopId)
        {
            return _installIndex.TryGetInstallPath(workshopId, out var path) ? path : null;
        }
    }
}
