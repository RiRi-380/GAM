using System;
using System.Threading.Tasks;
using System.Reactive;
using ReactiveUI;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI;
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
            RemindLaterCommand = ReactiveCommand.Create(() => RequestClose(false));
            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        }
        
        public string CurrentVersion => L.Format(
            "UpdateDialog.CurrentVersionFormat",
            UpdateService.NormalizeVersionLabel(GetCurrentVersion()));
        public string NewVersion => L.Format(
            "UpdateDialog.NewVersionFormat",
            UpdateService.NormalizeVersionLabel(updateInfo.Version));
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
        public event EventHandler<bool?>? CloseRequested;
        
        private async Task UpdateAsync()
        {
            IsUpdating = true;
            UpdateProgress = L.Get("UpdateDialog.ProgressDownloading");
            
            try
            {
                await updateService.DownloadAndInstallUpdateAsync(
                    updateInfo.DownloadUrl,
                    updateInfo.DownloadDigest);

                DialogResult = true;
                RequestClose(true);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            catch (TimeoutException)
            {
                await ShowErrorAsync(L.Get("UpdateDialog.UpdateTimedOut"));
                IsUpdating = false;
            }
            catch (OperationCanceledException)
            {
                await ShowErrorAsync(L.Get("UpdateDialog.UpdateTimedOut"));
                IsUpdating = false;
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("UpdateDialogViewModel.UpdateAsync", ex);
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

        private void RequestClose(bool? result)
        {
            DialogResult = result == true;
            CloseRequested?.Invoke(this, result);
        }

        private static string? GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
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
