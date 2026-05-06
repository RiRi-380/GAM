using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicData;
using DynamicData.Binding;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AddonGridViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;
    
    private ObservableCollection<AddonItemViewModel> allAddons;
    private ObservableCollection<AddonItemViewModel> filteredAddons;
    private readonly IDisposable filterSubscription;
    private readonly ObservableCollection<FilterOptionViewModel> addonTypeFilters = new();
    private readonly ObservableCollection<FilterOptionViewModel> addonTagFilters = new();
    private string filterText = "";
    private bool isLoading;
    private AssetItemViewModel? currentAsset;
    
    private bool ShowJunctionAsset => addonManager.DisableMode == DisableMode.Hard;
    private bool showOnlyAssetAddons;
    private bool isMultiSelectEnabled;
    private HashSet<string> selectedAddonIds;
    private AddonItemViewModel? selectedAddon;
    private bool isSelectionMode;
    private bool hasSelectedAddons;
    private int addonFilterIndex = 0; // 0=全て, 1=通常のみ, 2=キャッシュのみ, 3=ローカルのみ
    private DashboardViewModel? dashboardViewModel;
    private bool enableBackgroundTitleUpdates;
    private bool enableBackgroundAddonPreload;
    private bool enableLocalAddonsExperimental;
    private int baseFilteredCount;
    private CancellationTokenSource? backgroundPreloadCts;
    private CancellationTokenSource? metadataSupplementCts;
    private readonly System.Threading.SemaphoreSlim visibleLoadSemaphore = new System.Threading.SemaphoreSlim(3, 3);
    private readonly object visibleRangeLock = new object();
    private CancellationTokenSource? visibleRangeCts;
    private HashSet<string>? cachedExcludedAddonIds;
    private DateTime cachedExcludedAddonIdsUpdated = DateTime.MinValue;
    private bool disposed;
    private bool metadataSupplementUiSnapshotErrorLogged;
    private bool metadataSupplementCacheReadErrorLogged;
    private bool metadataSupplementWebErrorLogged;
    private bool metadataSupplementSaveErrorLogged;
    private static readonly bool ScrollPerfLogEnabled = string.Equals(
        Environment.GetEnvironmentVariable("GAM_SCROLL_PERF_LOG"),
        "1",
        StringComparison.OrdinalIgnoreCase);
    private static readonly object ScrollPerfLogLock = new object();
    private static readonly string ScrollPerfLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GmodAddonManager",
        "logs",
        "scroll_perf.log");

    public AddonGridViewModel(AddonManager addonManager, PendingChangeManager pendingChangeManager, GmodProcessWatcher processWatcher)
    {
        this.addonManager = addonManager;
        this.pendingChangeManager = pendingChangeManager;
        this.processWatcher = processWatcher;

        allAddons = new ObservableCollection<AddonItemViewModel>();
        filteredAddons = new ObservableCollection<AddonItemViewModel>();
        selectedAddonIds = new HashSet<string>();
        InitializeFilterOptions();
        ReloadSettings();

        // コマンドの初期化
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshWithProgressAsync);
        LoadDetailsCommand = ReactiveCommand.CreateFromTask<AddonItemViewModel>(LoadAddonDetailsAsync);
        AddSelectedAddonsCommand = ReactiveCommand.CreateFromTask(ShowAssetSelectionDialogAsync);
        SelectAllCommand = ReactiveCommand.Create(SelectAll);
        RemoveSelectedAddonsCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAddonsAsync);
        ChangeSelectedAddonStateCommand = ReactiveCommand.CreateFromTask<string>(ChangeSelectedAddonStateAsync);

        // フィルタリングの設定
        filterSubscription = this.WhenAnyValue(
                x => x.FilterText,
                x => x.ShowOnlyAssetAddons,
                x => x.CurrentAsset,
                x => x.AddonFilterIndex)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(_ => ApplyFilter());

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
            
    }

    public void ReloadSettings(AppSettings? settings = null)
    {
        var resolved = settings ?? AppSettings.Load();
        enableBackgroundTitleUpdates = resolved.EnableBackgroundTitleUpdates;
        enableBackgroundAddonPreload = resolved.EnableBackgroundAddonPreload;
        enableLocalAddonsExperimental = resolved.EnableLocalAddonsExperimental;
    }

    private void InitializeFilterOptions()
    {
        AddFilterOptions(addonTypeFilters, new (string Key, string LabelKey)[]
        {
            ("Gamemode", "AddonType.Gamemode"),
            ("Map", "AddonType.Map"),
            ("Weapon", "AddonType.Weapon"),
            ("Vehicle", "AddonType.Vehicle"),
            ("NPC", "AddonType.NPC"),
            ("Tool", "AddonType.Tool"),
            ("Entity", "AddonType.Entity"),
            ("Effects", "AddonType.Effects"),
            ("Model", "AddonType.Model"),
            ("ServerContent", "AddonType.ServerContent")
        });

        AddFilterOptions(addonTagFilters, new (string Key, string LabelKey)[]
        {
            ("Build", "AddonTag.Build"),
            ("Cartoon", "AddonTag.Cartoon"),
            ("Comic", "AddonTag.Comic"),
            ("Fun", "AddonTag.Fun"),
            ("Movie", "AddonTag.Movie"),
            ("Roleplay", "AddonTag.Roleplay"),
            ("Scenic", "AddonTag.Scenic"),
            ("Realism", "AddonTag.Realism"),
            ("Water", "AddonTag.Water")
        });

    }

    private void AddFilterOptions(ObservableCollection<FilterOptionViewModel> target, IEnumerable<(string Key, string LabelKey)> options)
    {
        foreach (var option in options)
        {
            var label = L.Get(option.LabelKey);
            if (label == option.LabelKey)
            {
                label = option.Key;
            }
            var filterOption = new FilterOptionViewModel(option.Key, label);
            filterOption.PropertyChanged += OnFilterOptionPropertyChanged;
            target.Add(filterOption);
        }
    }

    private void OnFilterOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterOptionViewModel.IsSelected))
        {
            ApplyFilter();
        }
    }

    private void OnCurrentAssetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetItemViewModel.Name) || string.IsNullOrEmpty(e.PropertyName))
        {
            this.RaisePropertyChanged(nameof(CurrentAssetDisplayName));
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
        {
            this.RaisePropertyChanged(nameof(SelectionButtonText));
            this.RaisePropertyChanged(nameof(SelectionActionLabel));
            this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
            this.RaisePropertyChanged(nameof(CurrentAssetDisplayName));
        }
    }

    private void CancelBackgroundPreload()
    {
        var cts = Interlocked.Exchange(ref backgroundPreloadCts, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelMetadataSupplement()
    {
        var cts = Interlocked.Exchange(ref metadataSupplementCts, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void QueueMetadataSupplement()
    {
        CancelMetadataSupplement();

        if (AllAddons.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        metadataSupplementCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await SupplementMissingTagsAndTypesAsync(cts.Token);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementMissingTagsAndTypesAsync", ex);
            }
        });
    }

    private async Task SupplementMissingTagsAndTypesAsync(CancellationToken token)
    {
        List<AddonItemViewModel> targets;
        try
        {
            targets = await Dispatcher.UIThread.InvokeAsync(() =>
                AllAddons.Where(NeedsMetadataSupplement).ToList());
        }
        catch (Exception ex)
        {
            if (!metadataSupplementUiSnapshotErrorLogged)
            {
                metadataSupplementUiSnapshotErrorLogged = true;
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementMissingTagsAndTypesAsync.UIThreadSnapshot", ex);
            }

            return;
        }

        if (targets.Count == 0 || token.IsCancellationRequested)
        {
            return;
        }

        Dictionary<string, WorkshopItemInfo> cacheDetails;
        try
        {
            cacheDetails = SteamWorkshopCacheReader.GetAddonDetails();
        }
        catch (Exception ex)
        {
            if (!metadataSupplementCacheReadErrorLogged)
            {
                metadataSupplementCacheReadErrorLogged = true;
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementMissingTagsAndTypesAsync.CacheRead", ex);
            }

            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var seeds = new Dictionary<string, MetadataSupplementSeed>(StringComparer.Ordinal);
        var missingTagIds = new List<string>();

        foreach (var addon in targets)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            cacheDetails.TryGetValue(addon.AddonId, out var info);

            var seed = new MetadataSupplementSeed(addon.AddonId);
            string[]? tagsToApply = null;
            string? typeToApply = null;

            if (!HasTagValues(addon.Tags) || string.IsNullOrWhiteSpace(addon.Type))
            {
                if (TryReadAddonJsonMetadata(addon, out var jsonType, out var jsonTags))
                {
                    if (!HasTagValues(addon.Tags) && jsonTags != null && jsonTags.Length > 0)
                    {
                        tagsToApply = NormalizeTags(jsonTags);
                    }

                    if (string.IsNullOrWhiteSpace(addon.Type) && !string.IsNullOrWhiteSpace(jsonType))
                    {
                        typeToApply = jsonType;
                    }
                }
            }

            if (!HasTagValues(addon.Tags) && tagsToApply == null && info != null)
            {
                tagsToApply = ParseNormalizedTags(info.Tags);
            }

            seed.Tags = tagsToApply;
            seed.Type = typeToApply;
            seeds[addon.AddonId] = seed;

            if (!HasTagValues(addon.Tags) && tagsToApply == null)
            {
                missingTagIds.Add(addon.AddonId);
            }
        }

        var webTags = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        if (missingTagIds.Count > 0 && !token.IsCancellationRequested)
        {
            try
            {
                var workshopService = addonManager.GetSteamWorkshopService();
                var detailsMap = await workshopService.GetWorkshopDetailsBatchAsync(
                    missingTagIds,
                    token,
                    treatAsHot: false,
                    requireTags: true);
                foreach (var kvp in detailsMap)
                {
                    var normalizedTags = NormalizeTags(kvp.Value.Tags);
                    if (normalizedTags != null && normalizedTags.Length > 0)
                    {
                        webTags[kvp.Key] = normalizedTags;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!metadataSupplementWebErrorLogged)
                {
                    metadataSupplementWebErrorLogged = true;
                    SafeFileLogger.TryLogException("AddonGridViewModel.SupplementMissingTagsAndTypesAsync.WebFetch", ex);
                }
            }
        }

        var updates = new List<MetadataSupplementUpdate>();
        foreach (var addon in targets)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!seeds.TryGetValue(addon.AddonId, out var seed))
            {
                continue;
            }

            var tagsToApply = seed.Tags;
            if (tagsToApply == null && webTags.TryGetValue(addon.AddonId, out var webTagsForAddon))
            {
                tagsToApply = webTagsForAddon;
            }

            var typeToApply = seed.Type;
            if (string.IsNullOrWhiteSpace(addon.Type) && string.IsNullOrWhiteSpace(typeToApply))
            {
                typeToApply = InferTypeFromTags(tagsToApply ?? addon.Tags?.ToArray());
            }

            if (tagsToApply == null && string.IsNullOrWhiteSpace(typeToApply))
            {
                continue;
            }

            updates.Add(new MetadataSupplementUpdate(addon.AddonId, tagsToApply, typeToApply));
        }

        if (updates.Count == 0 || token.IsCancellationRequested)
        {
            return;
        }

        var configUpdated = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var config = addonManager.GetConfiguration();
            var addonsById = AllAddons.ToDictionary(a => a.AddonId, StringComparer.Ordinal);

            foreach (var update in updates)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (addonsById.TryGetValue(update.AddonId, out var addonVm))
                {
                    var applyTags = !HasTagValues(addonVm.Tags) ? update.Tags : null;
                    var applyType = string.IsNullOrWhiteSpace(addonVm.Type) ? update.Type : null;
                    if (applyTags != null || !string.IsNullOrWhiteSpace(applyType))
                    {
                        addonVm.UpdateTagsAndType(applyTags, applyType);
                    }
                }

                if (config.AddonMetadata.TryGetValue(update.AddonId, out var metadata))
                {
                    var metadataChanged = false;
                    if (update.Tags != null && update.Tags.Length > 0 && !HasTagValues(metadata.Tags))
                    {
                        metadata.Tags = update.Tags;
                        metadataChanged = true;
                    }

                    if (!string.IsNullOrWhiteSpace(update.Type) && string.IsNullOrWhiteSpace(metadata.Type))
                    {
                        metadata.Type = update.Type;
                        metadataChanged = true;
                    }

                    if (metadataChanged)
                    {
                        config.AddonMetadata[update.AddonId] = metadata;
                        configUpdated = true;
                    }
                }
            }
        });

        if (configUpdated && !token.IsCancellationRequested)
        {
            try
            {
                await addonManager.SaveConfigurationAsync();
            }
            catch (Exception ex)
            {
                if (!metadataSupplementSaveErrorLogged)
                {
                    metadataSupplementSaveErrorLogged = true;
                    SafeFileLogger.TryLogException("AddonGridViewModel.SupplementMissingTagsAndTypesAsync.SaveConfiguration", ex);
                }
            }
        }

        if (!token.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(ApplyFilter);
        }
    }

    private static bool NeedsMetadataSupplement(AddonItemViewModel addon)
    {
        if (addon == null || !addon.HasWorkshopId)
        {
            return false;
        }

        return !HasTagValues(addon.Tags) || string.IsNullOrWhiteSpace(addon.Type);
    }

    private static bool HasTagValues(IEnumerable<string>? tags)
    {
        return tags != null && tags.Any(tag => !string.IsNullOrWhiteSpace(tag));
    }

    private static string[]? NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags == null)
        {
            return null;
        }

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();

        foreach (var tag in tags)
        {
            foreach (var part in SplitTagValue(tag))
            {
                var value = NormalizeTag(part);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (normalized.Add(value))
                {
                    results.Add(value);
                }
            }
        }

        return results.Count == 0 ? null : results.ToArray();
    }

    private static string[]? ParseNormalizedTags(string? tagsValue)
    {
        if (string.IsNullOrWhiteSpace(tagsValue))
        {
            return null;
        }

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();

        foreach (var part in SplitTagValue(tagsValue))
        {
            var tag = NormalizeTag(part);
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            if (normalized.Add(tag))
            {
                results.Add(tag);
            }
        }

        return results.Count == 0 ? null : results.ToArray();
    }

    private static bool TryReadAddonJsonMetadata(AddonItemViewModel addon, out string? type, out string[]? tags)
    {
        type = null;
        tags = null;

        if (addon == null)
        {
            return false;
        }

        var folderPath = addon.FolderPath;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        if (File.Exists(folderPath) && folderPath.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
        {
            return AddonJsonReader.TryReadFromGma(folderPath, out type, out tags);
        }

        if (Directory.Exists(folderPath))
        {
            var addonJsonPath = Path.Combine(folderPath, "addon.json");
            if (AddonJsonReader.TryReadFromFile(addonJsonPath, out type, out tags))
            {
                return true;
            }

            var inferredType = InferTypeFromFolder(folderPath);
            if (!string.IsNullOrWhiteSpace(inferredType))
            {
                type = inferredType;
            }

            var gmaPath = TryResolveGmaPath(folderPath, addon.AddonId);
            if (!string.IsNullOrWhiteSpace(gmaPath))
            {
                if (AddonJsonReader.TryReadFromGma(gmaPath, out var gmaType, out var gmaTags))
                {
                    type ??= gmaType;
                    tags ??= gmaTags;
                }
            }
        }

        return !string.IsNullOrWhiteSpace(type) || (tags != null && tags.Length > 0);
    }

    private static string? TryResolveGmaPath(string folderPath, string addonId)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                return null;
            }

            var directPath = Path.Combine(folderPath, $"{addonId}.gma");
            if (File.Exists(directPath))
            {
                return directPath;
            }

            return Directory.EnumerateFiles(folderPath, "*.gma").FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? InferTypeFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(folderPath, "gamemodes")))
        {
            return "Gamemode";
        }

        if (Directory.Exists(Path.Combine(folderPath, "maps")) &&
            Directory.EnumerateFiles(Path.Combine(folderPath, "maps"), "*.bsp").Any())
        {
            return "Map";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "weapons")))
        {
            return "Weapon";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "vehicles")))
        {
            return "Vehicle";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "npc")))
        {
            return "NPC";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "tools")))
        {
            return "Tool";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "entities")))
        {
            return "Entity";
        }

        if (Directory.Exists(Path.Combine(folderPath, "lua", "effects")))
        {
            return "Effects";
        }

        if (Directory.Exists(Path.Combine(folderPath, "models")))
        {
            return "Model";
        }

        return null;
    }

    private sealed class MetadataSupplementUpdate
    {
        public MetadataSupplementUpdate(string addonId, string[]? tags, string? type)
        {
            AddonId = addonId;
            Tags = tags;
            Type = type;
        }

        public string AddonId { get; }
        public string[]? Tags { get; }
        public string? Type { get; }
    }

    private sealed class MetadataSupplementSeed
    {
        public MetadataSupplementSeed(string addonId)
        {
            AddonId = addonId;
        }

        public string AddonId { get; }
        public string[]? Tags { get; set; }
        public string? Type { get; set; }
    }

    private HashSet<string> BuildExcludedAddonIds(Configuration config)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var allAddonIds = config.AddonMetadata.Keys.Where(id => id != "*").ToList();

        foreach (var asset in config.Assets)
        {
            if (!asset.Enabled)
            {
                continue;
            }

            if (asset.ContainsAllAddons())
            {
                if (asset.DefaultAddonState == AddonState.Excluded)
                {
                    foreach (var addonId in allAddonIds)
                    {
                        excluded.Add(addonId);
                    }
                    return excluded;
                }

                foreach (var kvp in asset.AddonStates)
                {
                    if (kvp.Value == AddonState.Excluded)
                    {
                        excluded.Add(kvp.Key);
                    }
                }
            }
            else
            {
                foreach (var addonId in asset.Addons)
                {
                    if (addonId == "*")
                    {
                        continue;
                    }

                    if (asset.GetAddonState(addonId) == AddonState.Excluded)
                    {
                        excluded.Add(addonId);
                    }
                }
            }
        }

        return excluded;
    }

    public ObservableCollection<AddonItemViewModel> AllAddons
    {
        get => allAddons;
        private set => SetAndRaise(ref allAddons, value);
    }

    public ObservableCollection<AddonItemViewModel> FilteredAddons
    {
        get => filteredAddons;
        private set
        {
            SetAndRaise(ref filteredAddons, value);
            this.RaisePropertyChanged(nameof(FilteredAddonsCount));
        }
    }

    public ObservableCollection<FilterOptionViewModel> AddonTypeFilters => addonTypeFilters;
    public ObservableCollection<FilterOptionViewModel> AddonTagFilters => addonTagFilters;

    public string FilterText
    {
        get => filterText;
        set => SetAndRaise(ref filterText, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        set => SetAndRaise(ref isLoading, value);
    }

    public AssetItemViewModel? CurrentAsset
    {
        get => currentAsset;
        set
        {
            if (ReferenceEquals(currentAsset, value))
            {
                return;
            }

            if (currentAsset != null)
            {
                currentAsset.PropertyChanged -= OnCurrentAssetPropertyChanged;
            }

            SetAndRaise(ref currentAsset, value);

            if (currentAsset != null)
            {
                currentAsset.PropertyChanged += OnCurrentAssetPropertyChanged;
            }

            this.RaisePropertyChanged(nameof(CurrentAssetDisplayName));
            this.RaisePropertyChanged(nameof(SelectionButtonText));
            this.RaisePropertyChanged(nameof(CanRemoveFromAsset));
            this.RaisePropertyChanged(nameof(AddonCountDisplay));
        }
    }

    public string CurrentAssetDisplayName
    {
        get => CurrentAsset?.Name ?? L.Get("AddonGrid.CurrentAssetNone") ?? "None";
    }

    public bool ShowOnlyAssetAddons
    {
        get => showOnlyAssetAddons;
        set => SetAndRaise(ref showOnlyAssetAddons, value);
    }

    public bool IsMultiSelectEnabled
    {
        get => isMultiSelectEnabled;
        set
        {
            SetAndRaise(ref isMultiSelectEnabled, value);
            if (!value)
            {
                // 複数選択を無効にしたら選択をクリア
                ClearSelection();
            }
        }
    }

    public AddonItemViewModel? SelectedAddon
    {
        get => selectedAddon;
        set => SetAndRaise(ref selectedAddon, value);
    }

    public DashboardViewModel? DashboardViewModel
    {
        get
        {
            if (dashboardViewModel == null)
            {
                dashboardViewModel = new DashboardViewModel(addonManager);
            }
            return dashboardViewModel;
        }
    }

    public int TotalAddonsCount => AllAddons.Count;
    public int FilteredAddonsCount => FilteredAddons.Count;
    
    public string AddonCountDisplay
    {
        get
        {
            if (CurrentAsset == null)
            {
                return $"({FilteredAddonsCount})";
            }

            if (!string.IsNullOrEmpty(FilterText))
            {
                var totalCount = baseFilteredCount > 0 ? baseFilteredCount : CurrentAsset.AddonCount;
                return $"({FilteredAddonsCount}/{totalCount})";
            }

            if (addonFilterIndex != 0)
            {
                return $"({FilteredAddonsCount}/{CurrentAsset.AddonCount})";
            }

            return $"({FilteredAddonsCount})";
        }
    }

    public bool IsSelectionMode
    {
        get => isSelectionMode;
        set
        {
            SetAndRaise(ref isSelectionMode, value);
            if (!value)
            {
                // 選択モードを解除したら選択をクリア
                ClearSelection();
            }
        }
    }

    public bool HasSelectedAddons
    {
        get => hasSelectedAddons;
        private set
        {
            SetAndRaise(ref hasSelectedAddons, value);
            this.RaisePropertyChanged(nameof(CanRemoveFromAsset));
        }
    }
    
    public int SelectedAddonsCount => selectedAddonIds.Count;
    
    public string SelectionButtonText => ShowJunctionAsset && currentAsset?.Id == "junction-system-asset"
        ? L.Get("Action.Restore")
        : L.Get("Action.Transfer");

    public string SelectionActionLabel => L.Format("AddonGrid.ActionFormat", SelectedAddonsCount, SelectionButtonText);

    public string SelectionDeleteLabel => L.Format("AddonGrid.DeleteFormat", SelectedAddonsCount);
    
    public bool CanRemoveFromAsset => HasSelectedAddons && 
                                      currentAsset != null && 
                                      !currentAsset.IsSystem &&
                                      currentAsset.Id != "junction-system-asset" &&
                                      currentAsset.Id != "subscribe-system-asset";
    
    public int AddonFilterIndex
    {
        get => addonFilterIndex;
        set
        {
            if (addonFilterIndex != value)
            {
                addonFilterIndex = value;
                this.RaisePropertyChanged(nameof(AddonFilterIndex));
                this.RaisePropertyChanged(nameof(AddonCountDisplay));
            }
        }
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<AddonItemViewModel, Unit> LoadDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSelectedAddonsCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedAddonsCommand { get; }
    public ReactiveCommand<string, Unit> ChangeSelectedAddonStateCommand { get; }

    public async Task LoadAddonsAsync()
    {
        try
        {
            IsLoading = true;
            ReloadSettings();
            CancelBackgroundPreload();
            CancelMetadataSupplement();
#if DEBUG
            // AddonGridViewModel.LoadAddonsAsync called
#endif
            
            // 新しいコレクションを作成
            var newAllAddons = new ObservableCollection<AddonItemViewModel>();

            // ScanWorkshopFolderAsyncは全てのアドオン（GMAファイル含む）を返す
#if DEBUG
            // Calling ScanWorkshopFolderAsync from AddonGridViewModel
#endif
            var addonList = await addonManager.ScanWorkshopFolderAsync();
#if DEBUG
            // ScanWorkshopFolderAsync returned {addonList.Count} addons
#endif
            
            // ローカルアドオンIDのセットを作成
            var localAddonIds = new HashSet<string>(addonList.Select(a => a.Id));
            
            // アセットに含まれているが、ローカルに存在しないアドオンも追加
            var config = addonManager.GetConfiguration();
            var allAssetAddonIds = new HashSet<string>();
            
            // すべてのアセットからアドオンIDを収集
            foreach (var asset in config.Assets)
            {
                // *を除外してアドオンIDを収集
                foreach (var addonId in asset.Addons.Where(id => id != "*"))
                {
                    allAssetAddonIds.Add(addonId);
                }
            }
            
            // アセットに登録されているが、ローカルに存在しないアドオンを追加
            foreach (var addonId in allAssetAddonIds)
            {
                if (!localAddonIds.Contains(addonId))
                {
                    if (!enableLocalAddonsExperimental && addonManager.IsLocalAddonId(addonId))
                    {
                        continue;
                    }

                    // メタデータから情報を取得
                    WorkshopAddon addonToAdd;
                    if (config.AddonMetadata.TryGetValue(addonId, out var metadata))
                    {
                        if (!enableLocalAddonsExperimental && metadata.IsLocal)
                        {
                            continue;
                        }

                        addonToAdd = new WorkshopAddon(metadata.Id, metadata.FolderPath)
                        {
                            Title = metadata.Title,
                            Size = metadata.Size,
                            LastUpdated = metadata.LastUpdated,
                            ThumbnailUrl = metadata.ThumbnailUrl,
                            Author = metadata.Author,
                            IsEnabled = metadata.IsEnabled,
                            Description = metadata.Description,
                            Type = metadata.Type,
                            Tags = metadata.Tags,
                            IsGmaFile = metadata.IsGmaFile,
                            NeedsTitleUpdate = metadata.NeedsTitleUpdate,
                            IsFavorite = metadata.IsFavorite,
                            IsLocal = metadata.IsLocal,
                            LocalMountPath = metadata.LocalMountPath,
                            LocalManagedPath = metadata.LocalManagedPath
                        };
                    }
                    else
                    {
                        // メタデータがない場合は基本情報のみで作成
                        addonToAdd = new WorkshopAddon(addonId, "")
                        {
                            Title = AddonTitleHelper.BuildPlaceholderTitle(addonId),
                            NeedsTitleUpdate = true
                        };
                    }
                    addonList.Add(addonToAdd);
                }
            }
            
            // 既存のViewModelのマッピングを作成（再利用のため）
            var existingViewModels = AllAddons.ToDictionary(vm => vm.AddonId, vm => vm);
            var reusedAddonIds = new HashSet<string>(StringComparer.Ordinal);
            
            foreach (var addon in addonList.OrderBy(a => a.Title ?? a.Id))
            {
                // 既存のViewModelがあれば再利用、なければ新規作成
                if (existingViewModels.TryGetValue(addon.Id, out var existingVm))
                {
                    reusedAddonIds.Add(addon.Id);
                    // 既存のViewModelを更新（タイトル等が変更されている可能性がある）
                    existingVm.UpdateTitle(addon.Title);
                    newAllAddons.Add(existingVm);
                }
                else
                {
                    var addonVm = new AddonItemViewModel(addon, addonManager, null); // logger removed
                    newAllAddons.Add(addonVm);
                }
            }

            foreach (var kvp in existingViewModels)
            {
                if (!reusedAddonIds.Contains(kvp.Key))
                {
                    kvp.Value.Dispose();
                }
            }
            
            // UIスレッドで一度に置き換える
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AllAddons = newAllAddons;
            });
            
            ApplyFilter();
            
            // プロパティ変更通知
            this.RaisePropertyChanged(nameof(FilteredAddonsCount));
            this.RaisePropertyChanged(nameof(TotalAddonsCount));
            
            // バックグラウンドでタイトルを更新
            if (enableBackgroundTitleUpdates)
            {
                _ = UpdateAddonTitlesInBackgroundAsync();
            }

            QueueMetadataSupplement();
            
            // logger.LogInformation($"Loaded {AllAddons.Count} addons"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to load addons", ex); // Removed logging
#if DEBUG
            // Failed to load addons: {ex}
#endif
            throw; // エラーを再スローして問題を明確にする
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyFilter()
    {
        try
        {
            CancelBackgroundPreload();

            var query = AllAddons.AsEnumerable();
            var config = addonManager.GetConfiguration();
            if (cachedExcludedAddonIds == null || config.LastUpdated != cachedExcludedAddonIdsUpdated)
            {
                cachedExcludedAddonIds = BuildExcludedAddonIds(config);
                cachedExcludedAddonIdsUpdated = config.LastUpdated;
            }

            var excludedAddonIds = cachedExcludedAddonIds;
            foreach (var addon in AllAddons)
            {
                addon.SetExcludedAddonIds(excludedAddonIds);
            }

            // Normal/Cacheフィルタ
            switch (addonFilterIndex)
            {
                case 1: // 通常のみ
                    query = query.Where(a => !a.IsGmaFile && !a.IsLocal);
                    break;
                case 2: // キャッシュのみ
                    query = query.Where(a => a.IsGmaFile && !a.IsLocal);
                    break;
                case 3: // ローカルのみ
                    query = query.Where(a => a.IsLocal);
                    break;
                // case 0: 全て表示（フィルタなし）
            }

            // アセットフィルタ
            if (ShowOnlyAssetAddons)
            {
                // CurrentAssetがnullの場合はフィルタリングしない（すべて表示）
                if (CurrentAsset == null)
                {
                    // アセットが未設定の場合は、すべてのアドオンを表示
                    // ただし、後で適切なアセットが設定されることを想定
                }
                else
                {
                    var assetAddonIds = CurrentAsset.GetAddonIds();
                
                // デバッグログ（ジャンクションアセットの問題調査用）
                if (CurrentAsset.Id == "junction-system-asset" && assetAddonIds.Count > 0)
                {
                    // logger.LogDebug($"Junction asset has {assetAddonIds.Count} addons"); // Removed logging
                }
                
                if (CurrentAsset.Id == "subscribe-system-asset" || assetAddonIds.Contains("*"))
                {
                    // 全アドオンを表示するが、ジャンクションアセットのアドオンは除外
                    var junctionAsset = addonManager.GetConfiguration().Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                    if (junctionAsset != null && junctionAsset.Addons.Count > 0)
                    {
                        var junctionAddonIds = new HashSet<string>(junctionAsset.Addons);
                        query = query.Where(a => !junctionAddonIds.Contains(a.AddonId));
                    }
                }
                    else
                    {
                        query = query.Where(a => assetAddonIds.Contains(a.AddonId));
                    }
                }
            }

            var selectedTypeFilters = GetSelectedFilterKeys(addonTypeFilters);
            var selectedAddonTags = GetSelectedFilterKeys(addonTagFilters);
            var totalTypeOptions = CountFilterOptions(addonTypeFilters);
            if (totalTypeOptions > 0 && selectedTypeFilters.Count == totalTypeOptions)
            {
                selectedTypeFilters.Clear();
            }

            var totalTagOptions = CountFilterOptions(addonTagFilters);
            if (totalTagOptions > 0 && selectedAddonTags.Count == totalTagOptions)
            {
                selectedAddonTags.Clear();
            }
            if (selectedTypeFilters.Count > 0 || selectedAddonTags.Count > 0)
            {
                query = query.Where(addon => MatchesFilterSelections(addon, selectedTypeFilters, selectedAddonTags));
            }

            // テキスト以外のフィルタ結果を確定
            var baseResults = query.ToList();

            // テキストフィルタ（選択済みのタグ/種別で絞り込んだ結果に対して適用）
            var results = baseResults;
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                results = baseResults.Where(a => a.MatchesFilter(FilterText)).ToList();
            }
            
            // 新しいコレクションを作成してから一度に置き換える
            var newFilteredAddons = new ObservableCollection<AddonItemViewModel>();
            foreach (var addon in results)
            {
                // 現在のアセットを設定して状態を更新
                addon.SetCurrentAsset(CurrentAsset);
                newFilteredAddons.Add(addon);
            }
            
            // UIスレッドで実行されていることを確認
            void UpdateFilteredView()
            {
                baseFilteredCount = baseResults.Count;
                FilteredAddons = newFilteredAddons;

                // フィルタ適用後、表示されているアドオンの詳細を読み込む
                _ = LoadVisibleAddonDetailsAsync();

                // アドオン数表示を更新
                this.RaisePropertyChanged(nameof(FilteredAddonsCount));
                this.RaisePropertyChanged(nameof(AddonCountDisplay));
            }

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                UpdateFilteredView();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateFilteredView);
            }
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to apply filter", ex); // Removed logging
        }
    }

    private static HashSet<string> GetSelectedFilterKeys(IEnumerable<FilterOptionViewModel> options)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (option != null && option.IsSelected)
            {
                var normalized = NormalizeTag(option.Key);
                if (!string.IsNullOrEmpty(normalized))
                {
                    selected.Add(normalized);
                }
            }
        }

        return selected;
    }

    private static int CountFilterOptions(IEnumerable<FilterOptionViewModel> options)
    {
        var count = 0;
        foreach (var option in options)
        {
            if (option != null)
            {
                count++;
            }
        }

        return count;
    }

    private static bool MatchesFilterSelections(
        AddonItemViewModel addon,
        HashSet<string> selectedTypeFilters,
        HashSet<string> selectedAddonTags)
    {
        if (addon == null)
        {
            return false;
        }

        var addonTags = BuildAddonTagSet(addon);

        if (selectedTypeFilters.Count > 0 && !MatchesType(addon, selectedTypeFilters, addonTags))
        {
            return false;
        }

        if (selectedAddonTags.Count > 0 && !ContainsAny(addonTags, selectedAddonTags))
        {
            return false;
        }

        return true;
    }

    private static HashSet<string> BuildAddonTagSet(AddonItemViewModel addon)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);

        if (addon.Tags == null)
        {
            return tags;
        }

        foreach (var tag in addon.Tags)
        {
            foreach (var part in SplitTagValue(tag))
            {
                var normalized = NormalizeTag(part);
                if (!string.IsNullOrEmpty(normalized))
                {
                    tags.Add(normalized);
                }
            }
        }

        return tags;
    }

    private static IEnumerable<string> SplitTagValue(string? tagValue)
    {
        if (string.IsNullOrWhiteSpace(tagValue))
        {
            yield break;
        }

        var separators = (tagValue.Contains(',') || tagValue.Contains(';'))
            ? new[] { ',', ';' }
            : new[] { ' ', '\t', '\r', '\n' };

        foreach (var part in tagValue.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static readonly (string Tag, string Type)[] TypeTagMappings =
    {
        ("gamemode", "Gamemode"),
        ("map", "Map"),
        ("weapon", "Weapon"),
        ("vehicle", "Vehicle"),
        ("npc", "NPC"),
        ("tool", "Tool"),
        ("entity", "Entity"),
        ("effect", "Effects"),
        ("effects", "Effects"),
        ("model", "Model"),
        ("servercontent", "ServerContent")
    };

    private static string? InferTypeFromTags(IEnumerable<string>? tags)
    {
        if (tags == null)
        {
            return null;
        }

        var tagSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            foreach (var part in SplitTagValue(tag))
            {
                var normalized = NormalizeTag(part);
                if (!string.IsNullOrEmpty(normalized))
                {
                    tagSet.Add(normalized);
                }
            }
        }

        if (tagSet.Count == 0)
        {
            return null;
        }

        foreach (var mapping in TypeTagMappings)
        {
            var key = NormalizeTag(mapping.Tag);
            if (ContainsMatch(tagSet, key))
            {
                return mapping.Type;
            }
        }

        return null;
    }

    private static bool MatchesType(AddonItemViewModel addon, HashSet<string> selectedTypeFilters, HashSet<string> addonTags)
    {
        var typeKey = NormalizeTag(addon.Type);
        var hasTypeKey = !string.IsNullOrEmpty(typeKey);
        HashSet<string>? typeKeySet = hasTypeKey
            ? new HashSet<string>(StringComparer.Ordinal) { typeKey }
            : null;

        foreach (var selected in selectedTypeFilters)
        {
            if (hasTypeKey)
            {
                if (string.Equals(typeKey, selected, StringComparison.Ordinal))
                {
                    return true;
                }

                if (typeKeySet != null && ContainsMatch(typeKeySet, selected))
                {
                    return true;
                }
            }

            if (ContainsMatch(addonTags, selected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(HashSet<string> addonTags, HashSet<string> selectedTags)
    {
        foreach (var selected in selectedTags)
        {
            if (ContainsMatch(addonTags, selected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsMatch(HashSet<string> tagSet, string key)
    {
        if (tagSet.Contains(key))
        {
            return true;
        }

        if (key.EndsWith("s", StringComparison.Ordinal) && key.Length > 1)
        {
            var singular = key.Substring(0, key.Length - 1);
            if (tagSet.Contains(singular))
            {
                return true;
            }
        }
        else
        {
            var plural = key + "s";
            if (tagSet.Contains(plural))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Dictionary<string, string> TagAliases = new(StringComparer.Ordinal)
    {
        { "scenery", "scenic" },
        { "roleplaying", "roleplay" },
        { "rp", "roleplay" },
        { "pose", "posed" }
    };

    private static string NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(ch);
        }

        var normalized = length == buffer.Length
            ? new string(buffer)
            : new string(buffer, 0, length);

        if (TagAliases.TryGetValue(normalized, out var alias))
        {
            return alias;
        }

        return normalized;
    }

    public void SetCurrentAsset(AssetItemViewModel? asset)
    {
        var previousAssetId = currentAsset?.Id;
        var nextAssetId = asset?.Id;
        if (!string.Equals(previousAssetId, nextAssetId, StringComparison.Ordinal))
        {
            if (IsSelectionMode)
            {
                // Keep toolbar visibility in sync with selection when asset context changes.
                IsSelectionMode = false;
            }
            else
            {
                ClearSelection();
            }
        }

        CurrentAsset = asset;
        ShowOnlyAssetAddons = asset != null;
        // アセットが設定されたらフィルターを再適用
        ApplyFilter();
        
        // デバッグ用ログ（起動時の問題調査）
        if (asset == null)
        {
            // [AddonGridViewModel] SetCurrentAsset: asset is null
        }
        else
        {
            // [AddonGridViewModel] SetCurrentAsset: {asset.Id}, ShowOnlyAssetAddons: {ShowOnlyAssetAddons}
        }
    }

    private async Task LoadAddonDetailsAsync(AddonItemViewModel addon)
    {
        if (addon != null && !addon.IsDetailsLoaded)
        {
            await addon.LoadDetailsCommand.Execute().GetAwaiter();
        }
    }

    public void SelectAddon(string addonId, bool isControlPressed = false)
    {
        var addon = FilteredAddons.FirstOrDefault(a => a.AddonId == addonId);
        if (addon != null)
        {
            if (IsSelectionMode || (IsMultiSelectEnabled && isControlPressed))
            {
                // 選択モードまたは複数選択モードでCtrlキー押下時はトグル
                addon.IsSelected = !addon.IsSelected;
                if (addon.IsSelected)
                {
                    selectedAddonIds.Add(addonId);
                }
                else
                {
                    selectedAddonIds.Remove(addonId);
                }
                HasSelectedAddons = selectedAddonIds.Count > 0;
                this.RaisePropertyChanged(nameof(SelectedAddonsCount));
                this.RaisePropertyChanged(nameof(SelectionActionLabel));
                this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
                
                // 選択アイテムが0になったら自動で選択モード解除
                if (IsSelectionMode && selectedAddonIds.Count == 0)
                {
                    IsSelectionMode = false;
                }
            }
            else
            {
                // 単一選択
                ClearSelection();
                addon.IsSelected = true;
                selectedAddonIds.Add(addonId);
                HasSelectedAddons = true;
                this.RaisePropertyChanged(nameof(SelectedAddonsCount));
                this.RaisePropertyChanged(nameof(SelectionActionLabel));
                this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
            }
        }
    }

    public void ClearSelection()
    {
        foreach (var a in FilteredAddons)
        {
            a.IsSelected = false;
        }
        selectedAddonIds.Clear();
        // 注: SelectedAddonは選択とは独立して管理（右クリックで設定）
        HasSelectedAddons = false;
        this.RaisePropertyChanged(nameof(SelectedAddonsCount));
        this.RaisePropertyChanged(nameof(SelectionActionLabel));
        this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
    }

    public ObservableCollection<AddonItemViewModel> GetSelectedAddons()
    {
        return new ObservableCollection<AddonItemViewModel>(
            FilteredAddons.Where(a => a.IsSelected)
        );
    }

    public async Task LoadVisibleAddonDetailsAsync()
    {
        try
        {
            var shouldLoadDetails = enableBackgroundAddonPreload;

            // 現在フィルタされて表示されているアドオンを取得
            var visibleAddons = FilteredAddons
                .Take(30)
                .Where(a => a.IsThumbnailLoading || (shouldLoadDetails && !a.IsDetailsLoaded))
                .ToList();
            
            // Loading details for visible addons
            
            // 表示されているアドオンの詳細とサムネイルを並列で読み込み
            using var semaphore = new System.Threading.SemaphoreSlim(6, 6);
            var token = CancellationToken.None;
            if (enableBackgroundAddonPreload)
            {
                backgroundPreloadCts ??= new CancellationTokenSource();
                token = backgroundPreloadCts.Token;
            }
            var tasks = visibleAddons
                .Select(addon => LoadAddonDetailsAndThumbnailAsync(addon, semaphore, shouldLoadDetails, allowRemote: true, token))
                .ToList();
            
            await Task.WhenAll(tasks);
            
            // 残りのアドオンはバックグラウンドで読み込み
            if (enableBackgroundAddonPreload)
            {
                _ = LoadRemainingAddonsAsync(visibleAddons, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation path when view/filter changes rapidly.
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.LoadVisibleAddonDetailsAsync", ex);
        }
    }

    private async Task RefreshWithProgressAsync()
    {
        // Prefer MainWindow refresh to keep one refresh path
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
            {
                await mainVm.RefreshAddonsAsync(showProgress: true);
                return;
            }
        }

        await LoadAddonsAsync();
    }

    private async Task LoadAddonDetailsAndThumbnailAsync(
        AddonItemViewModel addon,
        System.Threading.SemaphoreSlim semaphore,
        bool loadDetails,
        bool allowRemote,
        CancellationToken token)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(token);
            acquired = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (addon.IsThumbnailLoading)
            {
                await addon.LoadThumbnailAsync(allowRemote);
            }
            if (loadDetails && !addon.IsDetailsLoaded)
            {
                await addon.LoadDetailsBackgroundAsync();
            }
        }
        finally
        {
            if (acquired)
            {
                semaphore.Release();
            }
        }
    }
    
    public async Task LoadVisibleRangeAsync(int startIndex, int endIndex, bool allowRemote)
    {
        try
        {
            // 指定範囲のアドオンを取得
            var loadDetails = false;
            var rangeCount = Math.Max(0, endIndex - startIndex);
            var centerIndex = startIndex + (rangeCount / 2);
            var addonsToLoad = FilteredAddons
                .Skip(startIndex)
                .Take(rangeCount)
                .Select((addon, offset) => (addon, index: startIndex + offset))
                .Where(a => a.addon.IsThumbnailLoading || (loadDetails && !a.addon.IsDetailsLoaded))
                .OrderBy(a => Math.Abs(a.index - centerIndex))
                .Select(a => a.addon)
                .ToList();
            
            if (!addonsToLoad.Any())
            {
                return;
            }
            
            // 並列で読み込み
            CancellationToken token;
            lock (visibleRangeLock)
            {
                visibleRangeCts?.Cancel();
                visibleRangeCts?.Dispose();
                visibleRangeCts = new CancellationTokenSource();
                token = visibleRangeCts.Token;
            }

            var stopwatch = ScrollPerfLogEnabled ? Stopwatch.StartNew() : null;
            var tasks = addonsToLoad
                .Select(addon => LoadAddonDetailsAndThumbnailAsync(addon, visibleLoadSemaphore, loadDetails, allowRemote, token))
                .ToList();
            
            await Task.WhenAll(tasks);

            if (stopwatch != null)
            {
                stopwatch.Stop();
                LogScrollPerf($"[{DateTime.UtcNow:O}] range={startIndex}-{endIndex} allowRemote={allowRemote} total={rangeCount} queued={addonsToLoad.Count} ms={stopwatch.ElapsedMilliseconds}");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer range supersedes this request.
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.LoadVisibleRangeAsync", ex);
        }
    }

    private static void LogScrollPerf(string message)
    {
        if (!ScrollPerfLogEnabled)
        {
            return;
        }

        lock (ScrollPerfLogLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(ScrollPerfLogPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(ScrollPerfLogPath, message + Environment.NewLine);
            }
            catch
            {
                // Ignore logging failures
            }
        }
    }

    private async Task LoadRemainingAddonsAsync(List<AddonItemViewModel> alreadyLoaded, CancellationToken token)
    {
        try
        {
            var remainingAddons = FilteredAddons.Except(alreadyLoaded).ToList();
            
            foreach (var addon in remainingAddons)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (!addon.IsDetailsLoaded)
                    {
                        await addon.LoadDetailsBackgroundAsync();
                    }
                    if (addon.IsThumbnailLoading)
                    {
                        await addon.LoadThumbnailCommand.Execute();
                    }
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("AddonGridViewModel.LoadRemainingAddonsAsync.Item", ex);
                }

                try
                {
                    await Task.Delay(50, token); // 負荷分散のための遅延
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.LoadRemainingAddonsAsync", ex);
        }
    }
    
    private async Task UpdateAddonTitlesInBackgroundAsync()
    {
        try
        {
            // バックグラウンドでタイトルを更新
            await addonManager.UpdateAddonTitlesInBackgroundAsync();
            
            // タイトルが更新されたアドオンを反映
            await Task.Delay(2000); // 少し待機してから更新を確認
            
            // UIスレッドで更新
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var config = addonManager.GetConfiguration();
                foreach (var addon in AllAddons)
                {
                    if (config.AddonMetadata.ContainsKey(addon.AddonId))
                    {
                        var metadata = config.AddonMetadata[addon.AddonId];
                        if (!metadata.NeedsTitleUpdate && addon.Title != metadata.Title)
                        {
                            addon.UpdateTitle(metadata.Title);
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
#if DEBUG
            // Failed to update addon titles in background: {ex}
#endif
        }
    }

    private async Task PreloadThumbnailsAsync()
    {
        try
        {
            // logger?.LogInformation("Starting thumbnail preload"); // Removed logging
            
            // 表示されているアドオンから優先的に読み込む
            var visibleAddons = FilteredAddons.Take(30).ToList();
            var remainingAddons = AllAddons.Except(visibleAddons).ToList();
            
            // 表示中のアドオンのサムネイルを並列で読み込み（最大20つ同時）
            using var semaphore = new System.Threading.SemaphoreSlim(20, 20);
            var tasks = new List<Task>();
            
            foreach (var addon in visibleAddons)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await addon.LoadThumbnailCommand.Execute();
                    }
                    catch (Exception ex)
                    {
                        SafeFileLogger.TryLogException("AddonGridViewModel.PreloadThumbnailsAsync.VisibleItem", ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            // 表示中のアドオンの読み込みを待つ
            await Task.WhenAll(tasks);
            
            // 残りのアドオンもバックグラウンドで読み込み
            _ = Task.Run(async () =>
            {
                try
                {
                    using var backgroundSemaphore = new System.Threading.SemaphoreSlim(20, 20);
                    foreach (var addon in remainingAddons)
                    {
                        await backgroundSemaphore.WaitAsync();
                        try
                        {
                            await addon.LoadThumbnailCommand.Execute();
                        }
                        catch (Exception ex)
                        {
                            SafeFileLogger.TryLogException("AddonGridViewModel.PreloadThumbnailsAsync.Item", ex);
                        }
                        finally
                        {
                            backgroundSemaphore.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("AddonGridViewModel.PreloadThumbnailsAsync", ex);
                }
            });
            
            // logger?.LogInformation("Thumbnail preload started for visible items"); // Removed logging
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.PreloadThumbnailsAsync.Entry", ex);
        }
    }
    
    private async Task ShowAssetSelectionDialogAsync()
    {
        try
        {
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0)
            {
                return;
            }

            var dialogService = new DialogService();
            if (!ShowJunctionAsset && currentAsset?.Id == "junction-system-asset")
            {
                await dialogService.ShowErrorAsync(L.Get("Warning.Title"), L.Get("Warning.AssetUnavailableInMode"));
                return;
            }
            
            // AssetSelectionDialogを作成
            var assetListVm = ViewModelLocator.AssetListViewModel;
            
            if (assetListVm == null)
            {
                // logger.LogError("AssetListViewModel not found"); // Removed logging
                return;
            }
            
            // 全アセットリストを作成（サブスクライブとジャンクションを含む）
            var allAssets = new List<AssetItemViewModel>();
            allAssets.AddRange(assetListVm.Assets);
            if (ShowJunctionAsset)
            {
                allAssets.AddRange(assetListVm.JunctionAsset);
            }
            
            // アセットをソート（サブスクライブとジャンクションを最上位に）
            var sortedAssets = new List<AssetItemViewModel>();
            
            // サブスクライブを最初に
            var subscribeAsset = allAssets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            if (subscribeAsset != null) sortedAssets.Add(subscribeAsset);
            
            // ジャンクションを2番目に
            var junctionAsset = allAssets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (ShowJunctionAsset && junctionAsset != null) sortedAssets.Add(junctionAsset);
            
            // その他のアセット
            sortedAssets.AddRange(allAssets.Where(a => a != subscribeAsset && a != junctionAsset));
            
            if (!sortedAssets.Any())
            {
                await dialogService.ShowWarningAsync(L.Get("Warning.Title"), L.Get("Warning.NoAvailableAssets"));
                return;
            }
            
            // 現在のアセットがジャンクションかどうかで異なるダイアログを表示
            if (currentAsset?.Id == "junction-system-asset")
            {
                // ジャンクションアセットの場合は「戻す」ダイアログを表示
                var junctionDialog = new JunctionAssetSelectionDialog(sortedAssets, 
                    selectedAddons.Count == 1 ? addonManager.GetAddonSourceAssets(selectedAddons.First().AddonId) : new List<string>());
                var mainWindow = GetMainWindow();
                
                if (mainWindow != null)
                {
                    var result = await junctionDialog.ShowDialog<AssetSelectionResult?>(mainWindow);
                    
                    if (result != null)
                    {
                        if (result.RestoreToOriginal)
                        {
                            // 元の場所に戻す
                            using var progressDialog = ProgressDialogService.Show(
                                mainWindow,
                                L.Get("Busy.AddingAddonsToAsset"),
                                L.Format("Busy.Detail.AddonCount", selectedAddons.Count));
                            progressDialog?.UpdateProgress(0, selectedAddons.Count);

                            var current = 0;
                            foreach (var addon in selectedAddons)
                            {
                                addonManager.RestoreAddonFromJunction(addon.AddonId);
                                current++;
                                progressDialog?.UpdateProgress(current, selectedAddons.Count);
                            }
                            
                            // 状態を更新（ジャンクションの作成/削除を実行）
                            progressDialog?.UpdateStatus(L.Get("Busy.UpdatingAddonStates"));
                            var progress = progressDialog?.CreateProgress();
                            await addonManager.UpdateAddonStatesAsync(progress);
                            
                            await addonManager.SaveConfigurationAsync();

                            progressDialog?.Close();
                            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                L.Format("Success.RestoredToOriginal", selectedAddons.Count));
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                        else if (result.SelectedAsset != null)
                        {
                            // 選択したアセットに移動
                            using var progressDialog = ProgressDialogService.Show(
                                mainWindow,
                                L.Get("Busy.AddingAddonsToAsset"),
                                L.Format("Busy.Detail.AssetNameWithCount", result.SelectedAsset.Name, selectedAddons.Count));
                            progressDialog?.UpdateProgress(0, selectedAddons.Count);

                            var current = 0;
                            foreach (var addon in selectedAddons)
                            {
                                var state = currentAsset.GetAddonState(addon.AddonId);

                                // ジャンクションアセットから削除
                                addonManager.RemoveAddonFromAsset(currentAsset.Id, addon.AddonId);
                                
                                // 対象アセットに追加（個別状態も保持する）
                                addonManager.AddAddonToAsset(result.SelectedAsset.Id, addon.AddonId, state);
                                current++;
                                progressDialog?.UpdateProgress(current, selectedAddons.Count);
                            }
                            
                            // 状態を更新（ジャンクションの作成/削除を実行）
                            progressDialog?.UpdateStatus(L.Get("Busy.UpdatingAddonStates"));
                            var progress = progressDialog?.CreateProgress();
                            await addonManager.UpdateAddonStatesAsync(progress);
                            
                            await addonManager.SaveConfigurationAsync();

                            progressDialog?.Close();
                            await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                                L.Format("Success.RestoredToAsset", selectedAddons.Count, result.SelectedAsset.Name));
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                }
            }
            else
            {
                // 通常のアセットの場合は従来のダイアログを表示
                var dialog = new AssetSelectionDialog(sortedAssets, CreateAssetFromSelectionAsync);
                var mainWindow = GetMainWindow();
                
                if (mainWindow != null)
                {
                    var selectedAsset = await dialog.ShowDialog<AssetItemViewModel?>(mainWindow);

                    if (selectedAsset == null)
                    {
                        return;
                    }

                    if (!ShowJunctionAsset && selectedAsset.Id == "junction-system-asset")
                    {
                        await dialogService.ShowErrorAsync(L.Get("Warning.Title"), L.Get("Warning.AssetUnavailableInMode"));
                        return;
                    }

                    // ジャンクション送りの場合は確認
                    if (selectedAsset.Id == "junction-system-asset")
                    {
                        var confirmMessage = selectedAddons.Count == 1
                            ? L.Get("Confirm.SendToJunctionSingle")
                            : L.Format("Confirm.SendToJunctionMultiple", selectedAddons.Count);
                        
                        var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), confirmMessage);
                        if (!confirmed)
                        {
                            return;
                        }
                    }
                    
                    // 重複チェック
                    var targetAssetAddonIds = selectedAsset.GetAddonIds();
                    var duplicateAddons = selectedAddons.Where(a => targetAssetAddonIds.Contains(a.AddonId)).ToList();
                    var newAddons = selectedAddons.Except(duplicateAddons).ToList();
                    
                    // 全て重複している場合
                    if (duplicateAddons.Count == selectedAddons.Count)
                    {
                        var message = selectedAddons.Count == 1
                            ? L.Get("AddonGrid.DuplicateSingle")
                            : L.Format("AddonGrid.DuplicateMultiple", selectedAddons.Count);
                        await dialogService.ShowInfoAsync(L.Get("Info.Title"), message);
                        return;
                    }
                    
                    // 一部重複している場合
                    if (duplicateAddons.Count > 0)
                    {
                        var message = L.Format("AddonGrid.DuplicatePartial", selectedAddons.Count, duplicateAddons.Count);
                        
                        // 新規アドオンのみ追加
                        var addedCount = 0;
                        var isJunctionTransfer = selectedAsset.Id == "junction-system-asset";
                        
                        using var progressDialog = ProgressDialogService.Show(
                            mainWindow,
                            L.Get("Busy.AddingAddonsToAsset"),
                            L.Format("Busy.Detail.AssetNameWithCount", selectedAsset.Name, newAddons.Count));
                        progressDialog?.UpdateProgress(0, newAddons.Count);

                        var current = 0;
                        foreach (var addon in newAddons)
                        {
                            try
                            {
                                var state = CurrentAsset?.GetAddonState(addon.AddonId) ?? AddonState.Enabled;

                                // ジャンクション送りの場合、元のアセットから削除
                                if (isJunctionTransfer && CurrentAsset != null && !CurrentAsset.IsSystem)
                                {
                                    CurrentAsset.RemoveAddon(addon.AddonId);
                                }
                                
                                selectedAsset.AddAddon(addon.AddonId, state);
                                current++;
                                progressDialog?.UpdateProgress(current, newAddons.Count);
                                addedCount++;
                            }
                            catch (Exception ex)
                            {
                                // logger.LogError($"Failed to add addon {addon.AddonId} to asset {selectedAsset.Name}", ex); // Removed logging
                            }
                        }
                        
                        if (addedCount > 0)
                        {
                            // 設定を保存
                            await addonManager.SaveConfigurationAsync();
                            
                            // ジャンクションアセットを更新
                            if (isJunctionTransfer)
                            {
                                await addonManager.UpdateJunctionAssetAsync();
                            }
                            
                            selectedAsset.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == selectedAsset.Id));

                            progressDialog?.Close();
                            await ShowTransferSuccessDialogAsync(L.Get("Info.Title"), message, selectedAsset);
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                    else
                    {
                        // 重複なしの場合（従来の処理）
                        var addedCount = 0;
                        var isJunctionTransfer = selectedAsset.Id == "junction-system-asset";
                        
                        using var progressDialog = ProgressDialogService.Show(
                            mainWindow,
                            L.Get("Busy.AddingAddonsToAsset"),
                            L.Format("Busy.Detail.AssetNameWithCount", selectedAsset.Name, selectedAddons.Count));
                        progressDialog?.UpdateProgress(0, selectedAddons.Count);

                        var current = 0;
                        foreach (var addon in selectedAddons)
                        {
                            try
                            {
                                var state = CurrentAsset?.GetAddonState(addon.AddonId) ?? AddonState.Enabled;

                                // ジャンクション送りの場合、元のアセットから削除
                                if (isJunctionTransfer && CurrentAsset != null && !CurrentAsset.IsSystem)
                                {
                                    CurrentAsset.RemoveAddon(addon.AddonId);
                                }
                                
                                selectedAsset.AddAddon(addon.AddonId, state);
                                current++;
                                progressDialog?.UpdateProgress(current, selectedAddons.Count);
                                addedCount++;
                            }
                            catch (Exception ex)
                            {
                                // logger.LogError($"Failed to add addon {addon.AddonId} to asset {selectedAsset.Name}", ex); // Removed logging
                            }
                        }
                        
                        if (addedCount > 0)
                        {
                            // 設定を保存
                            await addonManager.SaveConfigurationAsync();
                            
                            // ジャンクションアセットを更新
                            if (isJunctionTransfer)
                            {
                                await addonManager.UpdateJunctionAssetAsync();
                            }
                            
                            selectedAsset.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == selectedAsset.Id));
                            
                            var message = isJunctionTransfer
                                ? (addedCount == 1 
                                    ? L.Get("Success.SentToJunctionSingle") 
                                    : L.Format("Success.SentToJunctionMultiple", addedCount))
                                : (addedCount == 1
                                    ? L.Format("Success.AddedToAssetSingle", selectedAsset.Name)
                                    : L.Format("Success.AddedToAssetMultiple", addedCount, selectedAsset.Name));

                            progressDialog?.Close();
                            await ShowTransferSuccessDialogAsync(L.Get("Success.Title"), message, selectedAsset);
                            
                            // 選択モードを解除
                            IsSelectionMode = false;
                            
                            // リロード処理
                            await ReloadAddons();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to show asset selection dialog", ex); // Removed logging
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.AssetSelectionDialogFailed"));
        }
    }

    private async Task<AssetItemViewModel?> CreateAssetFromSelectionAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return null;
        }

        var config = addonManager.GetConfiguration();
        var existingIds = new HashSet<string>(config.Assets.Select(a => a.Id));

        addonManager.CreateAsset(trimmedName);
        await addonManager.SaveConfigurationImmediatelyAsync();

        var newAsset = config.Assets.FirstOrDefault(a => !existingIds.Contains(a.Id));
        if (newAsset == null)
        {
            return null;
        }

        var settings = AppSettings.Load();
        var showExclusiveApply = DeveloperModeCommands.ShouldShowExclusiveApply(
            addonManager,
            settings.DeveloperModePhrase);
        return new AssetItemViewModel(newAsset, addonManager, pendingChangeManager, processWatcher, showExclusiveApply);
    }

    private async Task ShowTransferSuccessDialogAsync(string? title, string successMessage, AssetItemViewModel selectedAsset)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            return;
        }

        var dialog = new Window
        {
            Title = title ?? L.Get("Success.Title") ?? "Success",
            Width = 480,
            Height = 240,
            MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var result = false;

        var mainPanel = new DockPanel
        {
            LastChildFill = true
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 20)
        };

        var openButton = new Button
        {
            Width = 100,
            IsDefault = true,
            Content = L.Get("Dialog.Yes") ?? "Yes"
        };
        openButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        var closeButton = new Button
        {
            Width = 100,
            IsCancel = true,
            Content = L.Get("Dialog.No") ?? "No"
        };
        closeButton.Click += (_, _) => dialog.Close();

        buttonPanel.Children.Add(openButton);
        buttonPanel.Children.Add(closeButton);

        var openMessage = L.Format("Confirm.OpenCreatedAsset", selectedAsset.Name);
        if (string.IsNullOrWhiteSpace(openMessage))
        {
            openMessage = $"Open asset \"{selectedAsset.Name}\"?";
        }

        if (string.IsNullOrWhiteSpace(successMessage))
        {
            successMessage = L.Get("Success.Title") ?? "Success";
        }

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(20, 20, 20, 10)
        };

        var messagePanel = new StackPanel
        {
            Spacing = 10
        };

        messagePanel.Children.Add(new TextBlock
        {
            Text = successMessage,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 430
        });

        messagePanel.Children.Add(new TextBlock
        {
            Text = openMessage,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 430
        });

        scrollViewer.Content = messagePanel;

        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        mainPanel.Children.Add(buttonPanel);
        mainPanel.Children.Add(scrollViewer);

        dialog.Content = mainPanel;
        await dialog.ShowDialog(mainWindow);

        if (result)
        {
            SelectAssetInUi(selectedAsset.Id);
        }
    }

    private void SelectAssetInUi(string assetId)
    {
        var assetListVm = ViewModelLocator.AssetListViewModel;
        if (assetListVm == null)
        {
            return;
        }

        AssetItemViewModel? FindAsset()
        {
            return assetListVm.Assets.FirstOrDefault(a => a.Id == assetId)
                   ?? assetListVm.JunctionAsset.FirstOrDefault(a => a.Id == assetId);
        }

        void SelectCore()
        {
            var assetVm = FindAsset();
            if (assetVm == null)
            {
                // New assets may not be in the current list yet; refresh once and retry.
                assetListVm.LoadAssets();
                assetVm = FindAsset();
            }

            if (assetVm != null)
            {
                assetListVm.SelectedAsset = assetVm;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            SelectCore();
        }
        else
        {
            Dispatcher.UIThread.Post(SelectCore);
        }
    }

    private Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    private IDisposable? BeginBusy(string title, string? detail = null)
    {
        return ViewModelLocator.MainWindowViewModel?.BeginBusy(title, detail);
    }

    private void UpdateBusyProgress(int current, int total)
    {
        ViewModelLocator.MainWindowViewModel?.UpdateBusyProgress(current, total);
    }
    
    private async Task ReloadAddons(bool rescanWorkshop = true)
    {
        try
        {
            // MainWindowViewModelを取得してリロード
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
                {
                    await mainVm.RefreshAddonsAsync(rescanWorkshop, showProgress: false);
                }
            }
        }
        catch (Exception ex)
        {
            // logger.LogError("Failed to reload addons", ex); // Removed logging
        }
    }
    
    private void SelectAll()
    {
        try
        {
            if (FilteredAddons == null) return;
            
            // 全てのフィルタリングされたアドオンを選択
            foreach (var addon in FilteredAddons)
            {
                if (!addon.IsSelected)
                {
                    addon.IsSelected = true;
                    selectedAddonIds.Add(addon.AddonId);
                }
            }
            
            HasSelectedAddons = selectedAddonIds.Count > 0;
            this.RaisePropertyChanged(nameof(SelectedAddonsCount));
            this.RaisePropertyChanged(nameof(SelectionActionLabel));
            this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
            // logger?.LogInformation($"Selected all {FilteredAddons.Count} visible addons"); // Removed logging
        }
        catch (Exception ex)
        {
            // logger?.LogError("Failed to select all addons", ex); // Removed logging
        }
    }
    
    private void UpdateSelectionState()
    {
        HasSelectedAddons = selectedAddonIds.Count > 0;
        this.RaisePropertyChanged(nameof(SelectedAddonsCount));
        this.RaisePropertyChanged(nameof(SelectionActionLabel));
        this.RaisePropertyChanged(nameof(SelectionDeleteLabel));
    }
    
    private async Task RemoveSelectedAddonsAsync()
    {
        try
        {
            if (currentAsset == null || currentAsset.IsSystem) return;
            
            var selectedAddons = GetSelectedAddons();
            if (selectedAddons.Count == 0) return;
            
            var dialogService = new DialogService();
            var confirmMessage = selectedAddons.Count == 1
                ? L.Get("Confirm.RemoveFromAssetSingle")
                : L.Format("Confirm.RemoveFromAssetMultiple", selectedAddons.Count);
                
            var confirmed = await dialogService.ShowConfirmAsync(L.Get("Confirm.Title"), confirmMessage);
            
            if (confirmed)
            {
                // アセットから一括削除
                var addonIds = selectedAddons.Select(a => a.AddonId).ToList();
                var mainWindow = GetMainWindow();
                using var progressDialog = ProgressDialogService.Show(
                    mainWindow,
                    L.Get("Busy.UpdatingAddonStates"),
                    L.Format("Busy.Detail.AddonCount", selectedAddons.Count));
                var progress = progressDialog?.CreateProgress();

                addonManager.RemoveAddonsFromAssetBatch(currentAsset.Id, addonIds, progress);
                
                await addonManager.SaveConfigurationAsync();

                progressDialog?.Close();
                await dialogService.ShowInfoAsync(L.Get("Success.Title"), 
                    selectedAddons.Count == 1 
                        ? L.Get("Success.RemovedFromAssetSingle") 
                        : L.Format("Success.RemovedFromAssetMultiple", selectedAddons.Count));
                
                // 選択モードを解除
                IsSelectionMode = false;
                
                // リロード処理
                await ReloadAddons(rescanWorkshop: false);
            }
        }
        catch (Exception ex)
        {
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.RemoveAddonFailed"));
        }
    }
    
        private async Task ChangeSelectedAddonStateAsync(string action)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(action))
                    return;
                
                var selectedAddons = GetSelectedAddons();
                if (selectedAddons.Count == 0)
                    return;
                
                var selectedAddonIds = selectedAddons.Select(addon => addon.AddonId).ToList();
                
            // アセットが選択されていない場合は状態変更不可
            if (currentAsset == null)
                return;
                
                AddonState newState;
                // Check against localized values
                if (action == L.Get("AddonGrid.Enable"))
                {
                    newState = AddonState.Enabled;
                }
                else if (action == L.Get("AddonGrid.Disable"))
                {
                    newState = AddonState.Disabled;
                    
                    // Check if Steam is running before disabling addons
                    if (SteamProcessChecker.IsSteamRunning())
                    {
                        var dialog = new DialogService();
                        var result = await dialog.ShowConfirmAsync(
                            L.Get("Warning.SteamRunningTitle") ?? "Steam Running",
                            L.Get("Warning.SteamRunningDisable") ?? 
                            "Steam is currently running. Disabled addons may be re-downloaded when you start Garry's Mod.\n\n" +
                            "For best results:\n" +
                            "1. Close Garry's Mod\n" +
                            "2. Close Steam completely\n" +
                            "3. Disable addons in GAM\n" +
                            "4. Restart Steam\n\n" +
                            "Continue anyway?"
                        );
                        
                        if (!result)
                            return;
                    }
                }
                else if (action == L.Get("AddonGrid.Exclude"))
                {
                    newState = AddonState.Excluded;
                }
                else
                {
                    return;
                }
        
                // GMod稼働中は即時適用せず、状態だけ保存して後で反映する
                if (processWatcher.IsGmodRunning)
                {
                    addonManager.SetAddonStatesBatch(currentAsset.Id, selectedAddonIds, newState);
                    await addonManager.SaveConfigurationAsync();
        
                    // 後でUpdateAddonStatesAsyncを走らせるためのトリガー
                    pendingChangeManager.QueueChange(new AddonChange("apply_states", "*"));
        
                    var dialog = new DialogService();
                    await dialog.ShowInfoAsync(
                        L.Get("Info.Title"),
                        L.Get("Info.PendingAfterGmodExit")
                    );
                    return;
                }
                
                var mainWindow = GetMainWindow();
                using var progressDialog = ProgressDialogService.Show(
                    mainWindow,
                    L.Get("Busy.UpdatingAddonStates"),
                    L.Format("Busy.Detail.AddonCount", selectedAddons.Count));
                var progress = progressDialog?.CreateProgress();
        
                addonManager.SetAddonStatesBatch(currentAsset.Id, selectedAddonIds, newState, progress);
                
                // 状態を更新
                progressDialog?.UpdateStatus(L.Get("Busy.UpdatingAddonStates"));
                await addonManager.UpdateAddonStatesAsync(progressDialog?.CreateProgress());
                await addonManager.SaveConfigurationAsync();
                
                var dialogService = new DialogService();
                progressDialog?.Close();
                await dialogService.ShowInfoAsync(L.Get("Success.Title"),
                    L.Format("Success.StateChanged", selectedAddons.Count, action));
                    
                // 選択モードを解除
                IsSelectionMode = false;
                
                // リロード
                await ReloadAddons();
            }
            catch (Exception ex)
            {
                var dialogService = new DialogService();
                await dialogService.ShowErrorAsync(L.Get("Error.Title"), L.Get("Error.StateChangeFailed"));
            }
        }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        filterSubscription.Dispose();
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;

        if (currentAsset != null)
        {
            currentAsset.PropertyChanged -= OnCurrentAssetPropertyChanged;
        }

        foreach (var option in addonTypeFilters)
        {
            option.PropertyChanged -= OnFilterOptionPropertyChanged;
        }

        foreach (var option in addonTagFilters)
        {
            option.PropertyChanged -= OnFilterOptionPropertyChanged;
        }

        backgroundPreloadCts?.Cancel();
        backgroundPreloadCts?.Dispose();
        backgroundPreloadCts = null;

        metadataSupplementCts?.Cancel();
        metadataSupplementCts?.Dispose();
        metadataSupplementCts = null;

        visibleRangeCts?.Cancel();
        visibleRangeCts?.Dispose();
        visibleRangeCts = null;

        foreach (var addon in allAddons)
        {
            addon.Dispose();
        }

        allAddons.Clear();
        filteredAddons.Clear();
        visibleLoadSemaphore.Dispose();
    }
    
}


