using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;

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

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is VersionDetailsViewModel viewModel)
            {
                viewModel.Release();
            }

            base.OnClosed(e);
        }
    }
    
    public class VersionDetailsViewModel : ViewModelBase
    {
        private readonly Asset _asset;
        private readonly int _targetVersion;
        private readonly List<AssetVersion> _versionHistory;
        private readonly List<string> _currentAddonIds;
        private readonly List<string> _previousAddonIds;
        private readonly int? _previousVersion;
        private bool _disposed;
        private List<VersionAddonItem> displayAddons = new();
        
        public VersionDetailsViewModel(Asset asset, int targetVersion, List<AssetVersion> versionHistory)
        {
            _asset = asset;
            _targetVersion = targetVersion;
            _versionHistory = versionHistory;
            
            var targetSnapshot =
                versionHistory.FirstOrDefault(version => version.Version == targetVersion);
            _currentAddonIds = targetSnapshot?.AddonIds ?? new List<string>();
            
            // Gaps are valid because deleting a snapshot never renumbers history.
            var previousVersion = versionHistory
                .Where(version => version.Version < targetVersion)
                .OrderByDescending(version => version.Version)
                .FirstOrDefault();
            _previousVersion = previousVersion?.Version;
            _previousAddonIds = previousVersion?.AddonIds ?? new List<string>();
            
            // 表示用のアドオンリストを作成
            CreateDisplayAddons();
            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        }
        
        public string AssetName => GetAssetDisplayName();
        public string AssetLabel => L.Format("VersionDetails.AssetLabel", AssetName);
        public string VersionInfo => L.Format("VersionDetails.VersionInfoFormat", _targetVersion);
        public bool HasPreviousVersion => _previousVersion.HasValue;
        
        public string DiffSummary => HasPreviousVersion 
            ? L.Format(
                "VersionDetails.DiffSummaryFormat",
                _previousVersion!.Value,
                _targetVersion)
            : L.Get("VersionDetails.FirstVersion");
        
        public int AddedCount => _currentAddonIds.Except(_previousAddonIds).Count();
        public int RemovedCount => _previousAddonIds.Except(_currentAddonIds).Count();
        public string AddedCountText => L.Format("VersionDetails.AddedCountFormat", AddedCount);
        public string RemovedCountText => L.Format("VersionDetails.RemovedCountFormat", RemovedCount);
        
        public List<VersionAddonItem> DisplayAddons
        {
            get => displayAddons;
            private set => SetAndRaise(ref displayAddons, value);
        }
        
        private void CreateDisplayAddons()
        {
            var items = new List<VersionAddonItem>();
            var allIds = new HashSet<string>();
            allIds.UnionWith(_currentAddonIds);
            if (HasPreviousVersion)
            {
                allIds.UnionWith(_previousAddonIds);
            }
            
            // 削除されたアドオン（赤）
            foreach (var id in _previousAddonIds.Except(_currentAddonIds))
            {
                items.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = L.Format("VersionDetails.WorkshopIdFormat", id),
                    Status = AddonDiffStatus.Removed
                });
            }
            
            // 追加されたアドオン（緑）
            foreach (var id in _currentAddonIds.Except(_previousAddonIds))
            {
                items.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = L.Format("VersionDetails.WorkshopIdFormat", id),
                    Status = AddonDiffStatus.Added
                });
            }
            
            // 変更なしのアドオン（無色）
            foreach (var id in _currentAddonIds.Intersect(_previousAddonIds))
            {
                items.Add(new VersionAddonItem
                {
                    AddonId = id,
                    Title = L.Format("VersionDetails.WorkshopIdFormat", id),
                    Status = AddonDiffStatus.Unchanged
                });
            }

            DisplayAddons = items;
        }

        private string GetAssetDisplayName()
        {
            return _asset.Id switch
            {
                "subscribe-system-asset" => L.Get("Asset.SubscribeAsset"),
                _ => _asset.Name
            };
        }

        private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
            {
                this.RaisePropertyChanged(nameof(AssetName));
                this.RaisePropertyChanged(nameof(AssetLabel));
                this.RaisePropertyChanged(nameof(VersionInfo));
                this.RaisePropertyChanged(nameof(DiffSummary));
                this.RaisePropertyChanged(nameof(AddedCountText));
                this.RaisePropertyChanged(nameof(RemovedCountText));
                CreateDisplayAddons();
            }
        }

        public void Release()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        }
    }
    
    public class VersionAddonItem
    {
        public string AddonId { get; set; } = "";
        public string Title { get; set; } = "";
        public AddonDiffStatus Status { get; set; }
        
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
