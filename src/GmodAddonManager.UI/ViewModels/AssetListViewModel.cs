using Avalonia.Platform.Storage;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Models;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AssetListViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly PendingChangeManager pendingChangeManager;
    private readonly GmodProcessWatcher processWatcher;
    private readonly IDialogService dialogService;
    private readonly Action<bool> saveGmodDisabledCollapsePreference;
    private IDisposable? selectedAssetSubscription;
    private bool disposed;
    private ObservableCollection<AssetItemViewModel> assets;
    private ObservableCollection<AssetListEntryViewModel> entries;
    private AssetItemViewModel? selectedAsset;
    private string? currentGroupId;
    private bool isGmodDisabledCollapsed;
    private readonly HashSet<string> sharedAssetIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> sharedGroupIds = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ShareSelectionItemViewModel> shareSelectionItems = new();
    private readonly ObservableCollection<AssetBreadcrumbItemViewModel> breadcrumbs = new();
    private bool isShareMode;
    private bool includeImagesInShare;
    private bool includeMemosInShare;
    private bool isShareExporting;
    private string shareErrorText = string.Empty;

    public AssetListViewModel(
        AddonManager addonManager,
        PendingChangeManager pendingChangeManager,
        GmodProcessWatcher processWatcher,
        AppSettings? initialSettings = null,
        Action<bool>? saveGmodDisabledCollapsePreference = null)
    {
        this.addonManager = addonManager;
        this.pendingChangeManager = pendingChangeManager;
        this.processWatcher = processWatcher;
        this.saveGmodDisabledCollapsePreference =
            saveGmodDisabledCollapsePreference ?? SaveGmodDisabledCollapsePreference;
        dialogService = new DialogService();
        assets = new ObservableCollection<AssetItemViewModel>();
        entries = new ObservableCollection<AssetListEntryViewModel>();

        try
        {
            isGmodDisabledCollapsed = (initialSettings ?? AppSettings.Load())
                .CollapseGmodDisabledAddons;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.LoadCollapsePreference", ex);
            isGmodDisabledCollapsed = false;
        }

        CreateAssetCommand = ReactiveCommand.CreateFromTask(CreateAssetAsync);
        ImportGamAssetCommand = ReactiveCommand.CreateFromTask(ImportGamAssetAsync);
        RefreshCommand = ReactiveCommand.Create(LoadAssets);
        BackCommand = ReactiveCommand.Create(ReturnToParent);
        BackToRootCommand = ReactiveCommand.Create(ReturnToRoot);
        ToggleGmodDisabledCollapseCommand = ReactiveCommand.Create(ToggleGmodDisabledCollapse);
        ExportShareSelectionCommand = ReactiveCommand.CreateFromTask(ExportShareSelectionAsync);
        CancelShareModeCommand = ReactiveCommand.Create(CancelShareMode);

        selectedAssetSubscription = this.WhenAnyValue(x => x.SelectedAsset)
            .Subscribe(asset =>
            {
                foreach (var candidate in Assets)
                {
                    candidate.IsSelected = false;
                    candidate.IsCurrent = false;
                }
                foreach (var entry in Entries.Where(entry => entry.IsGroup))
                {
                    entry.IsSelected = false;
                }

                if (asset != null)
                {
                    asset.IsSelected = true;
                    asset.IsCurrent = true;
                }
            });

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>
    /// All leaf Asset cards, including children hidden inside Groups. This
    /// collection remains compatible with MainWindow and Addon-grid callers.
    /// </summary>
    public ObservableCollection<AssetItemViewModel> Assets
    {
        get => assets;
        private set => SetAndRaise(ref assets, value);
    }

    /// <summary>The mixed entries visible in the current root/Group container.</summary>
    public ObservableCollection<AssetListEntryViewModel> Entries
    {
        get => entries;
        private set => SetAndRaise(ref entries, value);
    }

    public AssetItemViewModel? SelectedAsset
    {
        get => selectedAsset;
        set => SetAndRaise(ref selectedAsset, value);
    }

    public string? CurrentGroupId => currentGroupId;
    public bool IsAtRoot => string.IsNullOrWhiteSpace(currentGroupId);
    public bool IsInsideGroup => !IsAtRoot;
    public string CurrentHeader => IsAtRoot
        ? L.Get("AssetList.Header")
        : ResolveCurrentGroup()?.Name ?? L.Get("AssetGroup.Badge");
    public string CreateActionText => L.Get("AssetList.CreateNew");
    public string CreateActionTooltip => L.Get("AssetList.CreateNewTooltip");
    public bool IsCurrentGroupEmpty => IsInsideGroup && Entries.Count == 0;
    public bool HasDirectAssetInCurrentGroup =>
        IsAtRoot || Entries.Any(entry => entry.IsAsset);
    public bool IsCurrentGroupEmptyVisible =>
        !IsShareMode && IsInsideGroup && !HasDirectAssetInCurrentGroup;
    public string CurrentGroupEmptyText => IsCurrentGroupEmpty
        ? L.Get("AssetGroup.EmptyState")
        : L.Get("AssetGroup.NoDirectAssetsState");
    public bool IsGmodDisabledCollapsed
    {
        get => isGmodDisabledCollapsed;
        private set
        {
            SetAndRaise(ref isGmodDisabledCollapsed, value);
            this.RaisePropertyChanged(nameof(IsGmodDisabledExpanded));
            this.RaisePropertyChanged(nameof(GmodDisabledCollapseTooltip));
        }
    }
    public bool IsGmodDisabledExpanded => !IsGmodDisabledCollapsed;
    public string GmodDisabledCollapseTooltip => IsGmodDisabledCollapsed
        ? L.Get("AssetList.ShowGmodDisabled")
        : L.Get("AssetList.HideGmodDisabled");
    public bool IsShareMode
    {
        get => isShareMode;
        private set
        {
            SetAndRaise(ref isShareMode, value);
            this.RaisePropertyChanged(nameof(IsAddonGridVisible));
            this.RaisePropertyChanged(nameof(IsCurrentGroupEmptyVisible));
            this.RaisePropertyChanged(nameof(IsAssetMutationEnabled));
        }
    }
    public bool IsAddonGridVisible => !IsShareMode && HasDirectAssetInCurrentGroup;
    public bool IsAssetMutationEnabled => !IsShareMode;
    public bool IncludeImagesInShare
    {
        get => includeImagesInShare;
        set => SetAndRaise(ref includeImagesInShare, value);
    }
    public bool IncludeMemosInShare
    {
        get => includeMemosInShare;
        set => SetAndRaise(ref includeMemosInShare, value);
    }
    public bool IsShareExporting
    {
        get => isShareExporting;
        private set
        {
            SetAndRaise(ref isShareExporting, value);
            this.RaisePropertyChanged(nameof(CanExportShareSelection));
        }
    }
    public int SharedAssetCount => sharedAssetIds.Count;
    public int SharedGroupCount => sharedGroupIds.Count;
    public bool HasShareSelection => SharedAssetCount > 0 || SharedGroupCount > 0;
    public bool CanExportShareSelection => HasShareSelection && !IsShareExporting;
    public string ShareSelectionSummary => L.Format(
        "GamShare.SelectionSummary",
        SharedAssetCount,
        L.Get(SharedAssetCount == 1
            ? "GamShare.AssetSingular"
            : "GamShare.AssetPlural"),
        SharedGroupCount,
        L.Get(SharedGroupCount == 1
            ? "GamShare.GroupSingular"
            : "GamShare.GroupPlural"));
    public string ShareWorkspaceTitle => L.Get("GamShare.Title");
    public string ShareWorkspaceDescription => L.Get("GamShare.Description");
    public string ShareErrorText
    {
        get => shareErrorText;
        private set
        {
            SetAndRaise(ref shareErrorText, value);
            this.RaisePropertyChanged(nameof(HasShareError));
        }
    }
    public bool HasShareError => !string.IsNullOrWhiteSpace(ShareErrorText);
    public ObservableCollection<ShareSelectionItemViewModel> ShareSelectionItems =>
        shareSelectionItems;
    public ObservableCollection<AssetBreadcrumbItemViewModel> Breadcrumbs => breadcrumbs;

    public ReactiveCommand<Unit, Unit> CreateAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportGamAssetCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToRootCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleGmodDisabledCollapseCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportShareSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelShareModeCommand { get; }

    public void LoadAssets()
    {
        try
        {
            var previousSelectedId = SelectedAsset?.Id;
            ClearEntries();
            ClearAssets();

            var configuration = addonManager.GetConfiguration();
            PruneShareSelection(configuration);
            if (currentGroupId != null &&
                configuration.AssetGroups.All(group =>
                    !string.Equals(group.Id, currentGroupId, StringComparison.Ordinal)))
            {
                currentGroupId = null;
            }

            BuildBreadcrumbs(configuration);

            foreach (var asset in OrderAllAssets(configuration.Assets))
            {
                Assets.Add(new AssetItemViewModel(
                    asset,
                    addonManager,
                    pendingChangeManager,
                    processWatcher));
            }

            BuildVisibleEntries(configuration);
            ApplySharePresentation(configuration);
            RestoreSelection(configuration, previousSelectedId);
            RaiseNavigationProperties();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.LoadAssets", ex);
        }
    }

    public void OpenGroup(AssetListEntryViewModel entry)
    {
        if (entry?.Group == null)
        {
            return;
        }
        OpenGroup(entry.Id);
    }

    public void OpenGroup(string groupId)
    {
        NavigateToGroup(groupId);
    }

    public void NavigateToGroup(string? groupId)
    {
        var configuration = addonManager.GetConfiguration();
        if (!string.IsNullOrWhiteSpace(groupId) &&
            configuration.AssetGroups.All(group =>
                !string.Equals(group.Id, groupId, StringComparison.Ordinal)))
        {
            return;
        }

        currentGroupId = string.IsNullOrWhiteSpace(groupId) ? null : groupId;
        SelectedAsset = null;
        LoadAssets();
    }

    public void ReturnToRoot()
    {
        if (IsAtRoot)
        {
            return;
        }

        NavigateToGroup(groupId: null);
    }

    public void ReturnToParent()
    {
        if (IsAtRoot)
        {
            return;
        }

        NavigateToGroup(ResolveCurrentGroup()?.ParentGroupId);
    }

    public IReadOnlyList<AssetListEntryViewModel> GetReorderableEntries()
    {
        return Entries.Where(entry => entry.CanReorder).ToList();
    }

    public int GetClampedReorderTargetIndex(
        AssetListEntryViewModel moving,
        int requestedTargetIndex)
    {
        var reorderable = GetReorderableEntries();
        var currentIndex = IndexOfEntry(reorderable, moving);
        if (currentIndex < 0)
        {
            return -1;
        }

        var sameBand = reorderable
            .Select((entry, index) => new { entry, index })
            .Where(item => item.entry.IsFavorite == moving.IsFavorite)
            .Select(item => item.index)
            .ToList();
        if (sameBand.Count == 0)
        {
            return currentIndex;
        }

        return Math.Clamp(requestedTargetIndex, sameBand[0], sameBand[^1]);
    }

    public async Task ReorderEntryAsync(
        AssetListEntryViewModel entry,
        int targetIndex)
    {
        if (entry == null || !entry.CanReorder || targetIndex < 0)
        {
            return;
        }

        try
        {
            await addonManager.ReorderAssetListEntryAsync(
                entry.EntryKind,
                entry.Id,
                targetIndex,
                currentGroupId);
            LoadAssets();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ReorderEntry", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("AssetGroup.OperationFailed", ex.Message));
        }
    }

    public void ToggleShareSelection(AssetListEntryViewModel entry)
    {
        if (entry == null || !entry.CanShare)
        {
            return;
        }

        if (!IsShareMode)
        {
            IsShareMode = true;
            IncludeImagesInShare = false;
            IncludeMemosInShare = false;
            ShareErrorText = string.Empty;
        }

        var selectedIds = entry.IsGroup ? sharedGroupIds : sharedAssetIds;
        if (!selectedIds.Add(entry.Id))
        {
            selectedIds.Remove(entry.Id);
        }
        else if (entry.IsGroup)
        {
            if (IsCoveredBySelectedGroup(entry.ParentGroupId))
            {
                selectedIds.Remove(entry.Id);
            }
            else
            {
                RemoveSelectionsCoveredByGroup(entry.Id);
            }
        }
        else if (IsCoveredBySelectedGroup(entry.ParentGroupId))
        {
            selectedIds.Remove(entry.Id);
        }

        ApplySharePresentation(addonManager.GetConfiguration());
    }

    public void CancelShareMode()
    {
        sharedAssetIds.Clear();
        sharedGroupIds.Clear();
        shareSelectionItems.Clear();
        IncludeImagesInShare = false;
        IncludeMemosInShare = false;
        ShareErrorText = string.Empty;
        IsShareMode = false;
        foreach (var entry in Entries)
        {
            entry.SetSharePresentation(shareMode: false, selected: false);
        }
        RaiseShareProperties();
    }

    private async Task ExportShareSelectionAsync()
    {
        if (!CanExportShareSelection)
        {
            return;
        }

        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            return;
        }

        IsShareExporting = true;
        ShareErrorText = string.Empty;
        try
        {
            var file = await mainWindow.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = L.Get("GamAssetFile.SavePickerTitle"),
                    DefaultExtension = "gam",
                    SuggestedFileName = BuildShareSuggestedFileName(),
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new(L.Get("GamAssetFile.FileType"))
                        {
                            Patterns = new[] { "*.gam" }
                        }
                    }
                });
            if (file == null)
            {
                return;
            }

            var path = file.Path.LocalPath;
            if (!string.Equals(Path.GetExtension(path), ".gam", StringComparison.OrdinalIgnoreCase))
            {
                path += ".gam";
            }

            var assetIds = sharedAssetIds.ToArray();
            var groupIds = sharedGroupIds.ToArray();
            await addonManager.ExportGamSelectionAsync(
                assetIds,
                groupIds,
                path,
                IncludeImagesInShare,
                IncludeMemosInShare);

            CancelShareMode();
            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                L.Get("GamShare.ExportCompleted"));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ExportShareSelection", ex);
            ShareErrorText = L.Format("GamShare.ExportFailed", ex.Message);
        }
        finally
        {
            IsShareExporting = false;
        }
    }

    private void BuildVisibleEntries(Configuration configuration)
    {
        if (IsAtRoot)
        {
            AddSystemEntry(SystemAssetDefinitions.SubscribeId);
            if (!IsGmodDisabledCollapsed)
            {
                AddSystemEntry(AssetItemViewModel.GmodDisabledSystemAssetId);
            }

            var rootEntries = configuration.Assets
                .Where(asset => !asset.IsSystem && string.IsNullOrWhiteSpace(asset.ParentGroupId))
                .Select(asset => new VisibleModelEntry(asset))
                .Concat(configuration.AssetGroups
                    .Where(group => string.IsNullOrWhiteSpace(group.ParentGroupId))
                    .Select(group => new VisibleModelEntry(group)))
                .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal);
            foreach (var modelEntry in rootEntries)
            {
                AddVisibleModelEntry(modelEntry);
            }
            return;
        }

        var children = configuration.Assets
            .Where(asset => !asset.IsSystem &&
                            string.Equals(asset.ParentGroupId, currentGroupId, StringComparison.Ordinal))
            .Select(asset => new VisibleModelEntry(asset))
            .Concat(configuration.AssetGroups
                .Where(group => string.Equals(
                    group.ParentGroupId,
                    currentGroupId,
                    StringComparison.Ordinal))
                .Select(group => new VisibleModelEntry(group)))
            .OrderBy(entry => entry.IsFavorite ? 0 : 1)
            .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal);
        foreach (var child in children)
        {
            AddVisibleModelEntry(child);
        }
    }

    private void AddSystemEntry(string id)
    {
        var asset = GetAssetById(id);
        if (asset != null)
        {
            Entries.Add(new AssetListEntryViewModel(asset, parentGroupId: null));
        }
    }

    private void AddVisibleModelEntry(VisibleModelEntry entry)
    {
        if (entry.Asset != null)
        {
            var assetViewModel = GetAssetById(entry.Asset.Id);
            if (assetViewModel != null)
            {
                Entries.Add(new AssetListEntryViewModel(assetViewModel, entry.Asset.ParentGroupId));
            }
            return;
        }

        Entries.Add(new AssetListEntryViewModel(
            new AssetGroupItemViewModel(entry.Group!, addonManager)));
    }

    private void RestoreSelection(Configuration configuration, string? previousSelectedId)
    {
        AssetItemViewModel? selection = null;
        if (!string.IsNullOrWhiteSpace(previousSelectedId))
        {
            selection = Entries
                .Where(entry => entry.IsAsset)
                .Select(entry => entry.Asset)
                .FirstOrDefault(asset => asset?.Id == previousSelectedId);
        }

        if (selection == null && IsInsideGroup)
        {
            selection = Entries.FirstOrDefault(entry => entry.IsAsset)?.Asset;
        }

        if (selection == null && IsAtRoot)
        {
            selection = GetAssetById(SystemAssetDefinitions.SubscribeId) ??
                        Entries.FirstOrDefault(entry => entry.IsAsset)?.Asset;
        }

        SelectedAsset = selection;
    }

    private async Task CreateAssetAsync()
    {
        try
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                return;
            }

            var configuration = addonManager.GetConfiguration();
            var normalizedParentId = currentGroupId;
            var eligibleGroupAssets = configuration.Assets.Where(asset =>
                !asset.IsSystem && string.Equals(
                    asset.ParentGroupId,
                    normalizedParentId,
                    StringComparison.Ordinal));
            var allowAssetGroups = CanCreateGroupInCurrentContainer(configuration);
            var eligibleChildGroups = allowAssetGroups
                ? configuration.AssetGroups.Where(group =>
                    string.Equals(
                        group.ParentGroupId,
                        normalizedParentId,
                        StringComparison.Ordinal) &&
                    CanNestUnderNewGroup(configuration, group.Id))
                : Enumerable.Empty<AssetGroup>();
            var dialog = new SimpleAssetCreateDialog(
                allowSmartAssets: true,
                allowAssetGroups,
                eligibleGroupAssets,
                eligibleChildGroups,
                candidateName => addonManager.AssetNameExists(candidateName)
                    ? L.Format("Error.AssetNameAlreadyExists", candidateName)
                    : null);
            var result = await dialog.ShowDialog<string?>(mainWindow);
            if (string.IsNullOrWhiteSpace(result))
            {
                return;
            }

            var trimmedName = result.Trim();
            if (dialog.SelectedCreationTarget == AssetCreationTarget.AssetGroup)
            {
                var createdGroup = await addonManager.CreateAssetGroupAsync(
                    trimmedName,
                    currentGroupId,
                    dialog.SelectedGroupMemberAssetIds,
                    dialog.SelectedGroupMemberGroupIds);
                currentGroupId = createdGroup.Id;
                SelectedAsset = null;
                LoadAssets();
                return;
            }

            Asset createdAsset;
            if (IsInsideGroup)
            {
                createdAsset = await addonManager.CreateAssetInGroupAsync(
                    trimmedName,
                    currentGroupId!,
                    dialog.SelectedMembershipRule);
            }
            else
            {
                createdAsset = dialog.SelectedMembershipRule == null
                    ? await addonManager.CreateAssetAsync(trimmedName)
                    : await addonManager.CreateSmartAssetAsync(
                        trimmedName,
                        dialog.SelectedMembershipRule);
            }

            var createdAssetId = createdAsset.Id;
            LoadAssets();
            SelectedAsset = Assets.FirstOrDefault(a => a.Id == createdAssetId);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.CreateAsset", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.AssetCreateFailedGeneric"));
        }
    }

    private async Task ImportGamAssetAsync()
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            return;
        }

        try
        {
            var files = await mainWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = L.Get("GamAssetFile.ImportPickerTitle"),
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new(L.Get("GamAssetFile.FileType"))
                        {
                            Patterns = new[] { "*.gam" }
                        }
                    }
                });
            if (files.Count == 0)
            {
                return;
            }

            var preview = await addonManager.PreviewGamFileImportAsync(
                files[0].Path.LocalPath);
            string? requestedName = null;
            if (preview.IsBundle)
            {
                var bundleDialog = new GamBundleImportPreviewDialog(
                    preview,
                    addonManager.GetConfiguration().MaxNestedGroupDepth);
                if (!await bundleDialog.ShowDialog<bool>(mainWindow))
                {
                    return;
                }
            }
            else
            {
                var previewDialog = new GamAssetImportPreviewDialog(
                    preview.SingleAssetPreview!,
                    candidateName => addonManager.AssetNameExists(candidateName)
                        ? L.Format("Error.AssetNameAlreadyExists", candidateName)
                        : null);
                requestedName = await previewDialog.ShowDialog<string?>(mainWindow);
                if (string.IsNullOrWhiteSpace(requestedName))
                {
                    return;
                }
            }

            var imported = await addonManager.ImportGamFileAsync(preview, requestedName);
            currentGroupId = null;
            LoadAssets();
            var firstLooseAsset = imported.Assets.FirstOrDefault(asset =>
                string.IsNullOrWhiteSpace(asset.ParentGroupId));
            if (firstLooseAsset != null)
            {
                SelectedAsset = Assets.FirstOrDefault(asset => asset.Id == firstLooseAsset.Id);
            }

            await dialogService.ShowInfoAsync(
                L.Get("Success.Title"),
                imported.IsBundle
                    ? L.Format(
                        "GamBundleImport.ImportCompleted",
                        imported.Assets.Count,
                        imported.Groups.Count)
                    : L.Format(
                        "GamAssetFile.ImportCompleted",
                        imported.Assets[0].Name));
        }
        catch (GamAssetDocumentException ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ImportGamAsset", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("GamAssetFile.ImportFailedDetail", ex.Message));
        }
        catch (IOException ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ImportGamAsset", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("GamAssetFile.ImportFailedDetail", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ImportGamAsset", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("GamAssetFile.ImportFailedDetail", ex.Message));
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.ImportGamAsset", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("GamAssetFile.ImportFailed"));
        }
    }

    private void ToggleGmodDisabledCollapse()
    {
        if (!IsAtRoot)
        {
            return;
        }

        IsGmodDisabledCollapsed = !IsGmodDisabledCollapsed;
        if (IsGmodDisabledCollapsed && SelectedAsset?.IsGmodDisabledAsset == true)
        {
            SelectedAsset = GetAssetById(SystemAssetDefinitions.SubscribeId);
        }

        try
        {
            saveGmodDisabledCollapsePreference(IsGmodDisabledCollapsed);
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetListViewModel.SaveCollapsePreference", ex);
        }

        LoadAssets();
    }

    private static void SaveGmodDisabledCollapsePreference(bool isCollapsed)
    {
        var settings = AppSettings.Load();
        settings.CollapseGmodDisabledAddons = isCollapsed;
        settings.Save();
    }

    private void PruneShareSelection(Configuration configuration)
    {
        if (!IsShareMode)
        {
            return;
        }

        var existingAssets = new HashSet<string>(
            configuration.Assets
                .Where(asset => !asset.IsSystem)
                .Select(asset => asset.Id),
            StringComparer.Ordinal);
        var existingGroups = new HashSet<string>(
            configuration.AssetGroups.Select(group => group.Id),
            StringComparer.Ordinal);
        sharedAssetIds.RemoveWhere(id => !existingAssets.Contains(id));
        sharedGroupIds.RemoveWhere(id => !existingGroups.Contains(id));
    }

    private void ApplySharePresentation(Configuration configuration)
    {
        foreach (var entry in Entries)
        {
            var selected = entry.IsGroup
                ? sharedGroupIds.Contains(entry.Id)
                : sharedAssetIds.Contains(entry.Id);
            entry.SetSharePresentation(IsShareMode, selected);
        }

        shareSelectionItems.Clear();
        foreach (var group in configuration.AssetGroups
                     .Where(group => sharedGroupIds.Contains(group.Id))
                     .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            shareSelectionItems.Add(new ShareSelectionItemViewModel(
                group.Name,
                L.Get("AssetGroup.Badge")));
        }
        foreach (var asset in configuration.Assets
                     .Where(asset => sharedAssetIds.Contains(asset.Id))
                     .OrderBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            shareSelectionItems.Add(new ShareSelectionItemViewModel(
                asset.Name,
                asset.IsSmart
                    ? L.Get("SmartAsset.Badge")
                    : L.Get("GamAssetFile.FixedAsset")));
        }
        RaiseShareProperties();
    }

    private void RemoveSelectionsCoveredByGroup(string selectedGroupId)
    {
        var configuration = addonManager.GetConfiguration();
        var descendantIds = GetDescendantGroupIds(configuration, selectedGroupId);
        sharedGroupIds.RemoveWhere(id =>
            !string.Equals(id, selectedGroupId, StringComparison.Ordinal) &&
            descendantIds.Contains(id));
        sharedAssetIds.RemoveWhere(id =>
        {
            var asset = configuration.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            return asset != null &&
                   !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                   (string.Equals(asset.ParentGroupId, selectedGroupId, StringComparison.Ordinal) ||
                    descendantIds.Contains(asset.ParentGroupId));
        });
    }

    private bool IsCoveredBySelectedGroup(string? parentGroupId)
    {
        if (string.IsNullOrWhiteSpace(parentGroupId) || sharedGroupIds.Count == 0)
        {
            return false;
        }

        var configuration = addonManager.GetConfiguration();
        var groups = configuration.AssetGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = parentGroupId;
        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (sharedGroupIds.Contains(currentId))
            {
                return true;
            }
            currentId = groups.TryGetValue(currentId, out var current)
                ? current.ParentGroupId
                : null;
        }
        return false;
    }

    private void RaiseShareProperties()
    {
        this.RaisePropertyChanged(nameof(SharedAssetCount));
        this.RaisePropertyChanged(nameof(SharedGroupCount));
        this.RaisePropertyChanged(nameof(HasShareSelection));
        this.RaisePropertyChanged(nameof(CanExportShareSelection));
        this.RaisePropertyChanged(nameof(ShareSelectionSummary));
        this.RaisePropertyChanged(nameof(ShareWorkspaceTitle));
        this.RaisePropertyChanged(nameof(ShareWorkspaceDescription));
    }

    private string BuildShareSuggestedFileName()
    {
        var configuration = addonManager.GetConfiguration();
        string candidate;
        if (sharedGroupIds.Count == 1 && sharedAssetIds.Count == 0)
        {
            var id = sharedGroupIds.Single();
            candidate = configuration.AssetGroups.FirstOrDefault(group => group.Id == id)?.Name ??
                        L.Get("GamShare.DefaultFileName");
        }
        else if (sharedAssetIds.Count == 1 && sharedGroupIds.Count == 0)
        {
            var id = sharedAssetIds.Single();
            candidate = configuration.Assets.FirstOrDefault(asset => asset.Id == id)?.Name ??
                        L.Get("GamShare.DefaultFileName");
        }
        else
        {
            candidate = L.Get("GamShare.DefaultFileName");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(candidate
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "GAM-Assets";
        }
        return safe.EndsWith(".gam", StringComparison.OrdinalIgnoreCase)
            ? safe
            : safe + ".gam";
    }

    public AssetItemViewModel? GetAssetById(string assetId)
    {
        return Assets.FirstOrDefault(asset => asset.Id == assetId);
    }

    public void RefreshAssetStates()
    {
        var configuration = addonManager.GetConfiguration();
        var visibleGroupCount = configuration.AssetGroups.Count(group =>
            string.Equals(
                group.ParentGroupId,
                currentGroupId,
                StringComparison.Ordinal));
        if (configuration.Assets.Count != Assets.Count ||
            configuration.Assets.Any(asset => GetAssetById(asset.Id) == null) ||
            visibleGroupCount != Entries.Count(entry => entry.IsGroup))
        {
            LoadAssets();
            return;
        }

        foreach (var assetViewModel in Assets)
        {
            var asset = configuration.Assets.FirstOrDefault(candidate =>
                candidate.Id == assetViewModel.Id);
            if (asset != null)
            {
                assetViewModel.RefreshFromModel(asset);
            }
        }

        foreach (var entry in Entries.Where(entry => entry.Group != null))
        {
            var group = configuration.AssetGroups.FirstOrDefault(candidate =>
                candidate.Id == entry.Id);
            if (group != null)
            {
                entry.Group!.RefreshFromModel(group);
            }
        }
    }

    public void RefreshGmodDisabledAsset()
    {
        var configuration = addonManager.GetConfiguration();
        if (configuration.Assets.Count != Assets.Count ||
            configuration.Assets.Any(asset => GetAssetById(asset.Id) == null))
        {
            LoadAssets();
            return;
        }

        var model = configuration.Assets.FirstOrDefault(asset =>
            asset.Id == AssetItemViewModel.GmodDisabledSystemAssetId);
        var viewModel = GetAssetById(AssetItemViewModel.GmodDisabledSystemAssetId);
        if (model == null || viewModel == null)
        {
            LoadAssets();
            return;
        }

        viewModel.RefreshFromModel(model);
    }

    private AssetGroup? ResolveCurrentGroup()
    {
        return currentGroupId == null
            ? null
            : addonManager.GetConfiguration().AssetGroups.FirstOrDefault(group =>
                string.Equals(group.Id, currentGroupId, StringComparison.Ordinal));
    }

    private void BuildBreadcrumbs(Configuration configuration)
    {
        breadcrumbs.Clear();
        var currentPath = new List<AssetGroup>();
        if (!string.IsNullOrWhiteSpace(currentGroupId))
        {
            var groups = configuration.AssetGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var currentId = currentGroupId;
            while (!string.IsNullOrWhiteSpace(currentId) &&
                   visited.Add(currentId) &&
                   groups.TryGetValue(currentId, out var current))
            {
                currentPath.Add(current);
                currentId = current.ParentGroupId;
            }
            currentPath.Reverse();
        }

        breadcrumbs.Add(new AssetBreadcrumbItemViewModel(
            L.Get("AssetList.Header"),
            targetGroupId: null,
            isCurrent: currentPath.Count == 0,
            hasSeparator: false,
            NavigateToGroup));
        foreach (var group in currentPath)
        {
            breadcrumbs.Add(new AssetBreadcrumbItemViewModel(
                group.Name,
                group.Id,
                isCurrent: string.Equals(group.Id, currentGroupId, StringComparison.Ordinal),
                hasSeparator: true,
                NavigateToGroup));
        }
    }

    private bool CanCreateGroupInCurrentContainer(Configuration configuration)
    {
        if (IsAtRoot)
        {
            return true;
        }

        var currentDepth = GetGroupDepth(configuration, currentGroupId!);
        return currentDepth >= 0 && currentDepth < configuration.MaxNestedGroupDepth;
    }

    private bool CanNestUnderNewGroup(Configuration configuration, string candidateGroupId)
    {
        var newGroupDepth = IsAtRoot
            ? 0
            : GetGroupDepth(configuration, currentGroupId!) + 1;
        var candidateSubtreeHeight = GetGroupSubtreeHeight(configuration, candidateGroupId);
        return newGroupDepth >= 0 &&
               newGroupDepth + 1 + candidateSubtreeHeight <=
               configuration.MaxNestedGroupDepth;
    }

    private static int GetGroupDepth(Configuration configuration, string groupId)
    {
        var groups = configuration.AssetGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = groupId;
        var depth = 0;
        while (groups.TryGetValue(currentId, out var current))
        {
            if (!visited.Add(currentId))
            {
                return -1;
            }
            if (string.IsNullOrWhiteSpace(current.ParentGroupId))
            {
                return depth;
            }
            depth++;
            currentId = current.ParentGroupId;
        }
        return -1;
    }

    private static int GetGroupSubtreeHeight(Configuration configuration, string groupId)
    {
        var children = configuration.AssetGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.ParentGroupId))
            .GroupBy(group => group.ParentGroupId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        return GetGroupSubtreeHeight(children, groupId, new HashSet<string>(StringComparer.Ordinal));
    }

    private static int GetGroupSubtreeHeight(
        IReadOnlyDictionary<string, List<AssetGroup>> children,
        string groupId,
        HashSet<string> path)
    {
        if (!path.Add(groupId) || !children.TryGetValue(groupId, out var directChildren))
        {
            return 0;
        }

        var height = 0;
        foreach (var child in directChildren)
        {
            height = Math.Max(
                height,
                1 + GetGroupSubtreeHeight(children, child.Id, path));
        }
        path.Remove(groupId);
        return height;
    }

    private static HashSet<string> GetDescendantGroupIds(
        Configuration configuration,
        string groupId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(groupId);
        while (pending.Count > 0)
        {
            var parentId = pending.Pop();
            foreach (var child in configuration.AssetGroups.Where(candidate =>
                         string.Equals(candidate.ParentGroupId, parentId, StringComparison.Ordinal)))
            {
                if (result.Add(child.Id))
                {
                    pending.Push(child.Id);
                }
            }
        }
        return result;
    }

    private Avalonia.Controls.Window? GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    private void ClearEntries()
    {
        foreach (var entry in Entries)
        {
            entry.Dispose();
        }
        Entries.Clear();
    }

    private void ClearAssets()
    {
        foreach (var asset in Assets)
        {
            asset.Dispose();
        }
        Assets.Clear();
    }

    private void RaiseNavigationProperties()
    {
        this.RaisePropertyChanged(nameof(CurrentGroupId));
        this.RaisePropertyChanged(nameof(IsAtRoot));
        this.RaisePropertyChanged(nameof(IsInsideGroup));
        this.RaisePropertyChanged(nameof(CurrentHeader));
        this.RaisePropertyChanged(nameof(CreateActionText));
        this.RaisePropertyChanged(nameof(CreateActionTooltip));
        this.RaisePropertyChanged(nameof(IsCurrentGroupEmpty));
        this.RaisePropertyChanged(nameof(IsCurrentGroupEmptyVisible));
        this.RaisePropertyChanged(nameof(HasDirectAssetInCurrentGroup));
        this.RaisePropertyChanged(nameof(IsAddonGridVisible));
        this.RaisePropertyChanged(nameof(CurrentGroupEmptyText));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.CurrentLanguage) &&
            !string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        RaiseNavigationProperties();
        this.RaisePropertyChanged(nameof(GmodDisabledCollapseTooltip));
        if (IsShareMode)
        {
            ApplySharePresentation(addonManager.GetConfiguration());
        }
    }

    private static IEnumerable<Asset> OrderAllAssets(IEnumerable<Asset> source)
    {
        return source
            .OrderBy(asset => asset.Id switch
            {
                SystemAssetDefinitions.SubscribeId => 0,
                AssetItemViewModel.GmodDisabledSystemAssetId => 1,
                _ => 2
            })
            .ThenBy(asset => string.IsNullOrWhiteSpace(asset.ParentGroupId) ? 0 : 1)
            .ThenBy(asset => asset.IsFavorite ? 0 : 1)
            .ThenBy(asset => NormalizeSortOrder(asset.SortOrder))
            .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.Id, StringComparer.Ordinal);
    }

    private static int NormalizeSortOrder(int value)
    {
        return value < 0 ? int.MaxValue : value;
    }

    private static int IndexOfEntry(
        IReadOnlyList<AssetListEntryViewModel> entries,
        AssetListEntryViewModel target)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntryKind == target.EntryKind &&
                string.Equals(entries[index].Id, target.Id, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        selectedAssetSubscription?.Dispose();
        selectedAssetSubscription = null;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        ClearEntries();
        ClearAssets();
        GC.SuppressFinalize(this);
    }

    private sealed class VisibleModelEntry
    {
        public VisibleModelEntry(Asset asset)
        {
            Asset = asset;
            Kind = AssetListEntryKind.Asset;
        }

        public VisibleModelEntry(AssetGroup group)
        {
            Group = group;
            Kind = AssetListEntryKind.Group;
        }

        public Asset? Asset { get; }
        public AssetGroup? Group { get; }
        public AssetListEntryKind Kind { get; }
        public string Id => Asset?.Id ?? Group!.Id;
        public string Name => Asset?.Name ?? Group!.Name;
        public bool IsFavorite => Asset?.IsFavorite ?? Group!.IsFavorite;
        public int SortOrder => Asset?.SortOrder ?? Group!.SortOrder;
    }
}

public sealed class ShareSelectionItemViewModel
{
    public ShareSelectionItemViewModel(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public string Kind { get; }
}

public sealed class AssetBreadcrumbItemViewModel
{
    public AssetBreadcrumbItemViewModel(
        string name,
        string? targetGroupId,
        bool isCurrent,
        bool hasSeparator,
        Action<string?> navigate)
    {
        Name = name;
        TargetGroupId = targetGroupId;
        IsCurrent = isCurrent;
        HasSeparator = hasSeparator;
        NavigateCommand = ReactiveCommand.Create(() => navigate(TargetGroupId));
    }

    public string Name { get; }
    public string? TargetGroupId { get; }
    public bool IsCurrent { get; }
    public bool HasSeparator { get; }
    public ReactiveCommand<Unit, Unit> NavigateCommand { get; }
}
