using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace GmodAddonManager.UI.ViewModels;

/// <summary>
/// Independent hierarchy navigator for the manual-addon target picker. It
/// reads the same configuration tree as the left pane without changing that
/// pane's current location or selection.
/// </summary>
public sealed class AssetTargetPickerViewModel : ViewModelBase, IDisposable
{
    private readonly AddonManager addonManager;
    private readonly Dictionary<string, AssetItemViewModel> targetAssets;
    private readonly HashSet<string> ownedTargetAssetIds = new(StringComparer.Ordinal);
    private readonly ObservableCollection<AssetListEntryViewModel> entries = new();
    private readonly ObservableCollection<AssetBreadcrumbItemViewModel> breadcrumbs = new();
    private string? currentGroupId;
    private bool disposed;

    public AssetTargetPickerViewModel(
        AddonManager addonManager,
        IEnumerable<AssetItemViewModel> targetAssets)
    {
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        ArgumentNullException.ThrowIfNull(targetAssets);

        this.targetAssets = targetAssets
            .Where(asset => !asset.IsSystem && !asset.IsSmart)
            .GroupBy(asset => asset.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
        Reload();
    }

    public ObservableCollection<AssetListEntryViewModel> Entries => entries;
    public ObservableCollection<AssetBreadcrumbItemViewModel> Breadcrumbs => breadcrumbs;
    public event EventHandler? Navigated;
    public string? CurrentGroupId => currentGroupId;
    public bool IsAtRoot => string.IsNullOrWhiteSpace(currentGroupId);
    public bool IsInsideGroup => !IsAtRoot;
    public bool IsEmpty => Entries.Count == 0;
    public string CurrentHeader => IsAtRoot
        ? L.Get("AssetList.Header")
        : ResolveCurrentGroup()?.Name ?? L.Get("AssetGroup.Badge");
    public string EmptyText => L.Get("AssetSelection.Empty");

    public void OpenGroup(AssetListEntryViewModel? entry)
    {
        if (entry?.IsGroup != true)
        {
            return;
        }

        NavigateToGroup(entry.Id);
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
        Reload();
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void ReturnToParent()
    {
        if (IsAtRoot)
        {
            return;
        }

        NavigateToGroup(ResolveCurrentGroup()?.ParentGroupId);
    }

    public void ReturnToRoot()
    {
        NavigateToGroup(groupId: null);
    }

    public AssetListEntryViewModel? RegisterTargetAsset(
        AssetItemViewModel asset,
        bool ownsAsset = false)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.IsSystem || asset.IsSmart)
        {
            return null;
        }

        targetAssets[asset.Id] = asset;
        if (ownsAsset)
        {
            ownedTargetAssetIds.Add(asset.Id);
        }
        Reload();
        return Entries.FirstOrDefault(entry =>
            entry.IsAsset && string.Equals(entry.Id, asset.Id, StringComparison.Ordinal));
    }

    private void Reload()
    {
        ClearEntries();

        var configuration = addonManager.GetConfiguration();
        if (!string.IsNullOrWhiteSpace(currentGroupId) &&
            configuration.AssetGroups.All(group =>
                !string.Equals(group.Id, currentGroupId, StringComparison.Ordinal)))
        {
            currentGroupId = null;
        }

        BuildBreadcrumbs(configuration);
        foreach (var modelEntry in AssetHierarchyOrdering.GetChildren(
                     configuration,
                     currentGroupId,
                     asset => !asset.IsSystem &&
                              !asset.IsSmart &&
                              targetAssets.ContainsKey(asset.Id)))
        {
            if (modelEntry.Asset != null)
            {
                Entries.Add(new AssetListEntryViewModel(
                    targetAssets[modelEntry.Asset.Id],
                    modelEntry.Asset.ParentGroupId));
                continue;
            }

            Entries.Add(new AssetListEntryViewModel(
                new AssetGroupItemViewModel(modelEntry.Group!, addonManager)));
        }

        RaiseNavigationProperties();
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
        Breadcrumbs.Clear();
        var currentPath = new List<AssetGroup>();
        if (!string.IsNullOrWhiteSpace(currentGroupId))
        {
            var groups = configuration.AssetGroups.ToDictionary(
                group => group.Id,
                StringComparer.Ordinal);
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

        Breadcrumbs.Add(new AssetBreadcrumbItemViewModel(
            L.Get("AssetList.Header"),
            targetGroupId: null,
            isCurrent: currentPath.Count == 0,
            hasSeparator: false,
            NavigateToGroup));
        foreach (var group in currentPath)
        {
            Breadcrumbs.Add(new AssetBreadcrumbItemViewModel(
                group.Name,
                group.Id,
                isCurrent: string.Equals(group.Id, currentGroupId, StringComparison.Ordinal),
                hasSeparator: true,
                NavigateToGroup));
        }
    }

    private void RaiseNavigationProperties()
    {
        this.RaisePropertyChanged(nameof(CurrentGroupId));
        this.RaisePropertyChanged(nameof(IsAtRoot));
        this.RaisePropertyChanged(nameof(IsInsideGroup));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(CurrentHeader));
        this.RaisePropertyChanged(nameof(EmptyText));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.CurrentLanguage) &&
            !string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        BuildBreadcrumbs(addonManager.GetConfiguration());
        RaiseNavigationProperties();
    }

    private void ClearEntries()
    {
        foreach (var entry in Entries)
        {
            entry.Dispose();
        }
        Entries.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        ClearEntries();
        foreach (var assetId in ownedTargetAssetIds)
        {
            if (targetAssets.TryGetValue(assetId, out var asset))
            {
                asset.Dispose();
            }
        }
        ownedTargetAssetIds.Clear();
        GC.SuppressFinalize(this);
    }
}
