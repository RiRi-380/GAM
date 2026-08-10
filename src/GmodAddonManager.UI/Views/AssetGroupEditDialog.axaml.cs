using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.UI.Views;

public partial class AssetGroupEditDialog : Window
{
    private readonly AssetGroup? group;
    private readonly AddonManager? addonManager;
    private readonly Func<string, string?>? nameValidator;
    private readonly IDialogService dialogService = new DialogService();
    private readonly ObservableCollection<AssetGroupStructureOption> structureOptions = new();
    private IReadOnlyList<string> pendingMemberAssetIds = Array.Empty<string>();
    private IReadOnlyList<string> pendingMemberGroupIds = Array.Empty<string>();

    public AssetGroupEditDialog()
    {
        InitializeComponent();
        InitializeStructurePage();
        EditStructureButton.IsEnabled = false;
        UpdateSaveState();
        ConfigureSummary(null, null);
    }

    public AssetGroupEditDialog(
        AssetGroup group,
        Configuration configuration,
        AddonManager addonManager,
        Func<string, string?> nameValidator)
    {
        this.group = group ?? throw new ArgumentNullException(nameof(group));
        this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
        this.nameValidator = nameValidator ?? throw new ArgumentNullException(nameof(nameValidator));

        InitializeComponent();
        InitializeStructurePage();
        GroupNameTextBox.Text = group.Name;
        MemoTextBox.Text = group.Memo;
        pendingMemberAssetIds = configuration.Assets
            .Where(asset =>
                !asset.IsSystem &&
                string.Equals(asset.ParentGroupId, group.Id, StringComparison.Ordinal))
            .Select(asset => asset.Id)
            .ToArray();
        pendingMemberGroupIds = configuration.AssetGroups
            .Where(candidate =>
                string.Equals(candidate.ParentGroupId, group.Id, StringComparison.Ordinal))
            .Select(candidate => candidate.Id)
            .ToArray();
        ConfigureSummary(group, configuration, pendingMemberAssetIds, pendingMemberGroupIds);

        UpdateSaveState();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        if (GroupNameTextBox == null || SaveButton == null)
        {
            return;
        }

        var name = GroupNameTextBox.Text?.Trim();
        var validationError = string.IsNullOrWhiteSpace(name)
            ? null
            : nameValidator?.Invoke(name);
        NameValidationText.Text = validationError ?? string.Empty;
        NameValidationText.IsVisible = !string.IsNullOrWhiteSpace(validationError);
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(name) && validationError == null;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void OnEditStructure(object? sender, RoutedEventArgs e)
    {
        if (group == null || addonManager == null)
        {
            return;
        }

        try
        {
            var configuration = addonManager.GetConfiguration();
            var latest = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, group.Id, StringComparison.Ordinal));
            if (latest == null)
            {
                return;
            }

            structureOptions.Clear();
            foreach (var option in BuildStructureOptions(
                         latest,
                         configuration,
                         pendingMemberAssetIds.ToHashSet(StringComparer.Ordinal),
                         pendingMemberGroupIds.ToHashSet(StringComparer.Ordinal)))
            {
                structureOptions.Add(option);
            }

            StructureTitleText.Text = L.Format("AssetGroup.EditStructureTitle", latest.Name);
            Title = L.Get("AssetGroup.EditStructure");
            DetailsPage.IsVisible = false;
            StructurePage.IsVisible = true;
            StructureBackButton.Focus();
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AssetGroupEditDialog.OnEditStructure", ex);
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Format("AssetGroup.OperationFailed", ex.Message));
        }
    }

    private void OnStructureBack(object? sender, RoutedEventArgs e)
    {
        ShowDetailsPage();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (StructurePage.IsVisible &&
            !e.IsProgrammatic &&
            e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            ShowDetailsPage();
        }
    }

    private void OnStructureApply(object? sender, RoutedEventArgs e)
    {
        if (group == null || addonManager == null)
        {
            ShowDetailsPage();
            return;
        }

        pendingMemberAssetIds = structureOptions
            .Where(option => option.IsSelected && !option.IsGroup)
            .Select(option => option.Id)
            .ToArray();
        pendingMemberGroupIds = structureOptions
            .Where(option => option.IsSelected && option.IsGroup)
            .Select(option => option.Id)
            .ToArray();

        var configuration = addonManager.GetConfiguration();
        var latest = configuration.AssetGroups.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, group.Id, StringComparison.Ordinal));
        if (latest != null)
        {
            ConfigureSummary(
                latest,
                configuration,
                pendingMemberAssetIds,
                pendingMemberGroupIds);
        }

        ShowDetailsPage();
    }

    private void InitializeStructurePage()
    {
        StructureMemberItemsControl.ItemsSource = structureOptions;
    }

    private void ShowDetailsPage()
    {
        StructurePage.IsVisible = false;
        DetailsPage.IsVisible = true;
        Title = L.Get("AssetGroup.DetailsTitle");
        structureOptions.Clear();
        EditStructureButton.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = GroupNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || nameValidator?.Invoke(name) != null)
        {
            UpdateSaveState();
            return;
        }

        Close(new AssetGroupEditResult
        {
            IsSaved = true,
            Name = name,
            Memo = MemoTextBox.Text?.Trim() ?? string.Empty,
            MemberAssetIds = pendingMemberAssetIds,
            MemberGroupIds = pendingMemberGroupIds
        });
    }

    private void ConfigureSummary(
        AssetGroup? sourceGroup,
        Configuration? configuration,
        IReadOnlyCollection<string>? memberAssetIds = null,
        IReadOnlyCollection<string>? memberGroupIds = null)
    {
        if (sourceGroup == null || configuration == null)
        {
            GroupPathText.Text = "-";
            DirectContentsText.Text = "-";
            RecursiveContentsText.Text = "-";
            AddonSummaryText.Text = "-";
            return;
        }

        var directAssetIds = (memberAssetIds ?? configuration.Assets
                .Where(asset =>
                    !asset.IsSystem &&
                    string.Equals(asset.ParentGroupId, sourceGroup.Id, StringComparison.Ordinal))
                .Select(asset => asset.Id))
            .ToHashSet(StringComparer.Ordinal);
        var directGroupIds = (memberGroupIds ?? configuration.AssetGroups
                .Where(candidate =>
                    string.Equals(candidate.ParentGroupId, sourceGroup.Id, StringComparison.Ordinal))
                .Select(candidate => candidate.Id))
            .ToHashSet(StringComparer.Ordinal);
        var subtreeIds = GetPendingSubtreeGroupIds(configuration, sourceGroup.Id, directGroupIds);
        var directAssets = directAssetIds.Count;
        var directGroups = directGroupIds.Count;
        var recursiveAssets = configuration.Assets.Where(asset =>
                !asset.IsSystem &&
                (directAssetIds.Contains(asset.Id) ||
                 (!string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                  !string.Equals(asset.ParentGroupId, sourceGroup.Id, StringComparison.Ordinal) &&
                  subtreeIds.Contains(asset.ParentGroupId))))
            .ToList();
        var recursiveGroups = Math.Max(0, subtreeIds.Count - 1);
        var addonIds = recursiveAssets
            .SelectMany(asset => asset.Addons)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var metadata = addonManager?.GetAllAddons();
        var totalSize = addonIds.Sum(id =>
            metadata != null && metadata.TryGetValue(id, out var addon)
                ? Math.Max(0, addon.Size)
                : 0L);

        GroupPathText.Text = BuildGroupPath(configuration, sourceGroup.Id);
        DirectContentsText.Text = L.Format(
            "AssetGroup.DirectContentsSummary",
            directAssets,
            directGroups);
        RecursiveContentsText.Text = L.Format(
            "AssetGroup.RecursiveContentsSummary",
            recursiveAssets.Count,
            recursiveGroups);
        AddonSummaryText.Text = L.Format(
            "AssetGroup.AddonSummary",
            addonIds.Count,
            FormatFileSize(totalSize));
    }

    private static HashSet<string> GetPendingSubtreeGroupIds(
        Configuration configuration,
        string rootGroupId,
        IReadOnlyCollection<string> directGroupIds)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootGroupId };
        var pending = new Stack<string>(directGroupIds);
        while (pending.Count > 0)
        {
            var groupId = pending.Pop();
            if (!result.Add(groupId))
            {
                continue;
            }

            foreach (var child in configuration.AssetGroups.Where(candidate =>
                         string.Equals(candidate.ParentGroupId, groupId, StringComparison.Ordinal)))
            {
                pending.Push(child.Id);
            }
        }
        return result;
    }

    private static string BuildGroupPath(Configuration configuration, string groupId)
    {
        var byId = configuration.AssetGroups.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = groupId;
        while (byId.TryGetValue(currentId, out var current) && visited.Add(currentId))
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
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    internal static IReadOnlyList<AssetGroupStructureOption> BuildStructureOptions(
        AssetGroup group,
        Configuration configuration,
        IReadOnlySet<string>? selectedAssetIds,
        IReadOnlySet<string>? selectedGroupIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(configuration);

        var parentId = group.ParentGroupId;
        var assetOptions = configuration.Assets
            .Where(asset =>
                !asset.IsSystem &&
                (string.Equals(asset.ParentGroupId, group.Id, StringComparison.Ordinal) ||
                 string.Equals(asset.ParentGroupId, parentId, StringComparison.Ordinal)))
            .Select(asset => new AssetGroupStructureOption(
                asset.Id,
                asset.Name,
                AssetListEntryKind.Asset,
                asset.IsFavorite,
                asset.SortOrder,
                selectedAssetIds?.Contains(asset.Id) ??
                string.Equals(asset.ParentGroupId, group.Id, StringComparison.Ordinal)));
        var groupOptions = configuration.AssetGroups
            .Where(candidate =>
                !string.Equals(candidate.Id, group.Id, StringComparison.Ordinal) &&
                (string.Equals(candidate.ParentGroupId, group.Id, StringComparison.Ordinal) ||
                 (string.Equals(candidate.ParentGroupId, parentId, StringComparison.Ordinal) &&
                  CanMoveUnder(configuration, candidate.Id, group.Id))))
            .Select(candidate => new AssetGroupStructureOption(
                candidate.Id,
                candidate.Name,
                AssetListEntryKind.Group,
                candidate.IsFavorite,
                candidate.SortOrder,
                selectedGroupIds?.Contains(candidate.Id) ??
                string.Equals(candidate.ParentGroupId, group.Id, StringComparison.Ordinal)));

        return assetOptions
            .Concat(groupOptions)
            .OrderBy(option => option.IsFavorite ? 0 : 1)
            .ThenBy(option => option.SortOrder < 0 ? int.MaxValue : option.SortOrder)
            .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.Kind)
            .ThenBy(option => option.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CanMoveUnder(
        Configuration configuration,
        string candidateGroupId,
        string destinationGroupId)
    {
        var destinationDepth = GetDepth(configuration, destinationGroupId);
        var subtreeHeight = GetSubtreeHeight(configuration, candidateGroupId);
        return destinationDepth >= 0 &&
               destinationDepth + 1 + subtreeHeight <= configuration.MaxNestedGroupDepth;
    }

    private static int GetDepth(Configuration configuration, string groupId)
    {
        var groups = configuration.AssetGroups.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
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

    private static int GetSubtreeHeight(Configuration configuration, string rootId)
    {
        var children = configuration.AssetGroups
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ParentGroupId))
            .GroupBy(candidate => candidate.ParentGroupId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        return GetSubtreeHeight(children, rootId, new HashSet<string>(StringComparer.Ordinal));
    }

    private static int GetSubtreeHeight(
        IReadOnlyDictionary<string, List<AssetGroup>> children,
        string rootId,
        HashSet<string> path)
    {
        if (!path.Add(rootId) || !children.TryGetValue(rootId, out var directChildren))
        {
            return 0;
        }

        var height = 0;
        foreach (var child in directChildren)
        {
            height = Math.Max(height, 1 + GetSubtreeHeight(children, child.Id, path));
        }
        path.Remove(rootId);
        return height;
    }
}

public sealed class AssetGroupStructureOption
{
    public AssetGroupStructureOption(
        string id,
        string name,
        AssetListEntryKind kind,
        bool isFavorite,
        int sortOrder,
        bool isSelected)
    {
        Id = id;
        Name = name;
        Kind = kind;
        IsFavorite = isFavorite;
        SortOrder = sortOrder;
        IsSelected = isSelected;
    }

    public string Id { get; }
    public string Name { get; }
    public AssetListEntryKind Kind { get; }
    public bool IsGroup => Kind == AssetListEntryKind.Group;
    public bool IsFavorite { get; }
    public int SortOrder { get; }
    public bool IsSelected { get; set; }
    public string KindText => IsGroup
        ? L.Get("AssetGroup.Kind.Group")
        : L.Get("AssetGroup.Kind.Asset");
    public string KindBackground => IsGroup ? "#304A64" : "#343434";
    public string KindForeground => IsGroup ? "#B4D8F8" : "#D0D0D0";
}

public sealed class AssetGroupEditResult
{
    public bool IsSaved { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public IReadOnlyList<string> MemberAssetIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MemberGroupIds { get; set; } = Array.Empty<string>();
}
