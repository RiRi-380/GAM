using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.IO;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GmodAddonManager.UI.ViewModels
{
    public sealed class VersionManagementViewModel : ViewModelBase
    {
        // 繧ｦ繧｣繝ｳ繝峨え繧帝哩縺倥ｋ縺溘ａ縺ｮ繧､繝吶Φ繝・
        public event EventHandler? CloseRequested;
        private Asset _asset;
        private readonly AddonManager _addonManager;
        private readonly HybridWorkshopService _workshopService;
        private bool _includeAddonStates = true;
        private bool _isNewestFirst = true;
        private bool _disposed;
        private ObservableCollection<VersionItemViewModel> _versions;
        private VersionItemViewModel? _selectedVersion;
        private ObservableCollection<VersionAddonItemViewModel> _selectedVersionAddons;
        private bool _showDiff = true;
        
        // 繧ｭ繝｣繝・す繝･逕ｨ繝輔ぅ繝ｼ繝ｫ繝・
        private readonly Dictionary<string, AddonItemViewModel> _addonViewModelCache = new();
        private List<WorkshopAddon>? _cachedAddonList;
        private DateTime _lastScanTime = DateTime.MinValue;
        
        public VersionManagementViewModel(Asset asset, AddonManager addonManager)
        {
            _asset = asset;
            _addonManager = addonManager;
            var iconResolver = new WorkshopIconResolver(
                new SteamPathDetector(), 
                null, 
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GmodAddonManager"
                )
            );
            var steamWorkshopService = new SteamWorkshopService(iconResolver);
            _workshopService = new HybridWorkshopService(steamWorkshopService);
            _versions = new ObservableCollection<VersionItemViewModel>();
            _selectedVersionAddons = new ObservableCollection<VersionAddonItemViewModel>();
            
            CreateNewVersionCommand = ReactiveCommand.CreateFromTask(CreateNewVersionAsync);
            ShowVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(ShowVersionAsync);
            RestoreVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(RestoreVersionAsync);
            RestoreSelectedVersionCommand = ReactiveCommand.CreateFromTask(RestoreSelectedVersionAsync, 
                this.WhenAnyValue(x => x.SelectedVersion).Select(v => v != null && !v.IsCurrent));
            DeleteVersionCommand = ReactiveCommand.CreateFromTask<VersionItemViewModel>(DeleteVersionAsync);
            RenameVersionsCommand = ReactiveCommand.CreateFromTask(RenameVersionsAsync);
            ClearVersionHistoryCommand = ReactiveCommand.CreateFromTask(ClearVersionHistoryAsync);
            
            LoadVersions();
            
            // 繝・ヵ繧ｩ繝ｫ繝医〒驕ｸ謚槭☆繧九ヰ繝ｼ繧ｸ繝ｧ繝ｳ繧呈ｱｺ螳・
            if (_asset.CurrentVersion == 0 && _asset.HasImportBaseline)
            {
                // v0縺ｧ繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺後≠繧句ｴ蜷医・縲√う繝ｳ繝昴・繝亥燕繝舌・繧ｸ繝ｧ繝ｳ繧帝∈謚・
                SelectedVersion = _versions.FirstOrDefault(v => v.IsImportBaseline);
            }
            else
            {
                // 縺昴ｌ莉･螟悶・蝣ｴ蜷医・迴ｾ蝨ｨ縺ｮ繝舌・繧ｸ繝ｧ繝ｳ繧帝∈謚・
                SelectedVersion = _versions.FirstOrDefault(v => v.IsCurrent);
            }

            LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        }
        
        public string AssetName => GetAssetDisplayName();
        public string AssetTitle => L.Format("VersionManagement.AssetTitleFormat", AssetName);
        
        public bool IncludeAddonStates
        {
            get => _includeAddonStates;
            set => this.RaiseAndSetIfChanged(ref _includeAddonStates, value);
        }
        
        public bool IsNewestFirst
        {
            get => _isNewestFirst;
            set
            {
                this.RaiseAndSetIfChanged(ref _isNewestFirst, value);
                this.RaisePropertyChanged(nameof(SortedVersions));
                this.RaisePropertyChanged(nameof(IsOldestFirst));
            }
        }

        public bool IsOldestFirst
        {
            get => !_isNewestFirst;
            set
            {
                if (value == _isNewestFirst)
                {
                    IsNewestFirst = !value;
                }
            }
        }
        
        public bool ShowDiff
        {
            get => _showDiff;
            set
            {
                this.RaiseAndSetIfChanged(ref _showDiff, value);
                // 蟾ｮ蛻・｡ｨ遉ｺ縺ｮ蛻・ｊ譖ｿ縺域凾縺ｫ蜀崎ｪｭ縺ｿ霎ｼ縺ｿ
                if (SelectedVersion != null)
                {
                    _ = LoadSelectedVersionAddonsAsync(SelectedVersion);
                }
            }
        }
        
        public ObservableCollection<VersionItemViewModel> Versions => _versions;
        
        public IEnumerable<VersionItemViewModel> SortedVersions
        {
            get
            {
                if (IsNewestFirst)
                    return _versions.OrderByDescending(v => v.IsImportBaseline ? int.MinValue : v.Version);
                else
                    return _versions.OrderBy(v => v.IsImportBaseline ? int.MinValue : v.Version);
            }
        }
        
        public VersionItemViewModel? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                // Debug.WriteLine($"[VersionManagement] SelectedVersion setter called");
                // Debug.WriteLine($"[VersionManagement] New value: {value?.VersionDisplay} (v{value?.Version}), IsCurrent: {value?.IsCurrent}");
                
                // 莉･蜑阪・驕ｸ謚槭ｒ隗｣髯､
                if (_selectedVersion != null)
                {
                    _selectedVersion.IsSelected = false;
                }
                
                this.RaiseAndSetIfChanged(ref _selectedVersion, value);
                
                // 譁ｰ縺励＞驕ｸ謚槭ｒ險ｭ?E
                if (value != null)
                {
                    value.IsSelected = true;
                    _ = LoadSelectedVersionAddonsAsync(value);
                }
                
                this.RaisePropertyChanged(nameof(SelectedVersionTitle));
                this.RaisePropertyChanged(nameof(CanRestore));
                
                // Debug.WriteLine($"[VersionManagement] CanRestore: {CanRestore}");
            }
        }
        
        public ObservableCollection<VersionAddonItemViewModel> SelectedVersionAddons => _selectedVersionAddons;
        
        public string SelectedVersionTitle => SelectedVersion != null 
            ? L.Format("VersionManagement.SelectedVersionTitleFormat", SelectedVersion.VersionDisplay, SelectedVersion.CreatedAtDisplay)
            : L.Get("VersionManagement.SelectVersionPrompt");
            
        public bool CanRestore => SelectedVersion != null;
        
        public ReactiveCommand<Unit, Unit> CreateNewVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> ShowVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> RestoreVersionCommand { get; }
        public ReactiveCommand<Unit, Unit> RestoreSelectedVersionCommand { get; }
        public ReactiveCommand<VersionItemViewModel, Unit> DeleteVersionCommand { get; }
        public ReactiveCommand<Unit, Unit> RenameVersionsCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearVersionHistoryCommand { get; }

        private Asset ResolveCurrentAsset()
        {
            var latest = _addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == _asset.Id);
            if (latest != null && !ReferenceEquals(latest, _asset))
            {
                _asset = latest;
            }

            return _asset;
        }

        private List<string> ResolveAllAddonIdsForAsset()
        {
            var asset = ResolveCurrentAsset();
            var resolved = new HashSet<string>();

            // Subscribe繧｢繧ｻ繝・ヨ縺ｯ螳溘し繝悶せ繧ｯ荳隕ｧ繧貞━蜈・
            if (asset.Id == "subscribe-system-asset")
            {
                foreach (var addonId in SteamWorkshopCacheReader.GetSubscribedAddonIds())
                {
                    if (addonId != "*")
                    {
                        resolved.Add(addonId);
                    }
                }
            }

            if (resolved.Count == 0)
            {
                var allAddons = _addonManager.GetAllAddons();
                if (allAddons != null)
                {
                    foreach (var addonId in allAddons.Keys)
                    {
                        if (addonId != "*")
                        {
                            resolved.Add(addonId);
                        }
                    }
                }
            }

            return resolved.OrderBy(id => id).ToList();
        }

        private List<string> ResolveAddonIdsForSnapshot()
        {
            var asset = ResolveCurrentAsset();
            if (asset.Id == "subscribe-system-asset" || asset.ContainsAllAddons())
            {
                return ResolveAllAddonIdsForAsset();
            }

            return asset.Addons
                .Where(id => id != "*")
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        private List<string> NormalizeAddonIdsForVersion(AssetVersion? version)
        {
            if (version == null)
            {
                return new List<string>();
            }

            if (version.AddonIds.Contains("*"))
            {
                return ResolveAllAddonIdsForAsset();
            }

            return version.AddonIds
                .Where(id => id != "*")
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }
        
        private void LoadVersions()
        {
            var asset = ResolveCurrentAsset();
            // Debug.WriteLine($"[VersionManagement] LoadVersions called for asset '{_asset.Name}'");
            // Debug.WriteLine($"[VersionManagement] CurrentVersion: {_asset.CurrentVersion}");
            // Debug.WriteLine($"[VersionManagement] VersionHistory count: {_asset.VersionHistory.Count}");
            
            _versions.Clear();
            
            // 螻･豁ｴ縺九ｉ繝舌・繧ｸ繝ｧ繝ｳ繧定ｿｽ蜉
            foreach (var version in asset.VersionHistory)
            {
                var isCurrent = version.Version == asset.CurrentVersion;
                // Debug.WriteLine($"[VersionManagement] Adding version {version.Version} (IsImportBaseline: {version.IsImportBaseline}, IsCurrent: {isCurrent})");
                
                var addonCount = NormalizeAddonIdsForVersion(version).Count;
                var vm = new VersionItemViewModel
                {
                    Version = version.Version,
                    CreatedAt = version.CreatedAt,
                    AddonCount = addonCount,
                    IsCurrent = isCurrent && !version.IsImportBaseline,  // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｯ豎ｺ縺励※迴ｾ蝨ｨ縺ｮ繝舌・繧ｸ繝ｧ繝ｳ縺ｫ縺ｪ繧峨↑縺・
                    IncludesStates = version.IncludeAddonStates,
                    IsImportBaseline = version.IsImportBaseline
                };
                _versions.Add(vm);
            }
            
            // v0縺ｯ陦ｨ遉ｺ縺励↑縺・ｼ医う繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺後≠繧句ｴ蜷医ｒ髯､縺擾ｼ・
            if (!asset.VersionHistory.Any(v => v.Version == asset.CurrentVersion) && asset.CurrentVersion != 0)
            {
                var currentVersion = new VersionItemViewModel
                {
                    Version = asset.CurrentVersion,
                    CreatedAt = DateTime.Now,
                    AddonCount = asset.Addons.Count,
                    IsCurrent = true,
                    IncludesStates = false
                };
                _versions.Add(currentVersion);
            }
            
            // 蜑企勁蜿ｯ閭ｽ繝輔Λ繧ｰ繧呈峩譁ｰ
            UpdateCanDeleteFlags();
            
            // 蜷・ヰ繝ｼ繧ｸ繝ｧ繝ｳ縺ｮ繝励Ο繝代ユ繧｣螟画峩繧帝夂衍・郁レ譎ｯ濶ｲ縺ｮ譖ｴ譁ｰ縺ｪ縺ｩ・・
            foreach (var version in _versions)
            {
                version.RaisePropertyChanged(nameof(version.IsCurrent));
                version.RaisePropertyChanged(nameof(version.BackgroundColor));
            }
        }
        
        private void UpdateCanDeleteFlags()
        {
            // 繝舌・繧ｸ繝ｧ繝ｳ縺・縺､縺励°縺ｪ縺・ｴ蜷医√∪縺溘・ v0 縺ｮ蝣ｴ蜷医・蜑企勁荳榊庄
            if (_versions.Count == 1)
            {
                foreach (var version in _versions)
                {
                    version.CanDelete = false;
                    version.RaisePropertyChanged(nameof(version.CanDelete));
                }
            }
            else
            {
                foreach (var version in _versions)
                {
                    // v0縺ｨ繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｯ蜑企勁荳榊庄
                    version.CanDelete = version.Version != 0 && !version.IsImportBaseline;
                    version.RaisePropertyChanged(nameof(version.CanDelete));
                }
            }
        }
        
        private async Task CreateNewVersionAsync()
        {
            try
            {
                var dialogService = new DialogService();
                var asset = ResolveCurrentAsset();
                var nextVersion = asset.CurrentVersion + 1;
                var confirmed = await dialogService.ShowConfirmAsync(
                    L.Get("VersionManagement.CreateConfirmTitle"),
                    L.Format("VersionManagement.CreateConfirmMessage", nextVersion)
                );
                
                if (!confirmed) return;
                
                var resolvedAddonIds = ResolveAddonIdsForSnapshot();
                
                // 譁ｰ縺励＞繝舌・繧ｸ繝ｧ繝ｳ繧剃ｽ懈・
                var newVersionNumber = nextVersion;
                var newVersion = new AssetVersion
                {
                    Version = newVersionNumber,
                    CreatedAt = DateTime.Now,
                    AddonIds = new List<string>(resolvedAddonIds),
                    IncludeAddonStates = IncludeAddonStates
                };
                
                // GAM蠖｢蠑上・繧ｳ繝ｳ繝・Φ繝・ｒ逕滓・
                var gamLines = new List<string>
                {
                    "# GAM Collection Export v1",
                    $"# Title: {asset.Name} v{newVersionNumber}",
                    $"# Description: Version {newVersionNumber} of {asset.Name}",
                    $"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"# Count: {resolvedAddonIds.Count}",
                    ""
                };
                gamLines.AddRange(resolvedAddonIds);
                newVersion.GamContent = string.Join("\n", gamLines);
                
                // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ菫晏ｭ倥☆繧句ｴ蜷・
                if (IncludeAddonStates)
                {
                    var filter = new HashSet<string>(resolvedAddonIds);
                    newVersion.AddonStates = asset.AddonStates
                        .Where(kvp => filter.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }
                
                // 繝舌・繧ｸ繝ｧ繝ｳ螻･豁ｴ縺ｫ霑ｽ蜉
                asset.VersionHistory.Add(newVersion);
                asset.CurrentVersion = newVersionNumber;
                
                // 險ｭ螳壹ｒ菫晏ｭ・
                await _addonManager.SaveConfigurationAsync();
                
                // UI繧呈峩譁ｰ
                LoadVersions();
                
                // 譁ｰ縺励￥菴懈・縺励◆繝舌・繧ｸ繝ｧ繝ｳ繧帝∈謚・
                SelectedVersion = _versions.FirstOrDefault(v => v.Version == newVersionNumber);
                
                // 驕ｸ謚槭＆繧後◆繝舌・繧ｸ繝ｧ繝ｳ縺ｮ繧｢繝峨が繝ｳ繧定ｪｭ縺ｿ霎ｼ繧・医％繧後↓繧医ｊ蜿ｳ蛛ｴ縺ｮ繧｢繝峨が繝ｳ陦ｨ遉ｺ縺梧峩譁ｰ縺輔ｌ繧具ｼ・
                if (SelectedVersion != null)
                {
                    await LoadSelectedVersionAddonsAsync(SelectedVersion);
                }
                
                // 繝舌・繧ｸ繝ｧ繝ｳ荳隕ｧ繧貞ｼｷ蛻ｶ逧・↓譖ｴ譁ｰ
                this.RaisePropertyChanged(nameof(SortedVersions));
                
                await dialogService.ShowInfoAsync(
                    L.Get("Success.Title"),
                    L.Format("VersionManagement.CreateCompleteMessage", newVersionNumber)
                );
            }
            catch (Exception)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.CreateFailed"));
            }
        }
        
        private async Task ShowVersionAsync(VersionItemViewModel version)
        {
            try
            {
                // 驕ｸ謚槭＆繧後◆繝舌・繧ｸ繝ｧ繝ｳ縺ｮ隧ｳ邏ｰ繧定｡ｨ遉ｺ
                var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                if (window?.MainWindow != null)
                {
                    var asset = ResolveCurrentAsset();
                    var dialog = new VersionDetailsDialog(asset, version.Version, asset.VersionHistory);
                    await dialog.ShowDialog(window.MainWindow);
                }
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.DetailsFailed"));
            }
        }
        
        private async Task RestoreVersionAsync(VersionItemViewModel versionVm)
        {
            try
            {
                var dialogService = new DialogService();
                var asset = ResolveCurrentAsset();

                var targetVersion = asset.VersionHistory.FirstOrDefault(v => v.Version == versionVm.Version);
                if (targetVersion == null)
                {
                    await dialogService.ShowErrorAsync(
                        L.Get("Error.Title"),
                        L.Get("VersionManagement.VersionNotFound"));
                    return;
                }

                List<string> addonsToSubscribe = new List<string>();
                List<string> addonsToUnsubscribe = new List<string>();
                var normalizedTargetAddonIds = NormalizeAddonIdsForVersion(targetVersion);

                if (asset.Id == "subscribe-system-asset")
                {
                    var currentAddons = new HashSet<string>(
                        SteamWorkshopCacheReader.GetSubscribedAddonIds().Where(id => id != "*"));
                    if (currentAddons.Count == 0)
                    {
                        currentAddons = new HashSet<string>(asset.Addons.Where(id => id != "*"));
                        if (currentAddons.Count == 0)
                        {
                            currentAddons = new HashSet<string>(ResolveAllAddonIdsForAsset());
                        }
                    }

                    if (targetVersion.IsImportBaseline && targetVersion.NewlySubscribedAddonIds != null)
                    {
                        addonsToSubscribe = new List<string>();
                        addonsToUnsubscribe = targetVersion.NewlySubscribedAddonIds
                            .Where(id => currentAddons.Contains(id))
                            .ToList();
                    }
                    else
                    {
                        var targetAddons = new HashSet<string>(normalizedTargetAddonIds);
                        addonsToSubscribe = targetAddons.Except(currentAddons).ToList();
                        addonsToUnsubscribe = currentAddons.Except(targetAddons).ToList();
                    }
                }

                var showSubscribeInfo = asset.Id == "subscribe-system-asset"
                    && (addonsToSubscribe.Any() || addonsToUnsubscribe.Any());
                var confirmMessage = versionVm.IsImportBaseline
                    ? L.Format("VersionManagement.RestoreImportBaselineConfirm", asset.Name)
                    : L.Format("VersionManagement.RestoreVersionConfirm", versionVm.Version);
                var confirmed = await dialogService.ShowVersionRestoreConfirmAsync(
                    confirmMessage,
                    addonsToSubscribe,
                    addonsToUnsubscribe,
                    showSubscribeInfo);

                if (!confirmed)
                {
                    return;
                }

                if (asset.Id == "subscribe-system-asset")
                {
                    asset.Addons.Clear();
                    asset.Addons.AddRange(normalizedTargetAddonIds);

                    if (targetVersion.IncludeAddonStates && targetVersion.AddonStates != null)
                    {
                        asset.AddonStates.Clear();
                        foreach (var kvp in targetVersion.AddonStates)
                        {
                            asset.AddonStates[kvp.Key] = kvp.Value;
                        }
                    }
                    else
                    {
                        foreach (var addonId in addonsToSubscribe)
                        {
                            asset.AddonStates[addonId] = AddonState.Enabled;
                        }
                    }

                    foreach (var addonId in addonsToUnsubscribe)
                    {
                        asset.AddonStates.Remove(addonId);
                    }

                    asset.CurrentVersion = targetVersion.Version;
                    await _addonManager.SaveConfigurationAsync();
                    await _addonManager.UpdateAddonStatesAsync();
                }
                else
                {
                    if (targetVersion.IsImportBaseline)
                    {
                        var deleteAssetId = asset.Id;
                        await _addonManager.SaveConfigurationAsync();

                        MainWindowViewModel? mainViewModel = null;
                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            mainViewModel = desktop.MainWindow?.DataContext as MainWindowViewModel;
                        }

                        await dialogService.ShowInfoAsync(
                            L.Get("Success.Title"),
                            L.Get("VersionManagement.RestoreImportBaselineComplete")
                        );

                        if (CloseRequested != null)
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                _addonManager.DeleteAsset(deleteAssetId);
                                await _addonManager.SaveConfigurationAsync();

                                if (mainViewModel != null)
                                {
                                    mainViewModel.AssetListViewModel?.LoadAssets();
                                    await mainViewModel.RefreshAddonsAsync();
                                }

                                CloseRequested.Invoke(this, EventArgs.Empty);
                            });
                        }
                        else
                        {
                            _addonManager.DeleteAsset(deleteAssetId);
                            await _addonManager.SaveConfigurationAsync();

                            if (mainViewModel != null)
                            {
                                mainViewModel.AssetListViewModel?.LoadAssets();
                                await mainViewModel.RefreshAddonsAsync();
                            }
                        }

                        return;
                    }

                    asset.Addons.Clear();
                    asset.Addons.AddRange(normalizedTargetAddonIds);

                    if (targetVersion.IncludeAddonStates && targetVersion.AddonStates != null)
                    {
                        asset.AddonStates.Clear();
                        foreach (var kvp in targetVersion.AddonStates)
                        {
                            asset.AddonStates[kvp.Key] = kvp.Value;
                        }
                    }
                    else
                    {
                        var validAddonIds = new HashSet<string>(asset.Addons);
                        var staleKeys = asset.AddonStates.Keys
                            .Where(id => !validAddonIds.Contains(id))
                            .ToList();
                        foreach (var staleId in staleKeys)
                        {
                            asset.AddonStates.Remove(staleId);
                        }
                    }

                    asset.CurrentVersion = targetVersion.Version;
                    await _addonManager.SaveConfigurationAsync();
                    await _addonManager.UpdateAddonStatesAsync();
                }

                if (!targetVersion.IsImportBaseline)
                {
                    await RefreshMainWindowLightAsync();
                    SyncAssetViewModelsFromConfiguration();
                    asset = ResolveCurrentAsset();
                    LoadVersions();
                    SelectedVersion = _versions.FirstOrDefault(v => v.Version == targetVersion.Version);
                    this.RaisePropertyChanged(nameof(SortedVersions));
                    this.RaisePropertyChanged(nameof(Versions));

                    await dialogService.ShowInfoAsync(
                        L.Get("Success.Title"),
                        L.Format("VersionManagement.RestoreCompleteMessage", targetVersion.Version)
                    );
                }
            }
            catch (Exception)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.RestoreFailed"));
            }
        }

        private async Task RestoreSelectedVersionAsync()
        {
            // Debug.WriteLine("[VersionManagement] RestoreSelectedVersionAsync called");
            if (SelectedVersion != null)
            {
                // Debug.WriteLine($"[VersionManagement] SelectedVersion: {SelectedVersion.VersionDisplay} (v{SelectedVersion.Version})");
                await RestoreVersionAsync(SelectedVersion);
            }
            else
            {
                // Debug.WriteLine("[VersionManagement] SelectedVersion is null!");
            }
        }
        
        private async Task LoadSelectedVersionAddonsAsync(VersionItemViewModel version)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var asset = ResolveCurrentAsset();
                _selectedVersionAddons.Clear();
                
                // 驕ｸ謚槭＆繧後◆繝舌・繧ｸ繝ｧ繝ｳ縺ｮ繧｢繝峨が繝ｳID繧貞叙蠕・
                List<string> addonIds;
                // 螻･豁ｴ縺九ｉ蜿門ｾ・
                var versionData = asset.VersionHistory.FirstOrDefault(v => v.Version == version.Version);
                addonIds = NormalizeAddonIdsForVersion(versionData);
                
                // Debug.WriteLine($"Loading v{version.Version}: {addonIds.Count} addons");
                
                // 蜑阪・繝舌・繧ｸ繝ｧ繝ｳ縺ｮ繧｢繝峨が繝ｳID繧貞叙蠕暦ｼ亥ｷｮ蛻・｡ｨ遉ｺ逕ｨ・・
                List<string> previousAddonIds = new List<string>();
                
                // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｾ縺溘・v1縺ｯ豈碑ｼ・ｯｾ雎｡縺ｪ縺・
                if (version.IsImportBaseline || version.Version == 1)
                {
                    // 豈碑ｼ・＠縺ｪ縺・ｼ亥・縺ｦ螟画峩縺ｪ縺励→縺励※陦ｨ遉ｺ・・
                    previousAddonIds = new List<string>(addonIds);
                }
                else
                {
                    // v2莉･髯阪・蜑阪・繝舌・繧ｸ繝ｧ繝ｳ縺ｨ豈碑ｼ・
                    var previousVersion = version.Version - 1;
                    var prevVersion = asset.VersionHistory.FirstOrDefault(v => v.Version == previousVersion);
                    previousAddonIds = NormalizeAddonIdsForVersion(prevVersion);
                }
                
                // Debug.WriteLine($"Current version: v{version.Version} with {addonIds.Count} addons");
                // Debug.WriteLine($"Previous addons: {previousAddonIds.Count} addons");
                // Debug.WriteLine($"ShowDiff: {ShowDiff}");
                
                // 繝｡繧､繝ｳ繧ｦ繧｣繝ｳ繝峨え縺ｨ螳悟・縺ｫ蜷後§譁ｹ豕輔〒繧｢繝峨が繝ｳ繧定ｪｭ縺ｿ霎ｼ繧
                // 譁ｰ縺励＞繧ｳ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ繧剃ｽ廢
                var versionAddonItems = new ObservableCollection<AddonItemViewModel>();
                
                // 繧ｭ繝｣繝・す繝･縺輔ｌ縺溘い繝峨が繝ｳ繝ｪ繧ｹ繝医ｒ菴ｿ逕ｨ縲√∪縺溘・5蛻・ｻ･荳顔ｵ碁℃縺励※縺・ｌ縺ｰ蜀阪せ繧ｭ繝｣繝ｳ
                List<WorkshopAddon> addonList;
                var timeSinceLastScan = DateTime.Now - _lastScanTime;
                if (_cachedAddonList == null || timeSinceLastScan > TimeSpan.FromMinutes(5))
                {
                    // ScanWorkshopFolderAsync繧剃ｽｿ縺｣縺ｦWorkshopAddon繧ｪ繝悶ず繧ｧ繧ｯ繝医ｒ蜿門ｾ・
                    addonList = await _addonManager.ScanWorkshopFolderAsync();
                    _cachedAddonList = addonList;
                    _lastScanTime = DateTime.Now;
                }
                else
                {
                    addonList = _cachedAddonList;
                }
                
                // 荳譎ら噪縺ｪ繝ｪ繧ｹ繝医↓蜈ｨ繧｢繝峨が繝ｳ繧貞庶髮・
                var tempAddonList = new List<VersionAddonItemViewModel>();
                var processedAddonIds = new HashSet<string>(); // 驥崎､・メ繧ｧ繝・け逕ｨ
                
                // 繝舌・繧ｸ繝ｧ繝ｳ縺ｫ蜷ｫ縺ｾ繧後ｋ繧｢繝峨が繝ｳ繧貞・逅・
                foreach (var addonId in addonIds)
                {
                    // 驥崎､・メ繧ｧ繝・け
                    if (processedAddonIds.Contains(addonId))
                    {
                        continue;
                    }
                    processedAddonIds.Add(addonId);
                    // WorkshopAddon繧ｪ繝悶ず繧ｧ繧ｯ繝医ｒ謗｢縺・
                    var workshopAddon = addonList.FirstOrDefault(a => a.Id == addonId);
                    
                    WorkshopAddon addonToUse;
                    if (workshopAddon != null)
                    {
                        addonToUse = workshopAddon;
                    }
                    else
                    {
                        // 隕九▽縺九ｉ縺ｪ縺・ｴ蜷医・譁ｰ縺励￥菴懈・・亥炎髯､縺輔ｌ縺溘い繝峨が繝ｳ縺ｮ蝣ｴ蜷茨ｼ・
                        // 縺ｾ縺哂ddonManager縺ｮ險ｭ螳壹°繧峨Γ繧ｿ繝・E繧ｿ繧貞叙蠕・
                        var config = _addonManager.GetConfiguration();
                        if (config.AddonMetadata.TryGetValue(addonId, out var metadata))
                        {
                            // 菫晏ｭ倥＆繧後◆繝｡繧ｿ繝・E繧ｿ縺九ｉ菴廢
                            addonToUse = metadata;
                        }
                        else
                        {
                            var workshopDetails = await _workshopService.GetWorkshopDetailsAsync(addonId);
                            if (workshopDetails != null)
                            {
                                addonToUse = new WorkshopAddon
                                {
                                    Id = addonId,
                                    Title = workshopDetails.Title ?? L.Format("VersionManagement.WorkshopIdFormat", addonId),
                                    FolderPath = "",
                                    IsGmaFile = false,
                                    NeedsTitleUpdate = false,
                                    Size = 0,
                                    LastUpdated = DateTimeOffset.FromUnixTimeSeconds(workshopDetails.TimeUpdated).DateTime,
                                    Description = workshopDetails.Description ?? string.Empty,
                                    Author = workshopDetails.Creator ?? "",
                                    ThumbnailUrl = workshopDetails.PreviewUrl ?? string.Empty,
                                    Tags = Array.Empty<string>()
                                };
                            }
                            else
                            {
                                addonToUse = new WorkshopAddon
                                {
                                    Id = addonId,
                                    Title = L.Format("VersionManagement.WorkshopIdDeletedFormat", addonId),
                                    FolderPath = "",
                                    IsGmaFile = false,
                                    NeedsTitleUpdate = false
                                };
                            }
                        }
                    }
                    
                    // 繧ｭ繝｣繝・す繝･縺九ｉAddonItemViewModel繧貞叙蠕励√∪縺溘・譁ｰ隕丈ｽ懈・
                    AddonItemViewModel addonItemVm;
                    if (_addonViewModelCache.TryGetValue(addonId, out var cachedVm))
                    {
                        // 繧ｭ繝｣繝・す繝･縺輔ｌ縺欸iewModel繧剃ｽｿ逕ｨ・域ュ蝣ｱ繧呈峩譁ｰ・・
                        if (!addonToUse.NeedsTitleUpdate && addonToUse.Title != null)
                        {
                            cachedVm.UpdateTitle(addonToUse.Title);
                        }
                        // 繝輔ぃ繧､繝ｫ繧ｵ繧､繧ｺ縺ｪ縺ｩ縺昴・莉悶・諠・ｱ繧よ峩譁ｰ
                        cachedVm.UpdateFromWorkshopAddon(addonToUse);
                        addonItemVm = cachedVm;
                    }
                    else
                    {
                        // 譁ｰ隕丈ｽ懈・縺励※繧ｭ繝｣繝・す繝･縺ｫ霑ｽ蜉
                        addonItemVm = new AddonItemViewModel(addonToUse, _addonManager, null);
                        _addonViewModelCache[addonId] = addonItemVm;
                    }
                    
                    // 蟾ｮ蛻・せ繝・・繧ｿ繧ｹ繧貞愛螳・
                    var status = AddonDiffStatus.Unchanged;
                    if (!version.IsImportBaseline && version.Version > 1 && ShowDiff)  // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｧ縺ｪ縺竣2莉･髯阪°縺､蟾ｮ蛻・｡ｨ遉ｺ縺薫N縺ｮ蝣ｴ蜷・
                    {
                        if (!previousAddonIds.Contains(addonId))
                        {
                            status = AddonDiffStatus.Added;
                            // Debug.WriteLine($"Added: {addonId} in v{version.Version}");
                        }
                    }
                    
                    // VersionAddonItemViewModel縺ｫ繝ｩ繝・E縺励※霑ｽ蜉
                    var versionAddon = new VersionAddonItemViewModel
                    {
                        AddonItemViewModel = addonItemVm,
                        Status = status
                    };
                    
                    // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ繝舌・繧ｸ繝ｧ繝ｳ縺ｧ譁ｰ隕上し繝悶せ繧ｯ繝ｩ繧､繝悶＆繧後◆繧｢繝峨が繝ｳ縺ｯ邱第棧
                    if (version.IsImportBaseline)
                    {
                        // Debug.WriteLine($"[VersionManagement] Checking import baseline addon: {addonId}");
                        if (versionData != null)
                        {
                            // Debug.WriteLine($"[VersionManagement] versionData is not null");
                            if (versionData.NewlySubscribedAddonIds != null)
                            {
                                // Debug.WriteLine($"[VersionManagement] NewlySubscribedAddonIds count: {versionData.NewlySubscribedAddonIds.Count}");
                                if (versionData.NewlySubscribedAddonIds.Contains(addonId))
                                {
                                    versionAddon.Status = AddonDiffStatus.Added; // 驍ｱ隨ｬ譽ｧ
                                    // Debug.WriteLine($"[VersionManagement] Import baseline addon marked as newly subscribed: {addonId}");
                                }
                            }
                            else
                            {
                                // Debug.WriteLine($"[VersionManagement] NewlySubscribedAddonIds is null!");
                            }
                        }
                        else
                        {
                            // Debug.WriteLine($"[VersionManagement] versionData is null!");
                        }
                    }
                    
                    tempAddonList.Add(versionAddon);
                }
                
                // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ繧り｡ｨ遉ｺ・医う繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｾ縺溘・v1縺ｧ縺ｪ縺・ｴ蜷医・縺ｿ・・
                if (!version.IsImportBaseline && version.Version > 1 && ShowDiff)
                {
                    foreach (var addonId in previousAddonIds.Except(addonIds))
                    {
                        // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ逕ｨ縺ｮWorkshopAddon繧剃ｽ廢
                        var deletedAddon = new WorkshopAddon
                        {
                            Id = addonId,
                            Title = L.Format("VersionManagement.WorkshopIdDeletedFormat", addonId),
                            FolderPath = "",
                            IsGmaFile = false,
                            NeedsTitleUpdate = true
                        };
                        
                        // 繧ｭ繝｣繝・す繝･縺九ｉAddonItemViewModel繧貞叙蠕励√∪縺溘・譁ｰ隕丈ｽ懈・
                        AddonItemViewModel addonItemVm;
                        if (_addonViewModelCache.TryGetValue(addonId, out var cachedVm))
                        {
                            addonItemVm = cachedVm;
                        }
                        else
                        {
                            // 譁ｰ隕丈ｽ懈・縺励※繧ｭ繝｣繝・す繝･縺ｫ霑ｽ蜉
                            addonItemVm = new AddonItemViewModel(deletedAddon, _addonManager, null);
                            _addonViewModelCache[addonId] = addonItemVm;
                        }
                        
                        var versionAddon = new VersionAddonItemViewModel
                        {
                            AddonItemViewModel = addonItemVm,
                            Status = AddonDiffStatus.Removed
                        };
                        
                        tempAddonList.Add(versionAddon);
                    }
                }
                
                // 繧ｽ繝ｼ繝・ 蜑企勁(襍､) ?E霑ｽ蜉(?E ?E螟画峩縺ｪ縺・
                var sortedAddons = tempAddonList
                    .OrderBy(a => 
                    {
                        switch (a.Status)
                        {
                            case AddonDiffStatus.Removed: return 0;
                            case AddonDiffStatus.Added: return 1;
                            default: return 2;
                        }
                    })
                    .ThenBy(a => a.Title)
                    .ToList();
                
                // ShowDiff縺後が繝輔・蝣ｴ蜷医・蜃ｦ逅・
                if (!ShowDiff && !version.IsImportBaseline && version.Version > 1)
                {
                    // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ繧帝勁?E
                    sortedAddons = sortedAddons.Where(a => a.Status != AddonDiffStatus.Removed).ToList();
                    
                    // 霑ｽ蜉縺輔ｌ縺溘い繝峨が繝ｳ縺ｮ繝懊・繝繝ｼ繧帝壼ｸｸ縺ｫ謌ｻ縺・
                    foreach (var addon in sortedAddons.Where(a => a.Status == AddonDiffStatus.Added))
                    {
                        addon.Status = AddonDiffStatus.Unchanged;
                    }
                }
                
                // 繧ｽ繝ｼ繝域ｸ医∩繝ｪ繧ｹ繝医ｒ霑ｽ蜉
                foreach (var addon in sortedAddons)
                {
                    _selectedVersionAddons.Add(addon);
                }
                
                // 陦ｨ遉ｺ縺輔ｌ縺ｦ縺・ｋ繧｢繝峨が繝ｳ縺ｮ隧ｳ邏ｰ縺ｨ繧ｵ繝繝阪う繝ｫ繧剃ｸｦ蛻励〒隱ｭ縺ｿ霎ｼ縺ｿ・医Γ繧､繝ｳ繧ｦ繧｣繝ｳ繝峨え縺ｨ蜷後§・・
                var tasks = new List<Task>();
                foreach (var versionAddon in _selectedVersionAddons.Take(30))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await versionAddon.AddonItemViewModel.LoadDetailsCommand.Execute();
                        await versionAddon.AddonItemViewModel.LoadThumbnailCommand.Execute();
                    }));
                }
                
                await Task.WhenAll(tasks);
                
                // 谿九ｊ縺ｮ繧｢繝峨が繝ｳ縺ｯ繝舌ャ繧ｯ繧ｰ繝ｩ繧ｦ繝ｳ繝峨〒隱ｭ縺ｿ霎ｼ縺ｿ
                _ = LoadRemainingVersionAddonsAsync(_selectedVersionAddons.Skip(30).ToList());
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.LoadAddonsFailed"));
            }
        }
        
        private async Task LoadRemainingVersionAddonsAsync(List<VersionAddonItemViewModel> remainingAddons)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                foreach (var versionAddon in remainingAddons)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    try
                    {
                        await versionAddon.AddonItemViewModel.LoadDetailsCommand.Execute();
                        await versionAddon.AddonItemViewModel.LoadThumbnailCommand.Execute();
                    }
                    catch (Exception ex)
                    {
                        SafeFileLogger.TryLogException("VersionManagementViewModel.LoadRemainingVersionAddonsAsync.Item", ex);
                    }

                    await Task.Delay(50); // Load in small batches to reduce UI pressure.
                }
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("VersionManagementViewModel.LoadRemainingVersionAddonsAsync", ex);
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
            CloseRequested = null;
            foreach (var addonViewModel in _addonViewModelCache.Values)
            {
                addonViewModel.Dispose();
            }
            _addonViewModelCache.Clear();
            _selectedVersionAddons.Clear();

        }

        private async Task DeleteVersionAsync(VersionItemViewModel versionVm)
        {
            try
            {
                var dialogService = new DialogService();
                var asset = ResolveCurrentAsset();
                
                // v0縺ｨ繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺ｯ蜑企勁縺ｧ縺阪↑縺・
                if (versionVm.Version == 0)
                {
                    await dialogService.ShowWarningAsync(
                        L.Get("Warning.Title"),
                        L.Get("VersionManagement.DeleteV0NotAllowed"));
                    return;
                }
                
                if (versionVm.IsImportBaseline)
                {
                    await dialogService.ShowWarningAsync(
                        L.Get("Warning.Title"),
                        L.Get("VersionManagement.DeleteImportBaselineNotAllowed"));
                    return;
                }
                
                // 譛蠕後・繝舌・繧ｸ繝ｧ繝ｳ縺ｯ蜑企勁縺ｧ縺阪↑縺・
                if (_versions.Count <= 1)
                {
                    await dialogService.ShowWarningAsync(
                        L.Get("Warning.Title"),
                        L.Get("VersionManagement.DeleteLastVersionNotAllowed"));
                    return;
                }
                
                // 1谿ｵ逶ｮ遒ｺ隱・
                var confirmed1 = await dialogService.ShowConfirmAsync(
                    L.Get("VersionManagement.DeleteConfirmTitle"),
                    L.Format("VersionManagement.DeleteConfirmMessage", versionVm.Version)
                );
                
                if (!confirmed1) return;
                
                // 2谿ｵ逶ｮ遒ｺ隱・
                var confirmed2 = await dialogService.ShowConfirmAsync(
                    L.Get("Confirm.FinalConfirmation"),
                    L.Format("VersionManagement.DeleteFinalConfirmMessage", versionVm.Version)
                );
                
                if (!confirmed2) return;
                
                // 繝舌・繧ｸ繝ｧ繝ｳ螻･豁ｴ縺九ｉ蜑企勁
                var versionToDelete = asset.VersionHistory.FirstOrDefault(v => v.Version == versionVm.Version);
                if (versionToDelete != null)
                {
                    asset.VersionHistory.Remove(versionToDelete);
                    
                    // 蜑企勁縺励◆繝舌・繧ｸ繝ｧ繝ｳ縺檎樟蝨ｨ縺ｮ繝舌・繧ｸ繝ｧ繝ｳ縺縺｣縺溷ｴ蜷医∫樟蝨ｨ縺ｮ繝舌・繧ｸ繝ｧ繝ｳ繧呈峩譁ｰ
                    if (versionVm.Version == asset.CurrentVersion)
                    {
                        // 蜑企勁縺励◆繝舌・繧ｸ繝ｧ繝ｳ繧医ｊ1縺､蜑阪・繝舌・繧ｸ繝ｧ繝ｳ繧堤樟蝨ｨ縺ｮ繝舌・繧ｸ繝ｧ繝ｳ縺ｫ縺吶ｋ
                        var newCurrentVersion = asset.VersionHistory
                            .Where(v => v.Version < versionVm.Version)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault();

                        if (newCurrentVersion == null)
                        {
                            newCurrentVersion = asset.VersionHistory
                                .Where(v => v.Version > versionVm.Version)
                                .OrderBy(v => v.Version)
                                .FirstOrDefault();
                        }
                        
                        if (newCurrentVersion != null)
                        {
                            asset.CurrentVersion = newCurrentVersion.Version;
                            
                            // 繧｢繝峨が繝ｳ繝ｪ繧ｹ繝医ｒ蠕ｩ蜈・
                            asset.Addons.Clear();
                            asset.Addons.AddRange(newCurrentVersion.AddonIds);
                            
                            // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ蠕ｩ蜈・ｼ井ｿ晏ｭ倥＆繧後※縺・ｋ蝣ｴ蜷茨ｼ・
                            if (newCurrentVersion.IncludeAddonStates && newCurrentVersion.AddonStates != null)
                            {
                                asset.AddonStates.Clear();
                                foreach (var kvp in newCurrentVersion.AddonStates)
                                {
                                    asset.AddonStates[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        else
                        {
                            // 蜑阪・繝舌・繧ｸ繝ｧ繝ｳ縺後↑縺・ｴ蜷医・v0縺ｫ縺吶ｋ・医◆縺縺苓｡ｨ遉ｺ縺ｯ縺輔ｌ縺ｪ縺・ｼ・
                            asset.CurrentVersion = 0;
                        }
                    }
                    
                    // 險ｭ螳壹ｒ菫晏ｭ・
                    await _addonManager.SaveConfigurationAsync();

                    await RefreshMainWindowLightAsync();
                    
                    // UI繧呈峩譁ｰ・亥炎髯､蠕後・迥ｶ諷九ｒ豁｣縺励￥蜿肴丐・・
                    LoadVersions();
                    this.RaisePropertyChanged(nameof(SortedVersions));
                    this.RaisePropertyChanged(nameof(Versions));
                    
                    // 蜑企勁蠕後∝燕縺ｮ繝舌・繧ｸ繝ｧ繝ｳ繧帝∈謚・
                    if (_versions.Any())
                    {
                        // 蜑企勁縺励◆繝舌・繧ｸ繝ｧ繝ｳ繧医ｊ1縺､蜑阪・繝舌・繧ｸ繝ｧ繝ｳ繧呈爾縺・
                        var previousVersion = _versions
                            .Where(v => v.Version < versionVm.Version)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault();
                        
                        // 蜑阪・繝舌・繧ｸ繝ｧ繝ｳ縺後↑縺・ｴ蜷医・縲∵ｬ｡縺ｮ繝舌・繧ｸ繝ｧ繝ｳ繧帝∈謚・
                        if (previousVersion == null)
                        {
                            previousVersion = _versions.OrderBy(v => v.Version).FirstOrDefault();
                        }
                        
                        SelectedVersion = previousVersion;
                    }
                    
                    await dialogService.ShowInfoAsync(
                        L.Get("Success.Title"),
                        L.Format("VersionManagement.DeleteCompleteMessage", versionVm.Version)
                    );
                }
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.DeleteFailed"));
            }
        }

        private async Task RenameVersionsAsync()
        {
            try
            {
                var dialogService = new DialogService();
                var asset = ResolveCurrentAsset();

                var versionsToRename = asset.VersionHistory
                    .Where(v => !v.IsImportBaseline && v.Version > 0)
                    .OrderBy(v => v.Version)
                    .ToList();

                if (versionsToRename.Count <= 1)
                {
                    await dialogService.ShowInfoAsync(
                        L.Get("Info.Title"),
                        L.Get("VersionManagement.RenameNoTargets"));
                    return;
                }

                var expected = 1;
                var needsRename = false;
                foreach (var version in versionsToRename)
                {
                    if (version.Version != expected)
                    {
                        needsRename = true;
                        break;
                    }
                    expected++;
                }

                if (!needsRename)
                {
                    await dialogService.ShowInfoAsync(
                        L.Get("Info.Title"),
                        L.Get("VersionManagement.RenameAlreadySequential"));
                    return;
                }

                var confirmed = await dialogService.ShowConfirmAsync(
                    L.Get("VersionManagement.RenameConfirmTitle"),
                    L.Get("VersionManagement.RenameConfirmMessage")
                );

                if (!confirmed)
                {
                    return;
                }

                var versionMap = new Dictionary<int, int>();
                var newVersion = 1;
                foreach (var version in versionsToRename)
                {
                    versionMap[version.Version] = newVersion;
                    version.Version = newVersion;
                    newVersion++;
                }

                if (versionMap.TryGetValue(asset.CurrentVersion, out var updatedCurrent))
                {
                    asset.CurrentVersion = updatedCurrent;
                }

                await _addonManager.SaveConfigurationAsync();
                await RefreshMainWindowLightAsync();

                LoadVersions();
                this.RaisePropertyChanged(nameof(SortedVersions));
                this.RaisePropertyChanged(nameof(Versions));

                SelectedVersion = _versions.FirstOrDefault(v => v.IsCurrent)
                    ?? _versions.OrderBy(v => v.IsImportBaseline ? int.MinValue : v.Version).FirstOrDefault();

                await dialogService.ShowInfoAsync(
                    L.Get("Success.Title"),
                    L.Get("VersionManagement.RenameCompleteMessage"));
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.RenameFailed"));
            }
        }
        
        private async Task ClearVersionHistoryAsync()
        {
            try
            {
                var dialogService = new DialogService();
                var asset = ResolveCurrentAsset();
                
                // 1谿ｵ逶ｮ遒ｺ隱・
                var confirmed1 = await dialogService.ShowConfirmAsync(
                    L.Get("VersionManagement.ClearHistoryConfirmTitle"),
                    L.Get("VersionManagement.ClearHistoryConfirmMessage")
                );
                
                if (!confirmed1) return;
                
                // 2谿ｵ逶ｮ遒ｺ隱・
                var confirmed2 = await dialogService.ShowConfirmAsync(
                    L.Get("Confirm.FinalConfirmation"),
                    L.Get("VersionManagement.ClearHistoryFinalConfirmMessage")
                );
                
                if (!confirmed2) return;
                
                // 繝舌・繧ｸ繝ｧ繝ｳ螻･豁ｴ繧偵け繝ｪ繧｢
                asset.VersionHistory.Clear();
                asset.CurrentVersion = 0;
                
                // 險ｭ螳壹ｒ菫晏ｭ・
                await _addonManager.SaveConfigurationAsync();
                
                // UI繧呈峩譁ｰ
                LoadVersions();
                SelectedVersion = _versions.FirstOrDefault();
                
                await dialogService.ShowInfoAsync(
                    L.Get("Success.Title"),
                    L.Get("VersionManagement.ClearHistoryCompleteMessage")
                );
                
                // 繧ｦ繧｣繝ｳ繝峨え繧帝哩縺倥ｋ
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.ClearHistoryFailed"));
            }
        }
        
        private long CalculateDirectorySize(DirectoryInfo dirInfo)
        {
            long size = 0;
            try
            {
                // 繝輔ぃ繧､繝ｫ縺ｮ繧ｵ繧､繧ｺ繧貞粋?E
                var files = dirInfo.GetFiles();
                foreach (var file in files)
                {
                    size += file.Length;
                }
                
                // 繧ｵ繝悶ョ繧｣繝ｬ繧ｯ繝医Μ縺ｮ繧ｵ繧､繧ｺ繧貞・蟶ｰ逧・↓險育ｮ・
                var subdirs = dirInfo.GetDirectories();
                foreach (var subdir in subdirs)
                {
                    size += CalculateDirectorySize(subdir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore access-denied entries while estimating size.
            }
            catch (PathTooLongException)
            {
                // Ignore too-long paths while estimating size.
            }
            catch (IOException)
            {
                // Ignore transient filesystem failures while estimating size.
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("VersionManagementViewModel.CalculateDirectorySize", ex);
            }
            return size;
        }
        
        // 繧ｭ繝｣繝・す繝･繧貞ｼｷ蛻ｶ逧・↓繝ｪ繝輔Ξ繝・す繝･縺吶ｋ
        public void RefreshCache()
        {
            _cachedAddonList = null;
            _lastScanTime = DateTime.MinValue;
        }
        
        private async Task ShowAddonDetailsAsync(string addonId)
        {
            try
            {
                var dialogService = new DialogService();
                var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={addonId}";
                await dialogService.ShowInfoAsync(
                    L.Get("VersionManagement.AddonDetailsTitle"),
                    L.Format("VersionManagement.AddonDetailsMessage", addonId, url));
                
                // 螳滄圀縺ｫ縺ｯ繝悶Λ繧ｦ繧ｶ縺ｧ髢九￥蜃ｦ逅・ｒ螳溯｣・
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(
                    L.Get("Error.Title"),
                    L.Get("VersionManagement.AddonDetailsFailed"));
            }
        }
        private async Task ReloadMainWindowAsync()
        {
            try
            {
                // MainWindowViewModel繧貞叙蠕励＠縺ｦ繝ｪ繝ｭ繝ｼ繝・
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
                    {
                        await mainVm.RefreshAddonsAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"Failed to reload main window: {ex.Message}");
            }
        }

        private async Task RefreshMainWindowLightAsync()
        {
            try
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    await RefreshMainWindowLightCoreAsync();
                }
                else
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshMainWindowLightCoreAsync);
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"Failed to refresh main window (light): {ex.Message}");
            }
        }

        private async Task RefreshMainWindowLightCoreAsync()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
                {
                    await mainVm.RefreshAddonsAsync(rescanWorkshop: false, showProgress: false);
                    return;
                }
            }

            var assetListVm = ViewModelLocator.AssetListViewModel;
            var addonGridVm = ViewModelLocator.AddonGridViewModel;

            if (assetListVm == null)
            {
                return;
            }

            var previousSelectedId = assetListVm.SelectedAsset?.Id;
            assetListVm.LoadAssets();

            if (!string.IsNullOrEmpty(previousSelectedId))
            {
                var selected = assetListVm.Assets.FirstOrDefault(a => a.Id == previousSelectedId)
                               ?? assetListVm.JunctionAsset.FirstOrDefault(a => a.Id == previousSelectedId);
                if (selected != null)
                {
                    assetListVm.SelectedAsset = selected;
                }
            }

            if (addonGridVm != null)
            {
                addonGridVm.SetCurrentAsset(assetListVm.SelectedAsset);
                addonGridVm.ApplyFilter();
            }
        }

        private void SyncAssetViewModelsFromConfiguration()
        {
            var assetListVm = ViewModelLocator.AssetListViewModel;
            if (assetListVm == null)
            {
                return;
            }

            assetListVm.RefreshAssetStates();

            var addonGridVm = ViewModelLocator.AddonGridViewModel;
            if (addonGridVm != null)
            {
                addonGridVm.SetCurrentAsset(assetListVm.SelectedAsset);
                addonGridVm.ApplyFilter();
            }
        }

        private string GetAssetDisplayName()
    {
        var asset = ResolveCurrentAsset();
        return asset.Id switch
        {
            "subscribe-system-asset" => L.Get("Asset.SubscribeAsset"),
            "junction-system-asset" => L.Get("Asset.Junction"),
            _ => asset.Name
        };
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
        {
            this.RaisePropertyChanged(nameof(AssetName));
            this.RaisePropertyChanged(nameof(AssetTitle));
            this.RaisePropertyChanged(nameof(SelectedVersionTitle));

            foreach (var version in _versions)
            {
                version.NotifyLanguageChanged();
            }

            foreach (var addon in _selectedVersionAddons)
            {
                addon.NotifyLanguageChanged();
            }
        }
    }

}

    public class VersionItemViewModel : ViewModelBase
    {
        private bool _isSelected;
        
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public int AddonCount { get; set; }
        public bool IsCurrent { get; set; }
        public bool IncludesStates { get; set; }
        public bool CanDelete { get; set; } = true; // 蜑企勁蜿ｯ閭ｽ繝輔Λ繧ｰ
        public bool IsImportBaseline { get; set; } // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺九←縺・°
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                this.RaisePropertyChanged(nameof(BorderColor));
                this.RaisePropertyChanged(nameof(OuterBorderColor));
                this.RaisePropertyChanged(nameof(OuterBorderThickness));
                this.RaisePropertyChanged(nameof(OuterBorderPadding));
            }
        }
        
        public string VersionDisplay => IsImportBaseline ? L.Get("Version.ImportBaseline") : $"v{Version}";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy/MM/dd HH:mm:ss");
        public string AddonCountDisplay => L.Format("VersionManagement.AddonCountFormat", AddonCount);
        public bool HasStateInfo => IncludesStates;
        public string StateInfoDisplay => IncludesStates ? L.Get("VersionManagement.StateInfoIncludes") : "";
        public void NotifyLanguageChanged()
        {
            this.RaisePropertyChanged(nameof(VersionDisplay));
            this.RaisePropertyChanged(nameof(AddonCountDisplay));
            this.RaisePropertyChanged(nameof(StateInfoDisplay));
        }
        
        public string BackgroundColor => IsCurrent ? "#1E3A5F" : "Transparent";
        
        // 繝懊・繝繝ｼ繧ｫ繝ｩ繝ｼ: 驕ｸ謚樔ｸｭ縺ｯ邱代∫樟蝨ｨ菴ｿ逕ｨ荳ｭ縺ｯ髱偵√◎繧御ｻ･螟悶・繧ｰ繝ｬ繝ｼ
        public string BorderColor
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "#4A90E2"; // ?EE縺ｯ髱抵ｼ育樟蝨ｨ菴ｿ逕ｨ荳ｭEE
                if (IsSelected)
                    return "#4CAF50"; // 驍ｱ繝ｻ
                if (IsCurrent)
                    return "#4A90E2"; // 髱抵ｼ・I縺ｧ菴ｿ繧上ｌ縺ｦ縺・ｋ繧｢繧ｯ繧ｻ繝ｳ繝医き繝ｩ繝ｼ・・
                return "#444444"; // 繝・ヵ繧ｩ繝ｫ繝医・繧ｰ繝ｬ繝ｼ
            }
        }
        
        // 螟門・縺ｮ繝懊・繝繝ｼ繧ｫ繝ｩ繝ｼ・磯∈謚樔ｸｭ縺九▽迴ｾ蝨ｨ菴ｿ逕ｨ荳ｭ縺ｮ蝣ｴ蜷医・縺ｿ邱托ｼ・
        public string OuterBorderColor
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "#4CAF50"; // 螟門・縺ｯ邱托ｼ磯∈謚樔ｸｭ・・
                return "Transparent";
            }
        }
        
        // 螟門・縺ｮ繝懊・繝繝ｼ縺ｮ螟ｪ縺・
        public string OuterBorderThickness
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "3";
                return "0";
            }
        }
        
        // 螟門・縺ｮ繝懊・繝繝ｼ縺ｮ繝代ョ繧｣繝ｳ繧ｰ
        public string OuterBorderPadding
        {
            get
            {
                if (IsSelected && IsCurrent)
                    return "3";
                return "0";
            }
        }
    }
    
    public class VersionAddonItemViewModel : ViewModelBase
    {
        private AddonItemViewModel _addonItemViewModel = null!;
        private AddonDiffStatus _status;
        
        public AddonItemViewModel AddonItemViewModel
        {
            get => _addonItemViewModel;
            set => this.RaiseAndSetIfChanged(ref _addonItemViewModel, value);
        }
        
        // AddonItemViewModel縺ｮ繝励Ο繝代ユ繧｣繧偵・繝ｭ繧ｭ繧ｷ
        public string AddonId => AddonItemViewModel?.AddonId ?? "";
        public string Title => AddonItemViewModel?.Title ?? "";
        public Bitmap? ThumbnailBitmap => AddonItemViewModel?.ThumbnailBitmap;
        public bool IsThumbnailLoading => AddonItemViewModel?.IsThumbnailLoading ?? false;
        public string FileSizeText => AddonItemViewModel?.FileSizeText ?? "";
        public string LastModifiedText => AddonItemViewModel?.LastModifiedText ?? "";
        
        public AddonDiffStatus Status
        {
            get => _status;
            set
            {
                this.RaiseAndSetIfChanged(ref _status, value);
                this.RaisePropertyChanged(nameof(BorderColor));
                this.RaisePropertyChanged(nameof(StatusColor));
                this.RaisePropertyChanged(nameof(StatusText));
            }
        }
        
        public string BorderColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "#666666"
        };
        
        public string StatusColor => Status switch
        {
            AddonDiffStatus.Added => "#4CAF50",
            AddonDiffStatus.Removed => "#F44336",
            _ => "#FFFFFF"
        };
        
        public string StatusText => Status switch
        {
            AddonDiffStatus.Added => L.Get("VersionManagement.DiffAdded"),
            AddonDiffStatus.Removed => L.Get("VersionManagement.DiffRemoved"),
            _ => ""
        };

        public void NotifyLanguageChanged()
        {
            this.RaisePropertyChanged(nameof(StatusText));
        }
        
        public ReactiveCommand<Unit, Unit> ShowDetailsCommand
        {
            get
            {
                if (AddonItemViewModel != null)
                {
                    return AddonItemViewModel.OpenWorkshopCommand;
                }
                return ReactiveCommand.Create(() => { });
            }
        }
    }
}

