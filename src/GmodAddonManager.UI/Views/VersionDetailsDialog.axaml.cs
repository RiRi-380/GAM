using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.UI.Views
{
    public partial class VersionDetailsDialog : Window
    {
        public VersionDetailsDialog()
        {
            InitializeComponent();
        }
        
        public VersionDetailsDialog(Asset asset, int targetVersion, List<AssetVersion> versionHistory) : this()
        {
            var viewModel = new VersionDetailsViewModel(asset, targetVersion, versionHistory);
            DataContext = viewModel;
        }
        
        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
    
    public class VersionDetailsViewModel
    {
        private readonly Asset _asset;
        private readonly int _targetVersion;
        private readonly List<AssetVersion> _versionHistory;
        private readonly List<string> _currentAddonIds;
        private readonly List<string> _previousAddonIds;
        
        public VersionDetailsViewModel(Asset asset, int targetVersion, List<AssetVersion> versionHistory)
        {
            _asset = asset;
            _targetVersion = targetVersion;
            _versionHistory = versionHistory;
            
            // 現在のバージョンのアドオンID取得
            if (targetVersion == asset.CurrentVersion)
            {
                _currentAddonIds = new List<string>(asset.Addons);
            }
            else
            {
                var version = versionHistory.FirstOrDefault(v => v.Version == targetVersion);
                _currentAddonIds = version?.AddonIds ?? new List<string>();
            }
            
            // 前バージョンのアドオンID取得
            var previousVersion = targetVersion - 1;
            if (previousVersion > 0)
            {
                var prevVer = versionHistory.FirstOrDefault(v => v.Version == previousVersion);
                _previousAddonIds = prevVer?.AddonIds ?? new List<string>();
            }
            else
            {
                _previousAddonIds = new List<string>();
            }
            
            // 表示用のアドオンリストを作成
            CreateDisplayAddons();
        }
        
        public string AssetName => _asset.Name;
        public string VersionInfo => $"バージョン: v{_targetVersion}";
        public bool HasPreviousVersion => _previousAddonIds.Count > 0;
        
        public string DiffSummary => HasPreviousVersion 
            ? $"v{_targetVersion - 1} → v{_targetVersion} の変更内容" 
            : "初回バージョン";
        
        public int AddedCount => _currentAddonIds.Except(_previousAddonIds).Count();
        public int RemovedCount => _previousAddonIds.Except(_currentAddonIds).Count();
        
        public List<VersionAddonItem> DisplayAddons { get; } = new();
        
        private void CreateDisplayAddons()
        {
            var allIds = new HashSet<string>();
            allIds.UnionWith(_currentAddonIds);
            if (HasPreviousVersion)
            {
                allIds.UnionWith(_previousAddonIds);
            }
            
            // 削除されたアドオン（赤）
            foreach (var id in _previousAddonIds.Except(_currentAddonIds))
            {
                DisplayAddons.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = $"Workshop ID: {id}",
                    Status = AddonDiffStatus.Removed,
                    HasState = false
                });
            }
            
            // 追加されたアドオン（緑）
            foreach (var id in _currentAddonIds.Except(_previousAddonIds))
            {
                DisplayAddons.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = $"Workshop ID: {id}",
                    Status = AddonDiffStatus.Added,
                    HasState = false
                });
            }
            
            // 変更なしのアドオン（無色）
            foreach (var id in _currentAddonIds.Intersect(_previousAddonIds))
            {
                DisplayAddons.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = $"Workshop ID: {id}",
                    Status = AddonDiffStatus.Unchanged,
                    HasState = false
                });
            }
        }
    }
    
    public class VersionAddonItem
    {
        public string AddonId { get; set; } = "";
        public string Title { get; set; } = "";
        public AddonDiffStatus Status { get; set; }
        public bool HasState { get; set; }
        public string StateDisplay { get; set; } = "";
        
        public string BorderColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "Transparent"
        };
        
        public string BackgroundColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "#666666"
        };
        
        public string StatusIcon => Status switch
        {
            AddonDiffStatus.Added => "+",
            AddonDiffStatus.Removed => "-",
            _ => ""
        };
    }
    
    public enum AddonDiffStatus
    {
        Unchanged,
        Added,
        Removed
    }
}