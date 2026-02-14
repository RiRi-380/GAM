using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public sealed class VersionRestoreDetailsViewModel : ViewModelBase
    {
        private readonly ObservableCollection<AddonItemViewModel> _addonsToSubscribe;
        private readonly ObservableCollection<AddonItemViewModel> _addonsToUnsubscribe;
        private bool _disposed;

        public ObservableCollection<AddonItemViewModel> AddonsToSubscribe => _addonsToSubscribe;
        public ObservableCollection<AddonItemViewModel> AddonsToUnsubscribe => _addonsToUnsubscribe;
        public bool ShowSubscribeActions { get; }

        public VersionRestoreDetailsViewModel(
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool showSubscribeActions)
        {
            _addonsToSubscribe = new ObservableCollection<AddonItemViewModel>();
            _addonsToUnsubscribe = new ObservableCollection<AddonItemViewModel>();
            ShowSubscribeActions = showSubscribeActions;

            if (ShowSubscribeActions)
            {
                _ = LoadAddonsAsync(addonsToSubscribe, addonsToUnsubscribe);
            }
        }

        private async Task LoadAddonsAsync(List<string> toSubscribe, List<string> toUnsubscribe)
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                var addonManager = ViewModelLocator.AddonManager;
                var hybridService = ViewModelLocator.HybridWorkshopService;
                if (addonManager == null || hybridService == null)
                {
                    return;
                }

                var allAddonIds = toSubscribe.Concat(toUnsubscribe).Distinct().ToList();
                var workshopDetailsDict = await hybridService.GetWorkshopDetailsBatchAsync(allAddonIds);
                if (_disposed)
                {
                    return;
                }

                foreach (var addonId in toSubscribe)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var addonViewModel = CreateAddonViewModel(addonId, addonManager, workshopDetailsDict);
                    if (_disposed)
                    {
                        addonViewModel.Dispose();
                        return;
                    }

                    _addonsToSubscribe.Add(addonViewModel);
                }

                foreach (var addonId in toUnsubscribe)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var addonViewModel = CreateAddonViewModel(addonId, addonManager, workshopDetailsDict);
                    if (_disposed)
                    {
                        addonViewModel.Dispose();
                        return;
                    }

                    _addonsToUnsubscribe.Add(addonViewModel);
                }
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("VersionRestoreDetailsViewModel.LoadAddonsAsync", ex);
            }
        }

        private AddonItemViewModel CreateAddonViewModel(
            string addonId,
            AddonManager addonManager,
            Dictionary<string, WorkshopItemDetails> workshopDetailsDict)
        {
            var workshopAddon = new WorkshopAddon(addonId, string.Empty)
            {
                Title = AddonTitleHelper.BuildPlaceholderTitle(addonId),
                NeedsTitleUpdate = true
            };

            var addonViewModel = new AddonItemViewModel(workshopAddon, addonManager);
            workshopDetailsDict.TryGetValue(addonId, out var workshopDetails);

            if (workshopDetails != null)
            {
                var updatedAddon = new WorkshopAddon
                {
                    Id = addonId,
                    Title = workshopDetails.Title ?? AddonTitleHelper.BuildPlaceholderTitle(addonId),
                    Description = workshopDetails.Description ?? string.Empty,
                    Author = workshopDetails.Creator ?? string.Empty,
                    ThumbnailUrl = workshopDetails.PreviewUrl ?? string.Empty,
                    Size = (long)workshopDetails.FileSize,
                    LastUpdated = DateTimeOffset.FromUnixTimeSeconds(workshopDetails.TimeUpdated).DateTime
                };

                addonViewModel.UpdateFromWorkshopAddon(updatedAddon);
            }

            return addonViewModel;
        }

        public void Release()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeAddonItems(_addonsToSubscribe);
            DisposeAddonItems(_addonsToUnsubscribe);
            _addonsToSubscribe.Clear();
            _addonsToUnsubscribe.Clear();
        }

        private static void DisposeAddonItems(IEnumerable<AddonItemViewModel> addons)
        {
            foreach (var addon in addons)
            {
                addon.Dispose();
            }
        }
    }
}
