using ReactiveUI;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public sealed class VersionRestoreConfirmViewModel : ViewModelBase
    {
        public string ConfirmMessage { get; }
        public List<string> AddonsToSubscribe { get; }
        public List<string> AddonsToUnsubscribe { get; }
        public bool HasChanges { get; }
        public bool ShowSubscribeInfo { get; }
        private bool disposed;

        public string SubscribeCountText => L.Format("VersionRestoreConfirm.SubscribeCountFormat", AddonsToSubscribe.Count);
        public string UnsubscribeCountText => L.Format("VersionRestoreConfirm.UnsubscribeCountFormat", AddonsToUnsubscribe.Count);
        public string ManualSyncNote => L.Get("VersionRestoreConfirm.ManualSyncNote");
        
        public VersionRestoreConfirmViewModel(
            string confirmMessage,
            List<string> addonsToSubscribe,
            List<string> addonsToUnsubscribe,
            bool showSubscribeInfo)
        {
            ConfirmMessage = confirmMessage;
            AddonsToSubscribe = addonsToSubscribe ?? new List<string>();
            AddonsToUnsubscribe = addonsToUnsubscribe ?? new List<string>();
            HasChanges = AddonsToSubscribe.Any() || AddonsToUnsubscribe.Any();
            ShowSubscribeInfo = HasChanges && showSubscribeInfo;

            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        }

        private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
            {
                this.RaisePropertyChanged(nameof(SubscribeCountText));
                this.RaisePropertyChanged(nameof(UnsubscribeCountText));
                this.RaisePropertyChanged(nameof(ManualSyncNote));
            }
        }

        public void Release()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        }
    }
}
