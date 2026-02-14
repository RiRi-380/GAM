using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Security.Cryptography;
using ReactiveUI;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        private readonly AddonManager _addonManager;
        
        private int _totalAddons;
        private int _enabledAddons;
        private int _disabledAddons;
        private string _totalSize = "0 B";
        private string _currentTip = "";
        
        public DashboardViewModel(AddonManager addonManager)
        {
            _addonManager = addonManager;
            
            // 繧ｳ繝槭Φ繝峨・蛻晄悄蛹・
            CheckNewAddonsCommand = ReactiveCommand.Create(() =>
            {
                var mainViewModel = ViewModelLocator.MainWindowViewModel;
                if (mainViewModel?.RefreshCommand != null)
                {
                    mainViewModel.RefreshCommand.Execute().Subscribe(
                        _ => { },
                        ex => SafeFileLogger.TryLogException("DashboardViewModel.CheckNewAddonsCommand", ex));
                }
            });
            
            UpdateStatistics();
            LoadRecentActivities();
            ShowRandomTip();
        }
        
        public string Version => $"v{GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        
        public int TotalAddons
        {
            get => _totalAddons;
            private set => SetAndRaise(ref _totalAddons, value);
        }
        
        public int EnabledAddons
        {
            get => _enabledAddons;
            private set => SetAndRaise(ref _enabledAddons, value);
        }
        
        public int DisabledAddons
        {
            get => _disabledAddons;
            private set => SetAndRaise(ref _disabledAddons, value);
        }
        
        public string TotalSize
        {
            get => _totalSize;
            private set => SetAndRaise(ref _totalSize, value);
        }
        
        public string CurrentTip
        {
            get => _currentTip;
            private set => SetAndRaise(ref _currentTip, value);
        }
        
        public ObservableCollection<RecentActivityItem> RecentActivities { get; } = new();
        
        public bool HasRecentActivity => RecentActivities.Count > 0;
        
        public ReactiveCommand<Unit, Unit> CheckNewAddonsCommand { get; }
        
        public void UpdateStatistics()
        {
            try
            {
                var config = _addonManager.GetConfiguration();
                TotalAddons = config.AddonMetadata.Count;
                
                // 邨ｱ險域ュ蝣ｱ繧定ｨ育ｮ・
                EnabledAddons = 0;
                DisabledAddons = 0;
                
                foreach (var asset in config.Assets)
                {
                    if (_addonManager.DisableMode == DisableMode.Hard && asset.Id == "junction-system-asset")
                    {
                        DisabledAddons += asset.Addons.Count;
                    }
                    else if (asset.Enabled)
                    {
                        foreach (var addonId in asset.Addons)
                        {
                            var state = asset.AddonStates.ContainsKey(addonId) 
                                ? asset.AddonStates[addonId] 
                                : asset.DefaultAddonState;
                            
                            if (state != Core.Models.AddonState.Excluded)
                            {
                                EnabledAddons++;
                            }
                        }
                    }
                }
                
                // 驥崎､・ｒ髯､蜴ｻ
                EnabledAddons = Math.Min(EnabledAddons, TotalAddons - DisabledAddons);
                
                // 繧ｵ繧､繧ｺ繧定ｨ育ｮ・
                long totalBytes = 0;
                foreach (var addon in config.AddonMetadata.Values)
                {
                    totalBytes += addon.Size;
                }
                
                TotalSize = FormatFileSize(totalBytes);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("DashboardViewModel.UpdateStatistics", ex);
            }
        }
        
        private void LoadRecentActivities()
        {
            RecentActivities.Clear();
            
            // UndoManager縺九ｉ謫堺ｽ懷ｱ･豁ｴ繧貞叙蠕・
            var undoManager = _addonManager.GetUndoManager();
            var history = undoManager.GetHistory();
            
            foreach (var action in history.Take(5))
            {
                RecentActivities.Add(new RecentActivityItem
                {
                    Time = action.Timestamp.ToString("HH:mm"),
                    Description = GetActionDescription(action)
                });
            }
            
            this.RaisePropertyChanged(nameof(HasRecentActivity));
        }
        
    private string GetActionDescription(Core.Models.UndoAction action)
    {
        var unknown = L.Get("Common.Unknown");
        return action.Type switch
        {
            Core.Models.UndoActionType.AddonAddedToAsset => L.Format("Dashboard.Activity.AddedToAsset", action.AffectedAddonIds?.Count ?? 1, action.AssetName ?? unknown),
            Core.Models.UndoActionType.AddonRemovedFromAsset => L.Format("Dashboard.Activity.RemovedFromAsset", action.AffectedAddonIds?.Count ?? 1),
            Core.Models.UndoActionType.AssetCreated => L.Format("Dashboard.Activity.CreatedAsset", action.AssetName ?? unknown),
            Core.Models.UndoActionType.AssetDeleted => L.Format("Dashboard.Activity.DeletedAsset", action.AssetName ?? unknown),
            Core.Models.UndoActionType.AssetEnabled => L.Format("Dashboard.Activity.AssetEnabled", action.AssetName ?? unknown),
            Core.Models.UndoActionType.AssetDisabled => L.Format("Dashboard.Activity.AssetDisabled", action.AssetName ?? unknown),
            Core.Models.UndoActionType.AssetExcluded => L.Format("Dashboard.Activity.AssetExcluded", action.AssetName ?? unknown),
            Core.Models.UndoActionType.AddonStateChanged => action.AffectedAddonIds != null && action.AffectedAddonIds.Count > 1
                ? L.Format("Dashboard.Activity.AddonStateChangedBatch", action.AffectedAddonIds.Count)
                : L.Format("Dashboard.Activity.AddonStateChanged",
                    !string.IsNullOrWhiteSpace(action.AddonName)
                        ? action.AddonName
                        : action.AffectedAddonIds != null && action.AffectedAddonIds.Count == 1
                            ? action.AffectedAddonIds[0]
                            : unknown),
            Core.Models.UndoActionType.AssetMerged => L.Format("Dashboard.Activity.AssetMerged", action.AssetName ?? unknown),
            _ => action.Description
        };
    }
        
        private void ShowRandomTip()
        {
            var tips = new List<string>
            {
                L.Get("Dashboard.Tip1"),
                L.Get("Dashboard.Tip2"),
                L.Get("Dashboard.Tip3"),
                L.Get("Dashboard.Tip4"),
                L.Get("Dashboard.Tip5"),
                L.Get("Dashboard.Tip8")
            };

            if (_addonManager.DisableMode == DisableMode.Hard)
            {
                tips.Add(L.Get("Dashboard.Tip6"));
                tips.Add(L.Get("Dashboard.Tip7"));
            }
            
            CurrentTip = tips[RandomNumberGenerator.GetInt32(tips.Count)];
        }
        
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
    
    public class RecentActivityItem
    {
        public string Time { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
