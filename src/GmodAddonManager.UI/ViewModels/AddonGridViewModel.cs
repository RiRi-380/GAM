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
using Newtonsoft.Json;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AddonGridViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly AddonSortService addonSortService = new();
    private readonly string sortSettingsPath;
    private readonly ObservableCollection<string> sortModeOptions = new();
    
    private ObservableCollection<AddonItemViewModel> allAddons;
    private ObservableCollection<AddonItemViewModel> filteredAddons;
    private readonly IDisposable filterSubscription;
    private readonly ObservableCollection<FilterOptionViewModel> addonTypeFilters = new();
    private readonly ObservableCollection<FilterOptionViewModel> addonTagFilters = new();
    private string filterText = "";
    private bool isLoading;
    private AssetItemViewModel? currentAsset;
    
    private bool showOnlyAssetAddons;
    private bool isMultiSelectEnabled;
    private HashSet<string> selectedAddonIds;
    private AddonItemViewModel? selectedAddon;
    private bool isSelectionMode;
    private bool hasSelectedAddons;
    private int addonFilterIndex = 0; // 0=All, 1=Enabled, 2=Disabled/Excluded
    private HashSet<string> currentSubscribedAddonIds = new(StringComparer.Ordinal);
    private DashboardViewModel? dashboardViewModel;
    private bool enableBackgroundTitleUpdates;
    private bool enableBackgroundAddonPreload;
    private int selectedSortModeIndex;
    private AddonSortDirection sortDirection = AddonSortDirection.Descending;
    private int baseFilteredCount;
    private CancellationTokenSource? backgroundPreloadCts;
    private CancellationTokenSource? metadataSupplementCts;
    private readonly System.Threading.SemaphoreSlim visibleLoadSemaphore = new System.Threading.SemaphoreSlim(3, 3);
    private readonly object visibleRangeLock = new object();
    private CancellationTokenSource? visibleRangeCts;
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

    public AddonGridViewModel(
        AddonManager addonManager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher,
        string? sortSettingsPath = null)
    {
        this.addonManager = addonManager;
        this.pendingChangeManager = pendingChangeManager;
        this.processWatcher = processWatcher;
        this.sortSettingsPath = sortSettingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GmodAddonManager",
            "addon-sort.json");

        allAddons = new ObservableCollection<AddonItemViewModel>();
        filteredAddons = new ObservableCollection<AddonItemViewModel>();
        selectedAddonIds = new HashSet<string>();
        RefreshSortModeOptions();
        LoadSortSettings();
        InitializeFilterOptions();
        ReloadSettings();

        // コマンドの初期化
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshWithProgressAsync);
        LoadDetailsCommand = ReactiveCommand.CreateFromTask<AddonItemViewModel>(LoadAddonDetailsAsync);
        AddSelectedAddonsCommand = ReactiveCommand.CreateFromTask(ShowAssetSelectionDialogAsync);
        SelectAllCommand = ReactiveCommand.Create(SelectAll);
        RemoveSelectedAddonsCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAddonsAsync);
        ToggleSortDirectionCommand = ReactiveCommand.Create(ToggleSortDirection);

        // フィルタリングの設定
        filterSubscription = this.WhenAnyValue(
                x => x.FilterText,
                x => x.ShowOnlyAssetAddons,
                x => x.CurrentAsset,
                x => x.AddonFilterIndex)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter());

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        processWatcher.GmodStarted += OnGmodRuntimeStateChanged;
        processWatcher.GmodStopped += OnGmodRuntimeStateChanged;
            
    }

    public void ReloadSettings(AppSettings? settings = null)
    {
        var resolved = settings ?? AppSettings.Load();
        enableBackgroundTitleUpdates = resolved.EnableBackgroundTitleUpdates;
        enableBackgroundAddonPreload = resolved.EnableBackgroundAddonPreload;
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
            RefreshSortModeOptions();
            this.RaisePropertyChanged(nameof(SelectedSortModeIndex));
            this.RaisePropertyChanged(nameof(SortDirectionLabel));
            ApplyFilter();
        }
    }

    private void OnGmodRuntimeStateChanged(object? sender, ProcessEventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyFilter, DispatcherPriority.Background);
    }

    private AddonSortOptions CurrentSortOptions => new()
    {
        Mode = (AddonSortMode)selectedSortModeIndex,
        Direction = sortDirection
    };

    private void RefreshSortModeOptions()
    {
        var japanese = LocalizationManager.Instance.CurrentLanguage
            .StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var labels = japanese
            ? new[] { "最近購読", "名前", "容量", "Workshop更新" }
            : new[] { "Recently subscribed", "Name", "Size", "Workshop updated" };

        sortModeOptions.Clear();
        foreach (var label in labels)
        {
            sortModeOptions.Add(label);
        }
    }

    private void ToggleSortDirection()
    {
        sortDirection = sortDirection == AddonSortDirection.Ascending
            ? AddonSortDirection.Descending
            : AddonSortDirection.Ascending;
        this.RaisePropertyChanged(nameof(SortDirectionLabel));
        SaveSortSettings();
        ApplyFilter();
    }

    private void LoadSortSettings()
    {
        try
        {
            if (!File.Exists(sortSettingsPath))
            {
                return;
            }

            var persisted = JsonConvert.DeserializeObject<PersistedAddonSortSettings>(
                File.ReadAllText(sortSettingsPath));
            if (persisted == null ||
                !Enum.IsDefined(persisted.Mode) ||
                !Enum.IsDefined(persisted.Direction))
            {
                return;
            }

            selectedSortModeIndex = (int)persisted.Mode;
            sortDirection = persisted.Direction;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.LoadSortSettings", ex);
        }
    }

    private void SaveSortSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(sortSettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = sortSettingsPath + ".tmp";
            var json = JsonConvert.SerializeObject(
                new PersistedAddonSortSettings
                {
                    Mode = (AddonSortMode)selectedSortModeIndex,
                    Direction = sortDirection
                },
                Formatting.Indented);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, sortSettingsPath, true);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonGridViewModel.SaveSortSettings", ex);
        }
    }

    private sealed class PersistedAddonSortSettings
    {
        public AddonSortMode Mode { get; set; } = AddonSortMode.RecentlySubscribed;

        public AddonSortDirection Direction { get; set; } = AddonSortDirection.Descending;
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
    public ObservableCollection<string> SortModeOptions => sortModeOptions;

    public int SelectedSortModeIndex
    {
        get => selectedSortModeIndex;
        set
        {
            if (value < (int)AddonSortMode.RecentlySubscribed ||
                value > (int)AddonSortMode.WorkshopUpdated ||
                selectedSortModeIndex == value)
            {
                return;
            }

            selectedSortModeIndex = value;
            this.RaisePropertyChanged(nameof(SelectedSortModeIndex));
            SaveSortSettings();
            ApplyFilter();
        }
    }

    public string SortDirectionLabel
    {
        get
        {
            var japanese = LocalizationManager.Instance.CurrentLanguage
                .StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            return sortDirection == AddonSortDirection.Ascending
                ? japanese ? "昇順 ↑" : "Ascending ↑"
                : japanese ? "降順 ↓" : "Descending ↓";
        }
    }

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
            if (CurrentAsset?.IsSubscribeAsset == true && ShowOnlyAssetAddons)
            {
                var availableCount = AllAddons.Count(addon =>
                    addon.IsAvailable &&
                    currentSubscribedAddonIds.Contains(addon.AddonId));
                return FormatSubscriptionCountDisplay(
                    FilteredAddonsCount,
                    availableCount,
                    currentSubscribedAddonIds.Count,
                    LocalizationManager.Instance.CurrentLanguage.StartsWith(
                        "ja",
                        StringComparison.OrdinalIgnoreCase));
            }

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
    
    public string SelectionButtonText => L.Get("Action.Transfer");

    public string SelectionActionLabel => L.Format("AddonGrid.ActionFormat", SelectedAddonsCount, SelectionButtonText);

    public string SelectionDeleteLabel => L.Format("AddonGrid.DeleteFormat", SelectedAddonsCount);
    
    public bool CanRemoveFromAsset => HasSelectedAddons && 
                                      currentAsset != null && 
                                      !currentAsset.IsSystem;
    
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
    public ReactiveCommand<Unit, Unit> ToggleSortDirectionCommand { get; }

    public async Task LoadAddonsAsync(CancellationToken cancellationToken = default)
    {
        var loadingStarted = false;
        try
        {
            AddonSortOptions? sortOptions = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);

                IsLoading = true;
                loadingStarted = true;
                ReloadSettings();
                CancelBackgroundPreload();
                CancelMetadataSupplement();
                sortOptions = CurrentSortOptions;
            });
