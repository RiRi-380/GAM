using System;
using System.Threading.Tasks;
using System.Reactive;
using ReactiveUI;
using System.Reflection;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public sealed class UpdateDialogViewModel : ViewModelBase
    {
        private readonly UpdateService updateService;
        private readonly UpdateInfo updateInfo;
        private bool isUpdating;
        private string updateProgress = string.Empty;
        private bool disposed;
        
        public UpdateDialogViewModel(UpdateService updateService, UpdateInfo updateInfo)
        {
            this.updateService = updateService;
            this.updateInfo = updateInfo;
            
            UpdateCommand = ReactiveCommand.CreateFromTask(UpdateAsync);
            RemindLaterCommand = ReactiveCommand.Create(() => { DialogResult = false; });
            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        }
        
        public string CurrentVersion => L.Format(
            "UpdateDialog.CurrentVersionFormat",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
        public string NewVersion => L.Format("UpdateDialog.NewVersionFormat", updateInfo.Version);
        public string ReleaseNotes => updateInfo.ReleaseNotes;
        
        public bool IsUpdating
        {
            get => isUpdating;
            set => this.RaiseAndSetIfChanged(ref isUpdating, value);
        }
        
        public string UpdateProgress
        {
            get => updateProgress;
            set => this.RaiseAndSetIfChanged(ref updateProgress, value);
        }
        
        public bool DialogResult { get; private set; }
        
        public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
        public ReactiveCommand<Unit, Unit> RemindLaterCommand { get; }
        
        private async Task UpdateAsync()
        {
            IsUpdating = true;
            UpdateProgress = L.Get("UpdateDialog.ProgressDownloading");
            
            try
            {
                await updateService.DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
            }
            catch (Exception)
            {
                await ShowErrorAsync(L.Get("UpdateDialog.UpdateFailed"));
                IsUpdating = false;
            }
        }
        
        private async Task ShowErrorAsync(string message)
        {
            UpdateProgress = message;
            await Task.Delay(3000);
            DialogResult = false;
        }

        private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
            {
                this.RaisePropertyChanged(nameof(CurrentVersion));
                this.RaisePropertyChanged(nameof(NewVersion));
                if (IsUpdating)
                {
                    UpdateProgress = L.Get("UpdateDialog.ProgressDownloading");
                }
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
