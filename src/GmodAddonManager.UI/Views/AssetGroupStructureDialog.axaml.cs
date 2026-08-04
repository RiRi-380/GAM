using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.UI.Views;

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

public partial class AssetGroupStructureDialog : Window
{
    private readonly ObservableCollection<AssetGroupStructureOption> options = new();

    public AssetGroupStructureDialog()
    {
        InitializeComponent();
        TitleText.Text = L.Get("AssetGroup.EditStructure");
        MemberItemsControl.ItemsSource = options;
    }

    public AssetGroupStructureDialog(AssetGroup group, Configuration configuration)
        : this(group, configuration, selectedAssetIds: null, selectedGroupIds: null)
    {
    }

    public AssetGroupStructureDialog(
        AssetGroup group,
        Configuration configuration,
        IReadOnlySet<string>? selectedAssetIds,
        IReadOnlySet<string>? selectedGroupIds)
        : this()
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(configuration);
        TitleText.Text = L.Format("AssetGroup.EditStructureTitle", group.Name);

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

        foreach (var option in assetOptions
                     .Concat(groupOptions)
                     .OrderBy(option => option.IsFavorite ? 0 : 1)
                     .ThenBy(option => option.SortOrder < 0 ? int.MaxValue : option.SortOrder)
                     .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(option => option.Kind)
                     .ThenBy(option => option.Id, StringComparer.Ordinal))
        {
            options.Add(option);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Close(new AssetGroupStructureEditResult
        {
            IsSaved = true,
            MemberAssetIds = options
                .Where(option => option.IsSelected && !option.IsGroup)
                .Select(option => option.Id)
                .ToArray(),
            MemberGroupIds = options
                .Where(option => option.IsSelected && option.IsGroup)
                .Select(option => option.Id)
                .ToArray()
        });
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
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

public sealed class AssetGroupStructureEditResult
{
    public bool IsSaved { get; set; }
    public IReadOnlyList<string> MemberAssetIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MemberGroupIds { get; set; } = Array.Empty<string>();
}
