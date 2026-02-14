using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views
{
    public partial class NewAddonCheckWindow : Window
    {
        private readonly AddonManager? addonManager;
        private readonly Stopwatch stopwatch;
        private readonly DispatcherTimer timer;
        private int newAddonCount = 0;
        private int processedCount = 0;
        
        public NewAddonCheckWindow()
        {
            InitializeComponent();
            this.stopwatch = new Stopwatch();
            
            // Setup timer for elapsed time display
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += UpdateElapsedTime;
            UpdateInfoText();
        }

        public NewAddonCheckWindow(AddonManager addonManager)
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
            UpdateInfoText();
        }

        private void UpdateInfoText()
        {
            if (addonManager != null && addonManager.DisableMode == DisableMode.Soft)
            {
                InfoText.Text = L.Get("NewAddonCheck.InfoSoft");
            }
            else
            {
                InfoText.Text = L.Get("NewAddonCheck.Info");
            }
        }
        
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _ = CheckForNewAddons();
        }
        
        private async Task CheckForNewAddons()
        {
            if (addonManager == null)
            {
                return;
            }
            stopwatch.Start();
            timer.Start();
            
            try
            {
                UpdateStatus(L.Get("NewAddonCheck.CheckingForNew"));
                ProgressBar.IsIndeterminate = true;
                
                // Get current configuration
                var config = addonManager.GetConfiguration();
                var existingAddonIds = new HashSet<string>();
                
                // Collect all known addon IDs from metadata
                foreach (var kvp in config.AddonMetadata)
                {
                    existingAddonIds.Add(kvp.Key);
                }
                
                AddDetail(L.Format("NewAddonCheck.FoundExisting", existingAddonIds.Count));
                
                // タイトル更新が必要な既存アドオンをチェック
                var addonsNeedingTitleUpdate = config.AddonMetadata
                    .Where(kvp => kvp.Value.NeedsTitleUpdate || 
                           (kvp.Value.IsGmaFile && (kvp.Value.Title == kvp.Key || AddonTitleHelper.IsPlaceholderTitle(kvp.Value.Title))))
                    .ToList();
                
                if (addonsNeedingTitleUpdate.Count > 0)
                {
                    UpdateStatus(L.Get("NewAddonCheck.UpdatingTitles"));
                    AddDetail(L.Format("NewAddonCheck.UpdatingTitleCount", addonsNeedingTitleUpdate.Count));
                    await addonManager.UpdateCacheAddonTitlesAsync();
                }
                
                // Scan workshop folder for new addons
                UpdateStatus(L.Get("NewAddonCheck.ScanningWorkshop"));
                var newAddons = await addonManager.ScanForNewAddonsAsync();
                newAddonCount = newAddons.Count;
                UpdateNewCount(newAddonCount);
                
                if (newAddonCount == 0 && addonsNeedingTitleUpdate.Count == 0)
                {
                    UpdateStatus(L.Get("NewAddonCheck.NoNewFound"));
                    AddDetail(L.Get("NewAddonCheck.AllRegistered"));
                    await Task.Delay(2000);
                    Close(true);
                    return;
                }
                
                AddDetail(L.Format("NewAddonCheck.FoundNewCount", newAddonCount));
                UpdateStatus(L.Format("NewAddonCheck.ProcessingNew", newAddonCount));
                ProgressBar.IsIndeterminate = false;
                
                var isHardMode = addonManager.DisableMode == DisableMode.Hard;

                // Process only new addons
                foreach (var addon in newAddons)
                {
                    processedCount++;
                    UpdateProgress(processedCount, newAddonCount);
                    
                    // Check if it's a GMA file or folder
                    if (!isHardMode)
                    {
                        AddDetail(L.Format("NewAddonCheck.RegisteringAddon", addon.Title ?? addon.Id));
                    }
                    else if (addon.IsGmaFile)
                    {
                        AddDetail(L.Format("NewAddonCheck.CreatingHardLink", addon.Title ?? addon.Id));
                    }
                    else
                    {
                        AddDetail(L.Format("NewAddonCheck.CreatingJunction", addon.Title ?? addon.Id));
                    }
                    
                    // The actual junction/hardlink creation happens in MigrateExistingAddonsAsync
                    await Task.Delay(50); // Small delay to show progress
                }
                
                // Add new addons to configuration
                foreach (var addon in newAddons)
                {
                    config.AddonMetadata[addon.Id] = addon;
                }
                
                // Run migration for new addons only
                UpdateStatus(isHardMode ? L.Get("NewAddonCheck.CreatingLinks") : L.Get("NewAddonCheck.Registering"));
                var newAddonIds = new HashSet<string>(newAddons.Select(a => a.Id));
                await addonManager.MigrateExistingAddonsAsync(newAddonIds);
                
                // 新規GMAアドオンの名前を更新
                var newGmaAddons = newAddons.Where(a => a.IsGmaFile).ToList();
                if (newGmaAddons.Count > 0)
                {
                    UpdateStatus(L.Get("NewAddonCheck.UpdatingTitles"));
                    ProgressBar.IsIndeterminate = false;
                    processedCount = 0;
                    
                    var titleUpdateProgress = new Progress<(int current, int total, string message)>(report =>
                    {
                        UpdateProgress(report.current, report.total);
                        AddDetail(report.message);
                    });
                    
                    await addonManager.UpdateCacheAddonTitlesAsync(titleUpdateProgress);
                }
                
                UpdateStatus(L.Format("NewAddonCheck.SuccessProcessed", newAddonCount));
                await Task.Delay(2000);
                
                // Close and continue
                Close(true);
            }
            catch (Exception ex)
            {
                UpdateStatus(L.Format("NewAddonCheck.Error", ex.Message));
                AddDetail(L.Format("NewAddonCheck.ErrorDetails", ex));
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
        
        private void UpdateNewCount(int count)
        {
            Dispatcher.UIThread.Post(() => NewCountText.Text = count.ToString());
        }
        
        private void UpdateElapsedTime(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var elapsed = stopwatch.Elapsed;
                ElapsedTimeText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            timer.Stop();
            timer.Tick -= UpdateElapsedTime;
            base.OnClosed(e);
        }
    }
}
