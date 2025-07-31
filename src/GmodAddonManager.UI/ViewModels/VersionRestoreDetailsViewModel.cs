using ReactiveUI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using System;

namespace GmodAddonManager.UI.ViewModels
{
    public class VersionRestoreDetailsViewModel : ViewModelBase
    {
        private readonly ObservableCollection<AddonItemViewModel> _addonsToSubscribe;
        private readonly ObservableCollection<AddonItemViewModel> _addonsToUnsubscribe;
        
        public ObservableCollection<AddonItemViewModel> AddonsToSubscribe => _addonsToSubscribe;
        public ObservableCollection<AddonItemViewModel> AddonsToUnsubscribe => _addonsToUnsubscribe;
        
        public VersionRestoreDetailsViewModel(
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe)
        {
            _addonsToSubscribe = new ObservableCollection<AddonItemViewModel>();
            _addonsToUnsubscribe = new ObservableCollection<AddonItemViewModel>();
            
            // アドオン情報を非同期で読み込む
            _ = LoadAddonsAsync(addonsToSubscribe, addonsToUnsubscribe);
        }
        
        private async Task LoadAddonsAsync(List<string> toSubscribe, List<string> toUnsubscribe)
        {
            try
            {
                // AddonManagerとHybridWorkshopServiceを取得
                var addonManager = ViewModelLocator.AddonManager;
                var hybridService = ViewModelLocator.HybridWorkshopService;
                
                if (addonManager == null || hybridService == null)
                {
                    // [VersionRestoreDetails] Services not available
                    return;
                }
                
                // 全てのアドオンIDをまとめて取得（効率化）
                var allAddonIds = toSubscribe.Concat(toUnsubscribe).Distinct().ToList();
                // [VersionRestoreDetails] Loading details for {allAddonIds.Count} addons
                
                // バッチでWorkshop詳細を取得
                var workshopDetailsList = await hybridService.GetWorkshopDetailsBatchAsync(allAddonIds);
                
                // IDと詳細のペアで管理
                var workshopDetailsDict = new Dictionary<string, WorkshopItemDetails>();
                for (int i = 0; i < allAddonIds.Count && i < workshopDetailsList.Count; i++)
                {
                    workshopDetailsDict[allAddonIds[i]] = workshopDetailsList[i];
                }
                
                // サブスクライブするアドオンの情報を作成
                foreach (var addonId in toSubscribe)
                {
                    var addonViewModel = CreateAddonViewModel(addonId, addonManager, workshopDetailsDict);
                    _addonsToSubscribe.Add(addonViewModel);
                }
                
                // サブスクライブ解除するアドオンの情報を作成
                foreach (var addonId in toUnsubscribe)
                {
                    var addonViewModel = CreateAddonViewModel(addonId, addonManager, workshopDetailsDict);
                    _addonsToUnsubscribe.Add(addonViewModel);
                }
            }
            catch (Exception ex)
            {
                // [VersionRestoreDetails] Error loading addons: {ex.Message}
            }
        }
        
        private AddonItemViewModel CreateAddonViewModel(
            string addonId,
            AddonManager addonManager,
            Dictionary<string, WorkshopItemDetails> workshopDetailsDict)
        {
            // WorkshopAddonオブジェクトを作成
            var workshopAddon = new WorkshopAddon(addonId, "")
            {
                Title = $"Workshop-{addonId}",
                NeedsTitleUpdate = true
            };
            
            // AddonItemViewModelを作成（メインウィンドウと同じ方法）
            var addonViewModel = new AddonItemViewModel(workshopAddon, addonManager);
            
            // Workshop詳細情報で更新（メインウィンドウと同じロジック）
            workshopDetailsDict.TryGetValue(addonId, out var workshopDetails);
            
            if (workshopDetails != null)
            {
                // WorkshopAddonオブジェクトに変換して更新
                var updatedAddon = new WorkshopAddon
                {
                    Id = addonId,
                    Title = workshopDetails.Title ?? $"Workshop-{addonId}",
                    Description = workshopDetails.Description,
                    Author = workshopDetails.Creator,
                    ThumbnailUrl = workshopDetails.PreviewUrl,
                    Size = 0, // WorkshopItemDetailsにはサイズ情報がない
                    LastUpdated = DateTimeOffset.FromUnixTimeSeconds(workshopDetails.TimeUpdated).DateTime
                };
                
                // AddonItemViewModelを更新
                addonViewModel.UpdateFromWorkshopAddon(updatedAddon);
            }
            else
            {
                // [VersionRestoreDetails] No workshop details found for {addonId}
            }
            
            return addonViewModel;
        }
    }
}