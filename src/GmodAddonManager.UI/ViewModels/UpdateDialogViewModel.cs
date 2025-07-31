using System;
using System.Threading.Tasks;
using System.Reactive;
using ReactiveUI;
using System.Reflection;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public class UpdateDialogViewModel : ViewModelBase
    {
        private readonly UpdateService updateService;
        private readonly UpdateInfo updateInfo;
        private bool isUpdating;
        private string updateProgress = string.Empty;
        
        public UpdateDialogViewModel(UpdateService updateService, UpdateInfo updateInfo)
        {
            this.updateService = updateService;
            this.updateInfo = updateInfo;
            
            UpdateCommand = ReactiveCommand.CreateFromTask(UpdateAsync);
            RemindLaterCommand = ReactiveCommand.Create(() => { DialogResult = false; });
        }
        
        public string CurrentVersion => $"現在のバージョン: v{Assembly.GetExecutingAssembly().GetName().Version}";
        public string NewVersion => $"最新バージョン: {updateInfo.Version}";
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
            UpdateProgress = "アップデートをダウンロード中...";
            
            try
            {
                await updateService.DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("アップデートに失敗しました。後でもう一度お試しください。");
                IsUpdating = false;
            }
        }
        
        private async Task ShowErrorAsync(string message)
        {
            UpdateProgress = message;
            await Task.Delay(3000);
            DialogResult = false;
        }
    }
}