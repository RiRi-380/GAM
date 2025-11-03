using ReactiveUI;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.UI.ViewModels
{
    public class VersionRestoreConfirmViewModel : ViewModelBase
    {
        public string ConfirmMessage { get; }
        public List<string> AddonsToSubscribe { get; }
        public List<string> AddonsToUnsubscribe { get; }
        public bool IsSteamworksAvailable { get; }
        public bool HasChanges { get; }
        
        public VersionRestoreConfirmViewModel(
            string confirmMessage,
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool isSteamworksAvailable)
        {
            ConfirmMessage = confirmMessage;
            AddonsToSubscribe = addonsToSubscribe ?? new List<string>();
            AddonsToUnsubscribe = addonsToUnsubscribe ?? new List<string>();
            IsSteamworksAvailable = isSteamworksAvailable;
            HasChanges = AddonsToSubscribe.Any() || AddonsToUnsubscribe.Any();
        }
    }
}