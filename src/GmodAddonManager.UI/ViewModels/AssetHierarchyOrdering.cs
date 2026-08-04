using GmodAddonManager.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.UI.ViewModels;

/// <summary>
/// Shared sibling ordering for every UI that presents the Asset/Asset Group
/// hierarchy. Keeping the picker and the left pane on this one path prevents
/// the same container from appearing in two different orders.
/// </summary>
internal static class AssetHierarchyOrdering
{
    public static IReadOnlyList<AssetHierarchyModelEntry> GetChildren(
        Configuration configuration,
        string? parentGroupId,
        Func<Asset, bool>? assetPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        assetPredicate ??= static _ => true;
        return configuration.Assets
            .Where(asset => IsDirectChild(asset.ParentGroupId, parentGroupId))
            .Where(assetPredicate)
            .Select(asset => new AssetHierarchyModelEntry(asset))
            .Concat(configuration.AssetGroups
                .Where(group => IsDirectChild(group.ParentGroupId, parentGroupId))
                .Select(group => new AssetHierarchyModelEntry(group)))
            .OrderBy(entry => entry.IsFavorite ? 0 : 1)
            .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static int NormalizeSortOrder(int value)
    {
        return value < 0 ? int.MaxValue : value;
    }

    private static bool IsDirectChild(string? candidateParentId, string? parentGroupId)
    {
        if (string.IsNullOrWhiteSpace(parentGroupId))
        {
            return string.IsNullOrWhiteSpace(candidateParentId);
        }

        return string.Equals(candidateParentId, parentGroupId, StringComparison.Ordinal);
    }
}

internal sealed class AssetHierarchyModelEntry
{
    public AssetHierarchyModelEntry(Asset asset)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        Kind = AssetListEntryKind.Asset;
    }

    public AssetHierarchyModelEntry(AssetGroup group)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
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
