using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views
{
    public partial class InitialLoadingWindow : Window
    {
        private readonly AddonManager addonManager;
        private readonly Stopwatch stopwatch;
        private readonly DispatcherTimer timer;
        private int processedCount = 0;
        private int totalCount = 0;
        
        public InitialLoadingWindow(AddonManager addonManager)
        {
            InitializeComponent();
            this.addonManager = addonManager;
            this.stopwatch = new Stopwatch();
            
            // Setup timer for elapsed time display
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += UpdateElapsedTime;
        }
        
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _ = PerformInitialLoad();
        }
        
        private async Task PerformInitialLoad()
        {
            stopwatch.Start();
            timer.Start();
            
            try
            {
                // Initialize addon manager
                UpdateStatus(L.Get("InitialLoading.InitializingManager"));
                await addonManager.InitializeAsync();
                
                // Migrate existing addons (this includes junction/hardlink creation)
                UpdateStatus(L.Get("InitialLoading.MigratingAddons"));
                AddDetail(L.Get("InitialLoading.CreatingJunctions"));
                AddDetail(L.Get("InitialLoading.SettingUpHardLinks"));
                
                // Count total items
                var workshopAddons = addonManager.GetAllAddons();
                totalCount = workshopAddons?.Count ?? 0;
                UpdateTotal(totalCount);
                
                // Scan workshop folder
                UpdateStatus(L.Get("InitialLoading.ScanningWorkshop"));
                ProgressBar.IsIndeterminate = false;
                
                var addons = await addonManager.ScanWorkshopFolderAsync();
                
                // Process each addon
                foreach (var addon in addons)
                {
                    processedCount++;
                    UpdateProgress(processedCount, totalCount);
                    AddDetail(L.Format("InitialLoading.ProcessedAddon", addon.Title ?? addon.Id));
                    
                    // Small delay to show progress
                    await Task.Delay(10);
                }
                
                // キャッシュアドオンの名前を更新
                UpdateStatus(L.Get("InitialLoading.UpdatingTitles"));
                ProgressBar.IsIndeterminate = false;
                
                var titleUpdateProgress = new Progress<(int current, int total, string message)>(report =>
                {
                    UpdateProgress(report.current, report.total);
                    AddDetail(report.message);
                });
                
                await addonManager.UpdateCacheAddonTitlesAsync(titleUpdateProgress);
                
                UpdateStatus(L.Get("InitialLoading.Complete"));
                await Task.Delay(1000);
                
                // Close and continue
                Close(true);
            }
            catch (Exception ex)
            {
                UpdateStatus(L.Format("InitialLoading.Error", ex.Message));
                AddDetail(L.Format("InitialLoading.ErrorDetails", ex));
                await Task.Delay(5000);
                Close(false);
            }
            finally
            {
                stopwatch.Stop();
                timer.Stop();
            }
        }
        
        private void UpdateStatus(string message)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = message);
        }
        
        private void AddDetail(string detail)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(DetailText.Text))
                    DetailText.Text += "\n";
                DetailText.Text += $"[{DateTime.Now:HH:mm:ss}] {detail}";
            });
        }
        
        private void UpdateProgress(int current, int total)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ProcessedCountText.Text = current.ToString();
                if (total > 0)
                {
                    ProgressBar.Value = (double)current / total * 100;
                }
            });
        }
        
        private void UpdateTotal(int total)
        {
            Dispatcher.UIThread.Post(() => TotalCountText.Text = total.ToString());
        }
        
        private void UpdateElapsedTime(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var elapsed = stopwatch.Elapsed;
                ElapsedTimeText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
            });
        }
    }
}