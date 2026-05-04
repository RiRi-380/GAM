using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels
{
    public class UpdateDialogViewModel : ViewModelBase
    {
        private readonly UpdateService updateService;
        private readonly UpdateInfo updateInfo;
        private bool isUpdating;
        private string updateProgress = string.Empty;
        private bool isDownloadProgressIndeterminate = true;
        private double downloadProgressValue;
        private CancellationTokenSource? updateCancellationTokenSource;

        public UpdateDialogViewModel(UpdateService updateService, UpdateInfo updateInfo)
        {
            this.updateService = updateService;
            this.updateInfo = updateInfo;

            UpdateCommand = ReactiveCommand.CreateFromTask(UpdateAsync);
            CancelUpdateCommand = ReactiveCommand.Create(CancelUpdate);
            RemindLaterCommand = ReactiveCommand.Create(RequestClose);
        }

        public string CurrentVersion => $"現在のバージョン: {ApplicationVersionProvider.GetDisplayVersion()}";
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

        public bool IsDownloadProgressIndeterminate
        {
            get => isDownloadProgressIndeterminate;
            set => this.RaiseAndSetIfChanged(ref isDownloadProgressIndeterminate, value);
        }

        public double DownloadProgressValue
        {
            get => downloadProgressValue;
            set => this.RaiseAndSetIfChanged(ref downloadProgressValue, value);
        }

        public bool DialogResult { get; private set; }
        public event EventHandler? CloseRequested;

        public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelUpdateCommand { get; }
        public ReactiveCommand<Unit, Unit> RemindLaterCommand { get; }

        private async Task UpdateAsync()
        {
            updateCancellationTokenSource?.Dispose();
            updateCancellationTokenSource = new CancellationTokenSource();

            IsUpdating = true;
            IsDownloadProgressIndeterminate = true;
            DownloadProgressValue = 0;
            UpdateProgress = "アップデートをダウンロード中...";

            try
            {
                var progress = new Progress<UpdateDownloadProgress>(report =>
                {
                    IsDownloadProgressIndeterminate = report.TotalBytes is not long totalBytes || totalBytes <= 0;
                    DownloadProgressValue = report.Percentage ?? 0;
                    UpdateProgress = UpdateService.FormatDownloadProgress(report);
                });

                await updateService.DownloadAndInstallUpdateAsync(
                    updateInfo,
                    progress,
                    updateCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                await ShowTransientStatusAsync("アップデートをキャンセルしました。");
                IsUpdating = false;
            }
            catch (TimeoutException)
            {
                await ShowTransientStatusAsync("ダウンロードが一定時間進まなかったため中止しました。ネットワークを確認してもう一度お試しください。");
                IsUpdating = false;
            }
            catch (Exception)
            {
                await ShowTransientStatusAsync("アップデートに失敗しました。しばらくしてからもう一度お試しください。");
                IsUpdating = false;
            }
            finally
            {
                updateCancellationTokenSource?.Dispose();
                updateCancellationTokenSource = null;
            }
        }

        private void CancelUpdate()
        {
            updateCancellationTokenSource?.Cancel();
        }

        private async Task ShowTransientStatusAsync(string message)
        {
            UpdateProgress = message;
            await Task.Delay(3000);
        }

        private void RequestClose()
        {
            DialogResult = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
