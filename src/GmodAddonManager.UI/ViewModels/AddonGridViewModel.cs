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
    private int sortRefreshQueued;
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

        // Text entry is the only filter source that benefits from debounce.
        // Asset navigation and explicit filter controls must update atomically
        // with the surrounding UI instead of leaving the previous grid visible.
        filterSubscription = this.WhenAnyValue(x => x.FilterText)
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
        AddFilterOptions(
            addonTypeFilters,
            AddonClassificationService.SupportedTypes.Select(
                value => (Key: value, LabelKey: "AddonType." + value)));

        AddFilterOptions(
            addonTagFilters,
            AddonClassificationService.SupportedTags.Select(
                value => (Key: value, LabelKey: "AddonTag." + value)));

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

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(AssetItemViewModel.IsEnabledState) ||
            e.PropertyName == nameof(AssetItemViewModel.IsDisabledState) ||
            e.PropertyName == nameof(AssetItemViewModel.IsExcludedState))
        {
            foreach (var addon in AllAddons)
            {
                addon.SetCurrentAsset(CurrentAsset);
            }
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

    private void OnAddonSortSourceChanged(object? sender, EventArgs e)
    {
        if (disposed || Interlocked.Exchange(ref sortRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                Interlocked.Exchange(ref sortRefreshQueued, 0);
                if (!disposed)
                {
                    ApplyFilter();
                }
            },
            DispatcherPriority.Background);
    }

    private AddonSortOptions CurrentSortOptions => new()
    {
        Mode = (AddonSortMode)selectedSortModeIndex,
        Direction = sortDirection
    };

    private void RefreshSortModeOptions()
    {
        var labels = new[]
        {
            L.Get("AddonGrid.SortMode.SubscriptionTime"),
            L.Get("AddonGrid.SortMode.Name"),
            L.Get("AddonGrid.SortMode.Size"),
            L.Get("AddonGrid.SortMode.WorkshopUpdated")
        };

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
                await SupplementWorkshopMetadataAsync(cts.Token);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementWorkshopMetadataAsync", ex);
            }
        });
    }

    private async Task SupplementWorkshopMetadataAsync(CancellationToken token)
    {
        List<MetadataSupplementTarget> targets;
        try
        {
            targets = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var config = addonManager.GetConfiguration();
                var viewModelsById = AllAddons.ToDictionary(
                    addon => addon.AddonId,
                    StringComparer.Ordinal);
                var targetIds = new HashSet<string>(
                    AllAddons
                        .Where(NeedsMetadataSupplement)
                        .Select(addon => addon.AddonId),
                    StringComparer.Ordinal);

                // A subscribed item with an empty/invalid local payload is not
                // represented by an AddonItemViewModel. Its persisted Workshop
                // metadata still needs repair even though it must stay out of the
                // visible inventory until Steam finishes the payload.
                foreach (var metadata in config.AddonMetadata.Values)
                {
                    if (currentSubscribedAddonIds.Contains(metadata.Id) &&
                        IsWorkshopMetadata(metadata) &&
                        WorkshopMetadataMergeService.NeedsSupplement(metadata))
                    {
                        targetIds.Add(metadata.Id);
                    }
                }

                return targetIds
                    .Where(config.AddonMetadata.ContainsKey)
                    .Select(addonId => new MetadataSupplementTarget(
                        addonId,
                        config.AddonMetadata[addonId],
                        viewModelsById.TryGetValue(addonId, out var viewModel)
                            ? viewModel
                            : null))
                    .ToList();
            });
        }
        catch (Exception ex)
        {
            if (!metadataSupplementUiSnapshotErrorLogged)
            {
                metadataSupplementUiSnapshotErrorLogged = true;
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementWorkshopMetadataAsync.UIThreadSnapshot", ex);
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
                SafeFileLogger.TryLogException("AddonGridViewModel.SupplementWorkshopMetadataAsync.CacheRead", ex);
            }

            cacheDetails = new Dictionary<string, WorkshopItemInfo>(StringComparer.Ordinal);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var seeds = new Dictionary<string, MetadataSupplementSeed>(StringComparer.Ordinal);
        var missingTagIds = new List<string>();

        foreach (var target in targets)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var metadata = target.Metadata;
            cacheDetails.TryGetValue(target.AddonId, out var info);

            var seed = new MetadataSupplementSeed(target.AddonId);
            string[]? tagsToApply = null;
            string? typeToApply = null;

            if (!HasTagValues(metadata.Tags) || string.IsNullOrWhiteSpace(metadata.Type))
            {
                if (TryReadAddonJsonMetadata(target.ViewModel, out var jsonType, out var jsonTags))
                {
                    if (!HasTagValues(metadata.Tags) && jsonTags != null && jsonTags.Length > 0)
                    {
                        tagsToApply = NormalizeTags(jsonTags);
                    }

                    if (string.IsNullOrWhiteSpace(metadata.Type) && !string.IsNullOrWhiteSpace(jsonType))
                    {
                        typeToApply = jsonType;
                    }
                }
            }

            if (!HasTagValues(metadata.Tags) && tagsToApply == null && info != null)
            {
                tagsToApply = ParseNormalizedTags(info.Tags);
            }

            seed.Tags = tagsToApply;
            seed.Type = typeToApply;
            seeds[target.AddonId] = seed;

            if (!HasTagValues(metadata.Tags) && tagsToApply == null)
            {
                missingTagIds.Add(target.AddonId);
            }
        }

        var targetIds = targets
            .Select(target => target.AddonId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var detailsMap = new Dictionary<string, WorkshopItemDetails>(StringComparer.Ordinal);
        if (!token.IsCancellationRequested)
        {
            try
            {
                var workshopService = addonManager.GetSteamWorkshopService();
                detailsMap = await workshopService.GetWorkshopDetailsBatchAsync(
                    targetIds,
                    token,
                    treatAsHot: false,
                    requireTags: false);

                var tagsStillMissing = missingTagIds
                    .Where(addonId =>
                        !detailsMap.TryGetValue(addonId, out var details) ||
                        !HasTagValues(details.Tags))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (tagsStillMissing.Count > 0 && !token.IsCancellationRequested)
                {
                    var tagDetails = await workshopService.GetWorkshopDetailsBatchAsync(
                        tagsStillMissing,
                        token,
                        treatAsHot: false,
                        requireTags: true);
                    foreach (var kvp in tagDetails)
                    {
                        detailsMap[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!metadataSupplementWebErrorLogged)
                {
                    metadataSupplementWebErrorLogged = true;
                    SafeFileLogger.TryLogException("AddonGridViewModel.SupplementWorkshopMetadataAsync.WebFetch", ex);
                }
            }
        }

        var updates = new List<MetadataSupplementUpdate>();
        foreach (var target in targets)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!seeds.TryGetValue(target.AddonId, out var seed))
            {
                continue;
            }

            var tagsToApply = seed.Tags;
            detailsMap.TryGetValue(target.AddonId, out var details);
            if (tagsToApply == null && details != null)
            {
                tagsToApply = NormalizeTags(details.Tags);
            }

            var typeToApply = seed.Type;
            if (string.IsNullOrWhiteSpace(target.Metadata.Type) && string.IsNullOrWhiteSpace(typeToApply))
            {
                typeToApply = AddonClassificationService.InferTypeFromTags(
                    tagsToApply ?? target.Metadata.Tags?.ToArray());
            }

            if (details == null &&
                tagsToApply == null &&
                string.IsNullOrWhiteSpace(typeToApply))
            {
                continue;
            }

            updates.Add(new MetadataSupplementUpdate(
                target.AddonId,
                details,
                tagsToApply,
                typeToApply));
        }

        if (updates.Count == 0 || token.IsCancellationRequested)
        {
            return;
        }

        var configUpdated = false;
        var classificationUpdated = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var config = addonManager.GetConfiguration();
            var addonsById = AllAddons.ToDictionary(a => a.AddonId, StringComparer.Ordinal);
            var metadataMerger = new WorkshopMetadataMergeService();

            foreach (var update in updates)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (!config.AddonMetadata.TryGetValue(update.AddonId, out var metadata))
                {
                    continue;
                }

                var changes = metadataMerger.Merge(
                    metadata,
                    update.Details,
                    update.Tags,
                    update.Type);
                if (changes == WorkshopMetadataMergeChanges.None)
                {
                    continue;
                }

                config.AddonMetadata[update.AddonId] = metadata;
                configUpdated = true;
                classificationUpdated |=
                    changes.HasFlag(WorkshopMetadataMergeChanges.Tags) ||
                    changes.HasFlag(WorkshopMetadataMergeChanges.Type);

                if (addonsById.TryGetValue(update.AddonId, out var addonVm))
                {
                    if (changes.HasFlag(WorkshopMetadataMergeChanges.Title))
                    {
                        addonVm.UpdateTitle(metadata.Title);
                    }

                    var applyTags = changes.HasFlag(WorkshopMetadataMergeChanges.Tags)
                        ? metadata.Tags
                        : null;
                    var applyType = changes.HasFlag(WorkshopMetadataMergeChanges.Type)
                        ? metadata.Type
                        : null;
                    if (applyTags != null || !string.IsNullOrWhiteSpace(applyType))
                    {
                        addonVm.UpdateTagsAndType(applyTags, applyType);
                    }
                }
            }
        });

        if (configUpdated && !token.IsCancellationRequested)
        {
            try
            {
                await addonManager.SaveConfigurationAsync();
                if (classificationUpdated)
                {
                    await addonManager.ReconcileSmartAssetsAsync();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ViewModelLocator.AssetListViewModel?.RefreshAssetStates();
                    });
                }
            }
            catch (Exception ex)
            {
                if (!metadataSupplementSaveErrorLogged)
                {
                    metadataSupplementSaveErrorLogged = true;
                    SafeFileLogger.TryLogException("AddonGridViewModel.SupplementWorkshopMetadataAsync.SaveConfiguration", ex);
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

        return WorkshopMetadataMergeService.NeedsSupplement(addon.SortSource);
    }

    private static bool IsWorkshopMetadata(WorkshopAddon metadata)
    {
        return metadata != null &&
               !metadata.IsLocal &&
               ulong.TryParse(metadata.Id, out var workshopId) &&
               workshopId > 0;
    }

    private static bool HasTagValues(IEnumerable<string>? tags)
    {
        return tags != null && tags.Any(tag => !string.IsNullOrWhiteSpace(tag));
    }

    private static string[]? NormalizeTags(IEnumerable<string>? tags)
    {
        var normalized = AddonClassificationService.NormalizeTags(tags);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string[]? ParseNormalizedTags(string? tagsValue)
    {
        if (string.IsNullOrWhiteSpace(tagsValue))
        {
            return null;
        }

        return NormalizeTags(new[] { tagsValue });
    }

    private static bool TryReadAddonJsonMetadata(AddonItemViewModel? addon, out string? type, out string[]? tags)
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
        public MetadataSupplementUpdate(
            string addonId,
            WorkshopItemDetails? details,
            string[]? tags,
            string? type)
        {
            AddonId = addonId;
            Details = details;
            Tags = tags;
            Type = type;
        }

        public string AddonId { get; }
        public WorkshopItemDetails? Details { get; }
        public string[]? Tags { get; }
        public string? Type { get; }
    }

    private sealed class MetadataSupplementTarget
    {
        public MetadataSupplementTarget(
            string addonId,
            WorkshopAddon metadata,
            AddonItemViewModel? viewModel)
        {
            AddonId = addonId;
            Metadata = metadata;
            ViewModel = viewModel;
        }

        public string AddonId { get; }
        public WorkshopAddon Metadata { get; }
        public AddonItemViewModel? ViewModel { get; }
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
            return sortDirection == AddonSortDirection.Ascending
                ? L.Get("AddonGrid.SortDirection.Ascending")
                : L.Get("AddonGrid.SortDirection.Descending");
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
        set
        {
            if (showOnlyAssetAddons == value)
            {
                return;
            }

            SetAndRaise(ref showOnlyAssetAddons, value);
            ApplyFilter();
        }
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
                var visibleSubscribedCount = FilteredAddons.Count(addon =>
                    !addon.IsLocal &&
                    currentSubscribedAddonIds.Contains(addon.AddonId));
                var availableCount = AllAddons.Count(addon =>
                    !addon.IsLocal &&
                    addon.IsAvailable &&
                    currentSubscribedAddonIds.Contains(addon.AddonId));
                var visibleLocalCount = FilteredAddons.Count(addon => addon.IsLocal);
                return FormatSubscriptionCountDisplay(
                    visibleSubscribedCount,
                    availableCount,
                    currentSubscribedAddonIds.Count,
                    visibleLocalCount,
                    LocalizationManager.Instance.CurrentLanguage.StartsWith(
                        "ja",
                        StringComparison.OrdinalIgnoreCase));
            }

            if (CurrentAsset?.IsGmodDisabledAsset == true && ShowOnlyAssetAddons)
            {
                return FormatFixedMembershipCountDisplay(
                    FilteredAddonsCount,
                    CurrentAsset.AddonCount);
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
                                      !currentAsset.IsSystem &&
                                      !currentAsset.IsSmart;
    
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
                ApplyFilter();
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
            .Where(addon => !addon.IsDownloadPending)
            .ToList();

        var config = addonManager.GetConfiguration();
        var subscribedAddonIds = new HashSet<string>(
            addonManager.GetResolvedAddonStates().Keys,
            StringComparer.Ordinal);
        AddRetainedMissingAddons(
            addonList,
            config,
            subscribedAddonIds,
            addonManager.IsLocalAddonId,
            cancellationToken);

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
                var newViewModel = new AddonItemViewModel(addon, addonManager, null);
                newViewModel.SortSourceChanged += OnAddonSortSourceChanged;
                newAllAddons.Add(newViewModel);
            }
        }

        foreach (var kvp in existingViewModels)
        {
            if (!reusedAddonIds.Contains(kvp.Key))
            {
                kvp.Value.SortSourceChanged -= OnAddonSortSourceChanged;
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
                        currentSubscribedAddonIds,
                        addon.IsLocal));
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
            var sortOptions = CurrentSortOptions;
            foreach (var addon in results)
            {
                addon.SetSortPresentationMode(sortOptions.Mode);
            }
            results = addonSortService
                .Sort(results.Select(addon => addon.SortSource), sortOptions)
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
            SafeFileLogger.TryLogException("AddonGridViewModel.ApplyFilter", ex);
        }
    }

    private static HashSet<string> GetSelectedFilterKeys(IEnumerable<FilterOptionViewModel> options)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (option != null && option.IsSelected)
            {
                if (!string.IsNullOrWhiteSpace(option.Key))
                {
                    selected.Add(option.Key);
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

        if (selectedTypeFilters.Count > 0 &&
            !selectedTypeFilters.Any(value =>
                AddonClassificationService.Evaluate(
                    addon.SortSource,
                    new AssetMembershipRule(AssetMembershipRuleKind.Type, value)) ==
                AddonClassificationMatch.Match))
        {
            return false;
        }

        if (selectedAddonTags.Count > 0 &&
            !selectedAddonTags.Any(value =>
                AddonClassificationService.Evaluate(
                    addon.SortSource,
                    new AssetMembershipRule(AssetMembershipRuleKind.Tag, value)) ==
                AddonClassificationMatch.Match))
        {
            return false;
        }

        return true;
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
        var nextShowOnlyAssetAddons = asset != null;
        if (showOnlyAssetAddons != nextShowOnlyAssetAddons)
        {
            showOnlyAssetAddons = nextShowOnlyAssetAddons;
            this.RaisePropertyChanged(nameof(ShowOnlyAssetAddons));
        }

        // Keep Asset-list navigation and the Addon grid in one UI transaction.
        // Search text remains debounced independently above.
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
        if (addon != null && !addon.IsLocal)
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
            FilteredAddons.Where(a => a.IsSelected && !a.IsLocal)
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
                await addon.LoadThumbnailAsync(allowRemote, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
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
            var selectedAddons = new ObservableCollection<AddonItemViewModel>(
                GetSelectedAddons().Where(addon => !addon.IsLocal));
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
                .Where(asset => !asset.IsSmart)
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
            SafeFileLogger.TryLogException(
                "AddonGridViewModel.ShowAssetSelectionDialogAsync",
                ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.AssetSelectionDialogFailed", ex.Message));
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
                if (!addon.IsLocal && !addon.IsSelected)
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
            if (currentAsset.IsSmart) return;
            
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
            SafeFileLogger.TryLogException(
                "AddonGridViewModel.RemoveSelectedAddonsFromAssetAsync",
                ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("Error.RemoveAddonFailed", ex.Message));
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

    private static void AddRetainedMissingAddons(
        IList<WorkshopAddon> addonList,
        Configuration config,
        IReadOnlySet<string> subscribedAddonIds,
        Func<string, bool> isLocalAddonId,
        CancellationToken cancellationToken)
    {
        var loadedAddonIds = new HashSet<string>(
            addonList.Select(addon => addon.Id),
            StringComparer.Ordinal);

        // Retention is authorized by either the profile-wide setting or the
        // owning Asset. The per-Asset authority is required for imported fixed
        // Assets, which deliberately preserve unavailable Workshop references.
        var retainedAddonIds = config.Assets
            .Where(asset =>
                !asset.IsSystem &&
                (config.RetainMissingAssetReferences || asset.RetainMissingReferences))
            .SelectMany(asset => asset.Addons)
            .Where(addonId => addonId != "*")
            .Distinct(StringComparer.Ordinal);

        foreach (var addonId in retainedAddonIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loadedAddonIds.Contains(addonId) ||
                isLocalAddonId(addonId) ||
                !config.AddonMetadata.TryGetValue(addonId, out var metadata) ||
                !ShouldAddRetainedMissingAddon(
                    retainMissingReferences: true,
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
            loadedAddonIds.Add(addonId);
        }
    }

    private static string FormatSubscriptionCountDisplay(
        int visibleCount,
        int availableCount,
        int subscribedCount,
        int visibleLocalCount,
        bool japanese)
    {
        var localSuffix = visibleLocalCount > 0
            ? japanese ? $" / ローカル {visibleLocalCount}" : $" / Local {visibleLocalCount}"
            : string.Empty;

        if (visibleCount == availableCount)
        {
            return japanese
                ? $"(利用可能 {availableCount} / 購読中 {subscribedCount}{localSuffix})"
                : $"(Available {availableCount} / Subscribed {subscribedCount}{localSuffix})";
        }

        return japanese
            ? $"(表示 {visibleCount} / 利用可能 {availableCount} / 購読中 {subscribedCount}{localSuffix})"
            : $"(Showing {visibleCount} / Available {availableCount} / Subscribed {subscribedCount}{localSuffix})";
    }

    private static string FormatFixedMembershipCountDisplay(
        int visibleCount,
        int membershipCount)
    {
        return visibleCount == membershipCount
            ? $"({membershipCount})"
            : $"({visibleCount}/{membershipCount})";
    }

    private static bool MatchesAssetMembership(
        string assetId,
        IReadOnlyCollection<string> assetAddonIds,
        string addonId,
        IReadOnlySet<string> subscribedAddonIds,
        bool isLocal)
    {
        // Experimental local addons are shown beside the initial Subscribe
        // inventory as read-only GMod-owned entries. They are not members of
        // Subscribe Asset (or any mutable Custom Asset).
        if (isLocal)
        {
            return assetId == "subscribe-system-asset";
        }

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
            addon.SortSourceChanged -= OnAddonSortSourceChanged;
            addon.Dispose();
        }

        allAddons.Clear();
        filteredAddons.Clear();
        visibleLoadSemaphore.Dispose();
    }
    
}


