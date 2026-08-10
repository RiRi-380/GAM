using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Views;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AssetGroupItemViewModel : ViewModelBase, IDisposable
{
    private AssetGroup group;
    private readonly AddonManager addonManager;
    private readonly IDialogService dialogService;
    private AssetGroupDisplayState displayState;
    private int directAssetCount;
    private int directGroupCount;
    private int recursiveAssetCount;
    private int recursiveGroupCount;
    private int recursiveAddonCount;
    private long recursiveAddonSize;
    private string groupPath = string.Empty;
    private bool isSelected;
    private Bitmap? imageBitmap;
    private bool disposed;

    public AssetGroupItemViewModel(AssetGroup group, AddonManager addonManager)
    {
        this.group = group ?? throw new ArgumentNullException(nameof(group));
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        dialogService = new DialogService();
        displayState = ResolveDisplayState();
        RefreshStatistics();

        SetEnabledCommand = ReactiveCommand.CreateFromTask(
            () => ApplyStateAsync(AddonState.Enabled));
        SetDisabledCommand = ReactiveCommand.CreateFromTask(
            () => ApplyStateAsync(AddonState.Disabled));
        SetExcludedCommand = ReactiveCommand.CreateFromTask(
            () => ApplyStateAsync(AddonState.Excluded));
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync);
        ShowDetailsCommand = ReactiveCommand.CreateFromTask(ShowDetailsAsync);
        EditImageCommand = ReactiveCommand.CreateFromTask(EditImageAsync);
        EditCommand = EditImageCommand;
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        _ = LoadImageAsync();
    }

    public string Id => group.Id;
    public string Name => group.Name;
    public string? ParentGroupId => group.ParentGroupId;
    public string Memo => group.Memo;
    public int ChildCount => DirectAssetCount + DirectGroupCount;
    public int DirectAssetCount => directAssetCount;
    public int DirectGroupCount => directGroupCount;
    public int RecursiveAssetCount => recursiveAssetCount;
    public int RecursiveGroupCount => recursiveGroupCount;
    public int RecursiveAddonCount => recursiveAddonCount;
    public long RecursiveAddonSize => recursiveAddonSize;
    public string RecursiveAddonSizeDisplay => FormatFileSize(RecursiveAddonSize);
    public string GroupPath => groupPath;
    public string AddonCountDisplay => L.Format(
        "AssetGroup.CardSummary",
        DirectAssetCount,
        DirectGroupCount,
        RecursiveAddonCount);
    public string GroupBadgeTooltip => L.Get("AssetGroup.BadgeTooltip");
    public string DetailsTooltip => L.Get("AssetGroup.DetailsAndStructureTooltip");
    public bool IsSmart => false;
    public string SmartBadgeText => string.Empty;
    public string SmartRuleText => string.Empty;
    public bool IsSystem => false;
    public bool CanDelete => true;
    public bool CanEditImage => true;
    public bool CanEditAddonDefaultState => true;
    public bool CanSetExcluded => true;
    public bool CanFavorite => true;
    public bool CanManageVersions => false;
    public int StateColumnSpan => 1;
    public string VersionDisplay => string.Empty;
    public string EnabledStateLabel => L.Get("AssetList.Enabled");
    public string DisabledStateLabel => L.Get("AssetList.Disabled");
    public string ExcludedStateLabel => L.Get("AssetList.Excluded");
    public string EnabledStateTooltip => L.Get("AssetGroup.EnabledTooltip");
    public string DisabledStateTooltip => L.Get("AssetGroup.DisabledTooltip");
    public string ExcludedStateTooltip => L.Get("AssetGroup.ExcludedTooltip");

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            SetAndRaise(ref isSelected, value);
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }

    public bool IsFavorite => group.IsFavorite;
    public string FavoriteButtonText => IsFavorite
        ? L.Get("AddonDetails.RemovedFromFavorites")
        : L.Get("AddonDetails.AddedToFavorites");

    public AssetGroupDisplayState DisplayState => displayState;
    public bool IsEnabledState => displayState == AssetGroupDisplayState.Enabled;
    public bool IsDisabledState => displayState == AssetGroupDisplayState.Disabled;
    public bool IsExcludedState => displayState == AssetGroupDisplayState.Excluded;
    public bool IsMixedState => displayState == AssetGroupDisplayState.Mixed;
    public string MixedStateText => L.Get("AssetGroup.Mixed");

    public string BorderColor => IsSelected ? "#4A90E2" : "Transparent";
    public string AssetStateColor => displayState switch
    {
        AssetGroupDisplayState.Enabled => "#4CAF50",
        AssetGroupDisplayState.Disabled => "#FF9800",
        AssetGroupDisplayState.Excluded => "#F44336",
        _ => "#9E9E9E"
    };

    public Bitmap? AssetImageBitmap
    {
        get => imageBitmap;
        private set
        {
            if (ReferenceEquals(imageBitmap, value))
            {
                return;
            }

            imageBitmap?.Dispose();
            imageBitmap = value;
            this.RaisePropertyChanged(nameof(AssetImageBitmap));
            this.RaisePropertyChanged(nameof(HasCustomImage));
            this.RaisePropertyChanged(nameof(HasNoCustomImage));
        }
    }

    public bool HasCustomImage => AssetImageBitmap != null;
    public bool HasNoCustomImage => AssetImageBitmap == null;

    public ReactiveCommand<Unit, Unit> SetEnabledCommand { get; }
    public ReactiveCommand<Unit, Unit> SetDisabledCommand { get; }
    public ReactiveCommand<Unit, Unit> SetExcludedCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> EditImageCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public void RefreshFromModel(AssetGroup updated)
    {
        group = updated;
        displayState = ResolveDisplayState();
        RefreshStatistics();
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(ParentGroupId));
        this.RaisePropertyChanged(nameof(Memo));
        this.RaisePropertyChanged(nameof(ChildCount));
        this.RaisePropertyChanged(nameof(DirectAssetCount));
        this.RaisePropertyChanged(nameof(DirectGroupCount));
        this.RaisePropertyChanged(nameof(RecursiveAssetCount));
        this.RaisePropertyChanged(nameof(RecursiveGroupCount));
        this.RaisePropertyChanged(nameof(RecursiveAddonCount));
        this.RaisePropertyChanged(nameof(RecursiveAddonSize));
        this.RaisePropertyChanged(nameof(RecursiveAddonSizeDisplay));
        this.RaisePropertyChanged(nameof(GroupPath));
        this.RaisePropertyChanged(nameof(AddonCountDisplay));
        this.RaisePropertyChanged(nameof(IsFavorite));
        this.RaisePropertyChanged(nameof(FavoriteButtonText));
        RaiseStateProperties();
        _ = LoadImageAsync();
    }

    private AssetGroupDisplayState ResolveDisplayState()
    {
        return addonManager.GetAssetGroupDisplayState(group.Id);
    }

    private void RefreshStatistics()
    {
        var configuration = addonManager.GetConfiguration();
        var subtreeGroupIds = GetSubtreeGroupIds(configuration, group.Id);
        directAssetCount = configuration.Assets.Count(asset =>
            !asset.IsSystem &&
            string.Equals(asset.ParentGroupId, group.Id, StringComparison.Ordinal));
        directGroupCount = configuration.AssetGroups.Count(candidate =>
            string.Equals(candidate.ParentGroupId, group.Id, StringComparison.Ordinal));
        recursiveAssetCount = configuration.Assets.Count(asset =>
            !asset.IsSystem &&
            !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
            subtreeGroupIds.Contains(asset.ParentGroupId));
        recursiveGroupCount = Math.Max(0, subtreeGroupIds.Count - 1);

        var addonIds = configuration.Assets
            .Where(asset =>
                !asset.IsSystem &&
                !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                subtreeGroupIds.Contains(asset.ParentGroupId))
            .SelectMany(asset => asset.Addons)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        recursiveAddonCount = addonIds.Count;
        var addonMetadata = addonManager.GetAllAddons();
        recursiveAddonSize = addonIds.Sum(id =>
            addonMetadata.TryGetValue(id, out var addon) ? Math.Max(0, addon.Size) : 0L);
        groupPath = BuildGroupPath(configuration, group.Id);
    }

    private async Task ApplyStateAsync(AddonState state)
    {
        try
        {
            await addonManager.ApplyAssetGroupStateAsync(Id, state);
            ViewModelLocator.AssetListViewModel?.LoadAssets();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("AssetGroupItemViewModel.ApplyState", ex);
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        try
        {
            await addonManager.SetAssetGroupFavoriteAsync(Id, !IsFavorite);
            ViewModelLocator.AssetListViewModel?.LoadAssets();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("AssetGroupItemViewModel.ToggleFavorite", ex);
        }
    }

    private async Task ShowDetailsAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            return;
        }

        try
        {
            var configuration = addonManager.GetConfiguration();
            var latest = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, Id, StringComparison.Ordinal));
            if (latest == null)
            {
                return;
            }

            var dialog = new AssetGroupEditDialog(
                latest,
                configuration,
                addonManager,
                candidateName => NameValidationError(configuration, candidateName));
            var result = await dialog.ShowDialog<AssetGroupEditResult?>(desktop.MainWindow);
            if (result is not { IsSaved: true })
            {
                return;
            }

            await addonManager.RenameAssetGroupAsync(Id, result.Name);
            await addonManager.UpdateAssetGroupMemoAsync(Id, result.Memo);
            await addonManager.SetAssetGroupMembersAsync(
                Id,
                result.MemberAssetIds,
                result.MemberGroupIds);
            ViewModelLocator.AssetListViewModel?.LoadAssets();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("AssetGroupItemViewModel.ShowDetails", ex);
        }
    }

    private async Task EditImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            return;
        }

        try
        {
            var latest = addonManager.GetConfiguration().AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, Id, StringComparison.Ordinal));
            if (latest == null)
            {
                return;
            }

            var dialog = new AssetEditDialog(addonManager.ResolveAssetGroupImagePath(latest));
            var result = await dialog.ShowDialog<AssetEditResult?>(desktop.MainWindow);
            if (result is not { IsSaved: true })
            {
                return;
            }

            await addonManager.ApplyAssetGroupEditAsync(
                Id,
                latest.Name,
                result.SourceImagePath,
                result.Crop,
                result.RemoveImage);
            ViewModelLocator.AssetListViewModel?.LoadAssets();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("AssetGroupItemViewModel.EditImage", ex);
        }
    }

    private string? NameValidationError(Configuration configuration, string candidateName)
    {
        var trimmed = candidateName.Trim();
        var duplicateAsset = configuration.Assets.Any(asset =>
            string.Equals(asset.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        var duplicateGroup = configuration.AssetGroups.Any(candidate =>
            !string.Equals(candidate.Id, Id, StringComparison.Ordinal) &&
            string.Equals(candidate.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        return duplicateAsset || duplicateGroup
            ? L.Format("Error.AssetNameAlreadyExists", trimmed)
            : null;
    }

    private async Task DeleteAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            return;
        }

        try
        {
            var configuration = addonManager.GetConfiguration();
            var latest = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, Id, StringComparison.Ordinal));
            if (latest == null)
            {
                return;
            }

            var subtreeGroupIds = GetSubtreeGroupIds(configuration, Id);
            var directAssetCount = configuration.Assets.Count(asset =>
                !asset.IsSystem &&
                string.Equals(asset.ParentGroupId, Id, StringComparison.Ordinal));
            var directGroupCount = configuration.AssetGroups.Count(candidate =>
                string.Equals(candidate.ParentGroupId, Id, StringComparison.Ordinal));
            var recursiveAssetCount = configuration.Assets.Count(asset =>
                !asset.IsSystem &&
                !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                subtreeGroupIds.Contains(asset.ParentGroupId));
            var recursiveGroupCount = Math.Max(0, subtreeGroupIds.Count - 1);
            var parentGroupId = latest.ParentGroupId;
            var dialog = new AssetGroupDeleteDialog(
                latest.Name,
                directAssetCount,
                directGroupCount,
                recursiveAssetCount,
                recursiveGroupCount);
            var choice = await dialog.ShowDialog<AssetGroupDeleteChoice>(desktop.MainWindow);
            if (choice == AssetGroupDeleteChoice.Cancel)
            {
                return;
            }

            var mode = choice == AssetGroupDeleteChoice.DeleteAssets
                ? AssetGroupDeleteMode.DeleteAssets
                : AssetGroupDeleteMode.KeepAssets;
            await addonManager.DeleteAssetGroupAsync(Id, mode);
            ViewModelLocator.AssetListViewModel?.NavigateToGroup(parentGroupId);
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("AssetGroupItemViewModel.Delete", ex);
        }
    }

    private async Task LoadImageAsync()
    {
        Bitmap? loaded = null;
        try
        {
            var path = addonManager.ResolveAssetGroupImagePath(group);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                loaded = await Task.Run(() => new Bitmap(path));
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetGroupItemViewModel.LoadImage", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (disposed)
            {
                loaded?.Dispose();
                return;
            }
            AssetImageBitmap = loaded;
        });
    }

    private static HashSet<string> GetSubtreeGroupIds(
        Configuration configuration,
        string rootGroupId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootGroupId };
        var pending = new Stack<string>();
        pending.Push(rootGroupId);
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

    private static string BuildGroupPath(Configuration configuration, string groupId)
    {
        var groups = configuration.AssetGroups.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = groupId;
        while (groups.TryGetValue(currentId, out var current) && visited.Add(currentId))
        {
            names.Add(current.Name);
            if (string.IsNullOrWhiteSpace(current.ParentGroupId))
            {
                break;
            }
            currentId = current.ParentGroupId;
        }
        names.Reverse();
        return string.Join(" / ", names);
    }

    private static string FormatFileSize(long bytes)
    {
        var normalized = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)normalized;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:0.##} {units[unitIndex]}";
    }

    private async Task ShowOperationErrorAsync(string context, Exception ex)
    {
        SafeFileLogger.TryLogException(context, ex);
        await dialogService.ShowErrorAsync(
            L.Get("Error.Title"),
            L.Format("AssetGroup.OperationFailed", ex.Message));
    }

    private void RaiseStateProperties()
    {
        this.RaisePropertyChanged(nameof(DisplayState));
        this.RaisePropertyChanged(nameof(IsEnabledState));
        this.RaisePropertyChanged(nameof(IsDisabledState));
        this.RaisePropertyChanged(nameof(IsExcludedState));
        this.RaisePropertyChanged(nameof(IsMixedState));
        this.RaisePropertyChanged(nameof(AssetStateColor));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.CurrentLanguage) &&
            !string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        this.RaisePropertyChanged(nameof(AddonCountDisplay));
        this.RaisePropertyChanged(nameof(RecursiveAddonSizeDisplay));
        this.RaisePropertyChanged(nameof(GroupBadgeTooltip));
        this.RaisePropertyChanged(nameof(DetailsTooltip));
        this.RaisePropertyChanged(nameof(FavoriteButtonText));
        this.RaisePropertyChanged(nameof(MixedStateText));
        this.RaisePropertyChanged(nameof(EnabledStateLabel));
        this.RaisePropertyChanged(nameof(DisabledStateLabel));
        this.RaisePropertyChanged(nameof(ExcludedStateLabel));
        this.RaisePropertyChanged(nameof(EnabledStateTooltip));
        this.RaisePropertyChanged(nameof(DisabledStateTooltip));
        this.RaisePropertyChanged(nameof(ExcludedStateTooltip));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        AssetImageBitmap = null;
    }
}
