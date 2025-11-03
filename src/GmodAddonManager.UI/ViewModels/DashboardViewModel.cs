using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        private readonly AddonManager _addonManager;
        private readonly Random _random = new Random();
        
        private int _totalAddons;
        private int _enabledAddons;
        private int _disabledAddons;
        private string _totalSize = "0 B";
        private string _currentTip = "";
        
        public DashboardViewModel(AddonManager addonManager)
        {
            _addonManager = addonManager;
            
            // コマンドの初期化
            CheckNewAddonsCommand = ReactiveCommand.Create(() =>
            {
                var mainViewModel = ViewModelLocator.MainWindowViewModel;
                if (mainViewModel?.RefreshCommand != null)
                {
                    mainViewModel.RefreshCommand.Execute().Subscribe();
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
                
                // 統計情報を計算
                EnabledAddons = 0;
                DisabledAddons = 0;
                
                foreach (var asset in config.Assets)
                {
                    if (asset.IsSystem && asset.Name == "Junction")
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
                
                // 重複を除去
                EnabledAddons = Math.Min(EnabledAddons, TotalAddons - DisabledAddons);
                
                // サイズを計算
                long totalBytes = 0;
                foreach (var addon in config.AddonMetadata.Values)
                {
                    totalBytes += addon.Size;
                }
                
                TotalSize = FormatFileSize(totalBytes);
            }
            catch
            {
                // エラー時はデフォルト値を保持
            }
        }
        
        private void LoadRecentActivities()
        {
            RecentActivities.Clear();
            
            // UndoManagerから操作履歴を取得
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
            return action.Type switch
            {
                Core.Models.UndoActionType.AddonAddedToAsset => L.Format("Dashboard.Activity.AddedToAsset", action.AffectedAddonIds?.Count ?? 1, action.AssetName ?? "Unknown"),
                Core.Models.UndoActionType.AddonRemovedFromAsset => L.Format("Dashboard.Activity.RemovedFromAsset", action.AffectedAddonIds?.Count ?? 1),
                Core.Models.UndoActionType.AssetCreated => L.Format("Dashboard.Activity.CreatedAsset", action.AssetName ?? "Unknown"),
                Core.Models.UndoActionType.AssetDeleted => L.Format("Dashboard.Activity.DeletedAsset", action.AssetName ?? "Unknown"),
                Core.Models.UndoActionType.AssetEnabled => L.Format("Dashboard.Activity.AssetEnabled", action.AssetName ?? "Unknown"),
                Core.Models.UndoActionType.AssetDisabled => L.Format("Dashboard.Activity.AssetDisabled", action.AssetName ?? "Unknown"),
                Core.Models.UndoActionType.AddonStateChanged => L.Format("Dashboard.Activity.AddonStateChanged", action.AddonName ?? "Unknown"),
                Core.Models.UndoActionType.AssetMerged => L.Format("Dashboard.Activity.AssetMerged", action.AssetName ?? "Unknown"),
                _ => action.Description
            };
        }
        
        private void ShowRandomTip()
        {
            var tips = new[]
            {
                L.Get("Dashboard.Tip1"),
                L.Get("Dashboard.Tip2"),
                L.Get("Dashboard.Tip3"),
                L.Get("Dashboard.Tip4"),
                L.Get("Dashboard.Tip5"),
                L.Get("Dashboard.Tip6"),
                L.Get("Dashboard.Tip7"),
                L.Get("Dashboard.Tip8")
            };
            
            CurrentTip = tips[_random.Next(tips.Length)];
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