#if DEBUG
            // AddonGridViewModel.LoadAddonsAsync called
#endif

            // ScanWorkshopFolderAsync contains synchronous directory enumeration
            // around its awaits. Run the whole inventory pipeline on a worker so
            // no continuation can capture and block Avalonia's UI context.
#if DEBUG
            // Calling ScanWorkshopFolderAsync from AddonGridViewModel
#endif
            var preparedInventory = await Task.Run(
                () => PrepareAddonInventoryAsync(sortOptions!, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
#if DEBUG
            // ScanWorkshopFolderAsync returned {preparedInventory.Addons.Count} addons
#endif

            // Bound view models and collections are created, updated, disposed,
            // and replaced only on Avalonia's UI thread.
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (disposed)
                    {
                        return;
                    }

                    ApplyPreparedAddonInventory(preparedInventory);
                },
                DispatcherPriority.Normal,
                cancellationToken);
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
            if (loadingStarted &&
                !disposed &&
                !cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (!disposed && !cancellationToken.IsCancellationRequested)
                        {
                            IsLoading = false;
                        }
                    },
                    DispatcherPriority.Normal,
                    cancellationToken);
            }
        }
    }

    private async Task<PreparedAddonInventory> PrepareAddonInventoryAsync(
        AddonSortOptions sortOptions,
        CancellationToken cancellationToken)
    {
        var addonList = await addonManager.ScanWorkshopFolderAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        addonList = addonList
            .Where(addon => !addon.IsLocal && !addon.IsDownloadPending)
            .ToList();

        var config = addonManager.GetConfiguration();
        var subscribedAddonIds = new HashSet<string>(
            addonManager.GetResolvedAddonStates().Keys,
            StringComparer.Ordinal);
        var loadedAddonIds = new HashSet<string>(
            addonList.Select(addon => addon.Id),
            StringComparer.Ordinal);

        // Only confirmed-unsubscribed references retained by custom assets get a
        // synthetic unavailable card. Subscribed-but-pending IDs stay aggregate-only.
        var customAssetAddonIds = config.Assets
            .Where(asset => !asset.IsSystem)
            .SelectMany(asset => asset.Addons)
            .Where(addonId => addonId != "*")
            .Distinct(StringComparer.Ordinal);

        foreach (var addonId in customAssetAddonIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loadedAddonIds.Contains(addonId) ||
                addonManager.IsLocalAddonId(addonId) ||
                !config.AddonMetadata.TryGetValue(addonId, out var metadata) ||
                !ShouldAddRetainedMissingAddon(
                    config.RetainMissingAssetReferences,
                    config.SubscriptionBaselineInitialized,
                    metadata,
                    subscribedAddonIds.Contains(addonId)))
            {
                continue;
            }

            addonList.Add(new WorkshopAddon(metadata.Id, metadata.FolderPath)
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
                LocalManagedPath = metadata.LocalManagedPath,
                FirstSeenSubscribedAtUtc = metadata.FirstSeenSubscribedAtUtc,
                WorkshopUpdatedAtUtc = metadata.WorkshopUpdatedAtUtc,
                IsAvailable = false,
                IsDownloadPending = false
            });
        }

        var sortedAddons = addonSortService.Sort(addonList, sortOptions).ToList();
        return new PreparedAddonInventory(sortedAddons, subscribedAddonIds);
    }

    private void ApplyPreparedAddonInventory(PreparedAddonInventory inventory)
    {
        var newAllAddons = new ObservableCollection<AddonItemViewModel>();
        var existingViewModels = AllAddons.ToDictionary(vm => vm.AddonId, vm => vm);
        var reusedAddonIds = new HashSet<string>(StringComparer.Ordinal);

        currentSubscribedAddonIds = inventory.SubscribedAddonIds;
        foreach (var addon in inventory.Addons)
        {
            if (existingViewModels.TryGetValue(addon.Id, out var existingVm))
            {
                reusedAddonIds.Add(addon.Id);
                existingVm.UpdateFromWorkshopAddon(addon);
                newAllAddons.Add(existingVm);
            }
            else
            {
                newAllAddons.Add(new AddonItemViewModel(addon, addonManager, null));
            }
        }

        foreach (var kvp in existingViewModels)
        {
            if (!reusedAddonIds.Contains(kvp.Key))
            {
                kvp.Value.Dispose();
            }
        }

        AllAddons = newAllAddons;
        ApplyFilter();
        this.RaisePropertyChanged(nameof(FilteredAddonsCount));
        this.RaisePropertyChanged(nameof(TotalAddonsCount));

        if (enableBackgroundTitleUpdates)
        {
            _ = UpdateAddonTitlesInBackgroundAsync();
        }

        QueueMetadataSupplement();
    }

    private sealed class PreparedAddonInventory
    {
        public PreparedAddonInventory(
            IReadOnlyList<WorkshopAddon> addons,
            HashSet<string> subscribedAddonIds)
        {
            Addons = addons;
            SubscribedAddonIds = subscribedAddonIds;
        }

        public IReadOnlyList<WorkshopAddon> Addons { get; }

        public HashSet<string> SubscribedAddonIds { get; }
    }

    public void ApplyFilter()
    {
        try
        {
            CancelBackgroundPreload();

            var query = AllAddons.AsEnumerable();
            RefreshRuntimeStates();

            foreach (var addon in AllAddons)
            {
                addon.SetCurrentAsset(CurrentAsset);
            }

            // State filter: keep border semantics separate from inactive asset membership badges.
            query = query.Where(addon => MatchesAddonStateFilter(addon, addonFilterIndex));

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
                    IReadOnlyCollection<string> assetAddonIds =
                        CurrentAsset.Id == "subscribe-system-asset"
                            ? Array.Empty<string>()
                            : CurrentAsset.GetAddonIds();
                    query = query.Where(addon => MatchesAssetMembership(
                        CurrentAsset.Id,
                        assetAddonIds,
                        addon.AddonId,
                        currentSubscribedAddonIds));
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

            var viewModelsById = results.ToDictionary(
                addon => addon.AddonId,
                StringComparer.Ordinal);
            results = addonSortService
                .Sort(results.Select(addon => addon.SortSource), CurrentSortOptions)
                .Select(addon => viewModelsById[addon.Id])
                .ToList();
            
            // 新しいコレクションを作成してから一度に置き換える
            var newFilteredAddons = new ObservableCollection<AddonItemViewModel>();
            foreach (var addon in results)
            {
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

    private static bool MatchesAddonStateFilter(AddonItemViewModel addon, int filterIndex)
    {
        return filterIndex switch
        {
            1 => addon.ActualEnabled == true,
            2 => addon.ActualEnabled == false,
            _ => true
        };
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
            var assetListVm = ViewModelLocator.AssetListViewModel;
            if (assetListVm == null)
            {
                return;
            }

            var targetAssets = assetListVm.Assets
                .Where(asset => !asset.IsSystem)
                .OrderBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var newlyCreatedAssetIds = new HashSet<string>(StringComparer.Ordinal);
            var dialog = new AssetSelectionDialog(
                targetAssets,
                async name =>
                {
                    var created = await CreateAssetFromSelectionAsync(name);
                    if (created != null)
                    {
                        newlyCreatedAssetIds.Add(created.Id);
                    }
                    return created;
                });
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                return;
            }

            var selectedAsset = await dialog.ShowDialog<AssetItemViewModel?>(mainWindow);
            if (selectedAsset == null)
            {
                return;
            }

            var existingIds = new HashSet<string>(
                selectedAsset.GetAddonIds(),
                StringComparer.Ordinal);
            var newAddons = selectedAddons
                .Where(addon => !existingIds.Contains(addon.AddonId))
                .ToList();
            if (newAddons.Count == 0)
            {
                var duplicateMessage = selectedAddons.Count == 1
                    ? L.Get("AddonGrid.DuplicateSingle")
                    : L.Format("AddonGrid.DuplicateMultiple", selectedAddons.Count);
                await dialogService.ShowInfoAsync(L.Get("Info.Title"), duplicateMessage);
                return;
            }

            using var progressDialog = ProgressDialogService.Show(
                mainWindow,
                L.Get("Busy.AddingAddonsToAsset"),
                L.Format("Busy.Detail.AssetNameWithCount", selectedAsset.Name, newAddons.Count));
            var newAddonIds = newAddons.Select(addon => addon.AddonId).ToList();
            if (newlyCreatedAssetIds.Contains(selectedAsset.Id))
            {
                addonManager.AddAddonsToNewAssetBatch(
                    selectedAsset.Id,
                    newAddonIds,
                    progress: progressDialog?.CreateProgress());
            }
            else
            {
                addonManager.AddAddonsToAssetBatch(
                    selectedAsset.Id,
                    newAddonIds,
                    progress: progressDialog?.CreateProgress());
            }

            selectedAsset.RefreshFromModel(
                addonManager.GetConfiguration().Assets.First(asset => asset.Id == selectedAsset.Id));

            progressDialog?.Close();
            var successMessage = newAddons.Count == 1
                ? L.Format("Success.AddedToAssetSingle", selectedAsset.Name)
                : L.Format("Success.AddedToAssetMultiple", newAddons.Count, selectedAsset.Name);
            await ShowTransferSuccessDialogAsync(L.Get("Success.Title"), successMessage, selectedAsset);
            IsSelectionMode = false;
            await ReloadAddons(rescanWorkshop: false);
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

        var newAsset = await addonManager.CreateAssetAsync(trimmedName);

        return new AssetItemViewModel(
            newAsset,
            addonManager,
            pendingChangeManager,
            processWatcher);
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
            return assetListVm.Assets.FirstOrDefault(a => a.Id == assetId);
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
    private void RefreshRuntimeStates()
    {
        var resolvedStates = addonManager.GetResolvedAddonStates();
        var actualStates = addonManager.CaptureState().States;
        var hasQueuedRuntimeApply = pendingChangeManager.HasPendingChanges();

        foreach (var addon in AllAddons)
        {
            if (!resolvedStates.TryGetValue(addon.AddonId, out var resolved))
            {
                resolved = new ResolvedAddonState(
                    addon.AddonId,
                    isSubscribed: false,
                    desiredEnabled: false,
                    enabledBySubscribe: false,
                    AddonStateResolutionReason.NotSubscribed,
                    Array.Empty<ResolvedAddonStateSource>(),
                    Array.Empty<ResolvedAddonStateSource>());
            }

            var actualEnabled = false;
            var hasActualState =
                resolved.IsRuntimeTarget &&
                actualStates.TryGetValue(addon.AddonId, out actualEnabled);
            addon.RefreshRuntimeState(
                resolved,
                hasActualState ? actualEnabled : null,
                hasQueuedRuntimeApply);
        }
    }

    private static bool ShouldAddRetainedMissingAddon(
        bool retainMissingReferences,
        bool subscriptionBaselineInitialized,
        WorkshopAddon metadata,
        bool isSubscribed)
    {
        return retainMissingReferences &&
               subscriptionBaselineInitialized &&
               !isSubscribed &&
               !metadata.IsLocal &&
               !metadata.IsAvailable &&
               !metadata.IsDownloadPending;
    }

    private static string FormatSubscriptionCountDisplay(
        int visibleCount,
        int availableCount,
        int subscribedCount,
        bool japanese)
    {
        if (visibleCount == availableCount)
        {
            return japanese
                ? $"(利用可能 {availableCount} / 購読中 {subscribedCount})"
                : $"(Available {availableCount} / Subscribed {subscribedCount})";
        }

        return japanese
            ? $"(表示 {visibleCount} / 利用可能 {availableCount} / 購読中 {subscribedCount})"
            : $"(Showing {visibleCount} / Available {availableCount} / Subscribed {subscribedCount})";
    }

    private static bool MatchesAssetMembership(
        string assetId,
        IReadOnlyCollection<string> assetAddonIds,
        string addonId,
        IReadOnlySet<string> subscribedAddonIds)
    {
        if (assetId == "subscribe-system-asset")
        {
            return subscribedAddonIds.Contains(addonId);
        }

        return assetAddonIds.Contains("*") || assetAddonIds.Contains(addonId);
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
        processWatcher.GmodStarted -= OnGmodRuntimeStateChanged;
        processWatcher.GmodStopped -= OnGmodRuntimeStateChanged;

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


