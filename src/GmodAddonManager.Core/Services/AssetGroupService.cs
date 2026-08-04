using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Pure configuration operations for bounded nested Asset Groups and
    /// persisted mixed Asset/Group ordering. Persistence, runtime reconciliation,
    /// and Undo-stack recording remain the AddonManager transaction boundary.
    /// </summary>
    public sealed class AssetGroupService
    {
        public bool NameExists(
            Configuration configuration,
            string? name,
            string? exceptAssetId = null,
            string? exceptGroupId = null)
        {
            RequireConfiguration(configuration);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalized = name.Trim();
            return configuration.Assets.Any(asset =>
                       !string.Equals(asset.Id, exceptAssetId, StringComparison.Ordinal) &&
                       string.Equals(asset.Name?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)) ||
                   configuration.AssetGroups.Any(group =>
                       !string.Equals(group.Id, exceptGroupId, StringComparison.Ordinal) &&
                       string.Equals(group.Name?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
        }

        public string ValidateUniqueName(
            Configuration configuration,
            string? name,
            string? exceptAssetId = null,
            string? exceptGroupId = null)
        {
            RequireConfiguration(configuration);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    $"Asset or Group name must contain 1 to {GamAssetDocumentCodec.MaximumAssetNameLength} characters.",
                    nameof(name));
            }

            var normalized = name.Trim();
            if (normalized.Length > GamAssetDocumentCodec.MaximumAssetNameLength)
            {
                throw new ArgumentException(
                    $"Asset or Group name must contain 1 to {GamAssetDocumentCodec.MaximumAssetNameLength} characters.",
                    nameof(name));
            }
            if (normalized.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "Asset or Group name cannot contain control characters.",
                    nameof(name));
            }
            if (NameExists(configuration, normalized, exceptAssetId, exceptGroupId))
            {
                throw new InvalidOperationException(
                    $"An Asset or Group named '{normalized}' already exists.");
            }

            return normalized;
        }

        public AssetGroupDisplayState GetDisplayState(
            Configuration configuration,
            string groupId)
        {
            var group = GetGroup(configuration, groupId);
            return GetDisplayState(
                configuration,
                group,
                new HashSet<string>(StringComparer.Ordinal));
        }

        public IReadOnlyList<Asset> GetOrderedChildren(
            Configuration configuration,
            string groupId)
        {
            var group = GetGroup(configuration, groupId);
            return OrderAssets(GetChildren(configuration, group.Id)).ToList();
        }

        public IReadOnlyList<AssetGroup> GetOrderedChildGroups(
            Configuration configuration,
            string groupId)
        {
            var group = GetGroup(configuration, groupId);
            return OrderGroups(GetChildGroups(configuration, group.Id)).ToList();
        }

        public int GetActualMaxNestedGroupDepth(Configuration configuration)
        {
            RequireConfiguration(configuration);
            if (configuration.AssetGroups.Count == 0)
            {
                return 0;
            }

            return configuration.AssetGroups.Max(group =>
                GetGroupDepth(configuration, group.Id));
        }

        public UndoAction? SetMaxNestedGroupDepth(
            Configuration configuration,
            int maxNestedDepth)
        {
            RequireConfiguration(configuration);
            ValidateMaxNestedGroupDepth(maxNestedDepth);
            var actualDepth = GetActualMaxNestedGroupDepth(configuration);
            if (maxNestedDepth < actualDepth)
            {
                throw new InvalidOperationException(
                    $"The configured depth cannot be lowered to {maxNestedDepth} " +
                    $"while the current Asset Group tree requires depth {actualDepth}.");
            }
            if (configuration.MaxNestedGroupDepth == maxNestedDepth)
            {
                return null;
            }

            var previous = configuration.MaxNestedGroupDepth;
            configuration.MaxNestedGroupDepth = maxNestedDepth;
            return new UndoAction(
                UndoActionType.AssetGroupDepthLimitChanged,
                $"Changed maximum nested Asset Group depth to {maxNestedDepth}")
            {
                PreviousMaxNestedGroupDepth = previous
            };
        }

        /// <summary>
        /// A non-empty uniform Group inherits its currently displayed state.
        /// Empty or Mixed Groups use the last/default bulk state.
        /// </summary>
        public AddonState GetNewChildState(
            Configuration configuration,
            string groupId)
        {
            var group = GetGroup(configuration, groupId);
            var displayState = GetDisplayState(configuration, group.Id);
            return displayState switch
            {
                AssetGroupDisplayState.Enabled => AddonState.Enabled,
                AssetGroupDisplayState.Disabled => AddonState.Disabled,
                AssetGroupDisplayState.Excluded => AddonState.Excluded,
                _ => group.DefaultChildState
            };
        }

        public AssetGroup CreateGroup(
            Configuration configuration,
            string name,
            IEnumerable<string>? memberAssetIds,
            out UndoAction undoAction)
        {
            return CreateGroup(
                configuration,
                name,
                parentGroupId: null,
                memberAssetIds,
                childGroupIds: null,
                out undoAction);
        }

        public AssetGroup CreateGroup(
            Configuration configuration,
            string name,
            string? parentGroupId,
            IEnumerable<string>? memberAssetIds,
            IEnumerable<string>? childGroupIds,
            out UndoAction undoAction)
        {
            RequireConfiguration(configuration);
            var normalizedName = ValidateUniqueName(configuration, name);
            var members = ResolveDistinctCustomAssets(configuration, memberAssetIds).ToList();
            var selectedGroups = ResolveDistinctGroups(configuration, childGroupIds).ToList();
            var parent = string.IsNullOrWhiteSpace(parentGroupId)
                ? null
                : GetGroup(configuration, parentGroupId);
            var normalizedParentId = parent?.Id;
            var misplacedAsset = members.FirstOrDefault(asset =>
                !string.Equals(
                    asset.ParentGroupId,
                    normalizedParentId,
                    StringComparison.Ordinal));
            if (misplacedAsset != null)
            {
                throw new InvalidOperationException(
                    $"Asset '{misplacedAsset.Name}' is not in the new Group's parent container.");
            }
            var misplacedGroup = selectedGroups.FirstOrDefault(group =>
                string.Equals(group.Id, normalizedParentId, StringComparison.Ordinal) ||
                !string.Equals(
                    group.ParentGroupId,
                    normalizedParentId,
                    StringComparison.Ordinal));
            if (misplacedGroup != null)
            {
                throw new InvalidOperationException(
                    $"Asset Group '{misplacedGroup.Name}' is not a movable sibling of the new Group.");
            }

            var group = new AssetGroup(normalizedName)
            {
                Id = CreateUniqueEntityId(configuration),
                ParentGroupId = normalizedParentId,
                DefaultChildState = parent == null
                    ? AddonState.Enabled
                    : GetNewChildState(configuration, parent.Id),
                SortOrder = GetNextSortOrder(
                    configuration,
                    normalizedParentId,
                    isFavorite: false)
            };
            var groupDepth = parent == null
                ? 0
                : checked(GetGroupDepth(configuration, parent.Id) + 1);
            EnsureDepthAllowed(configuration, groupDepth, subtreeHeight: 0);
            foreach (var childGroup in selectedGroups)
            {
                EnsureDepthAllowed(
                    configuration,
                    checked(groupDepth + 1),
                    GetSubtreeHeight(configuration, childGroup.Id));
            }

            var previousParents = members.ToDictionary(
                asset => asset.Id,
                asset => asset.ParentGroupId,
                StringComparer.Ordinal);
            var previousOrders = members.ToDictionary(
                asset => asset.Id,
                asset => asset.SortOrder,
                StringComparer.Ordinal);
            var previousGroupParents = selectedGroups.ToDictionary(
                child => child.Id,
                child => child.ParentGroupId,
                StringComparer.Ordinal);
            var previousGroupOrders = selectedGroups.ToDictionary(
                child => child.Id,
                child => child.SortOrder,
                StringComparer.Ordinal);

            configuration.AssetGroups.Add(group);
            AssignEntriesToEmptyGroup(members, selectedGroups, group.Id);

            undoAction = new UndoAction(
                UndoActionType.AssetGroupCreated,
                $"Asset Group '{group.Name}' created")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                AffectedAssetIds = members.Select(asset => asset.Id).ToList(),
                AffectedGroupIds = selectedGroups.Select(child => child.Id).ToList(),
                PreviousAssetParentGroupIds = previousParents,
                PreviousAssetSortOrders = previousOrders,
                PreviousGroupParentGroupIds = previousGroupParents,
                PreviousAssetGroupSortOrders = previousGroupOrders
            };
            return group;
        }

        /// <summary>
        /// Adds a newly created leaf Asset and applies the user-facing creation
        /// defaults. Root Assets start Enabled; Group children inherit the
        /// Group's last/default state.
        /// </summary>
        public UndoAction AddNewAsset(
            Configuration configuration,
            Asset asset,
            string? parentGroupId = null)
        {
            RequireConfiguration(configuration);
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }
            if (asset.IsSystem)
            {
                throw new InvalidOperationException("System Assets cannot be created inside a Group.");
            }
            if (configuration.Assets.Any(existing =>
                    string.Equals(existing.Id, asset.Id, StringComparison.Ordinal)) ||
                configuration.AssetGroups.Any(existing =>
                    string.Equals(existing.Id, asset.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Duplicate Asset identity: {asset.Id}");
            }

            asset.Name = ValidateUniqueName(configuration, asset.Name);
            AssetGroup? group = null;
            if (!string.IsNullOrWhiteSpace(parentGroupId))
            {
                group = GetGroup(configuration, parentGroupId);
            }

            asset.IsSystem = false;
            asset.IsFavorite = false;
            asset.ParentGroupId = group?.Id;
            asset.SetWholeState(group == null
                ? AddonState.Enabled
                : GetNewChildState(configuration, group.Id));
            asset.SortOrder = GetNextSortOrder(
                configuration,
                asset.ParentGroupId,
                asset.IsFavorite);
            configuration.Assets.Add(asset);

            return new UndoAction(
                UndoActionType.AssetCreated,
                $"Asset '{asset.Name}' created")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                GroupId = group?.Id,
                GroupName = group?.Name
            };
        }

        public UndoAction? ApplyGroupState(
            Configuration configuration,
            string groupId,
            AddonState state)
        {
            if (!Enum.IsDefined(typeof(AddonState), state))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            var group = GetGroup(configuration, groupId);
            var descendantGroups = GetGroupSubtree(configuration, group.Id).ToList();
            var descendantGroupIds = new HashSet<string>(
                descendantGroups.Select(candidate => candidate.Id),
                StringComparer.Ordinal);
            var children = configuration.Assets
                .Where(asset =>
                    !asset.IsSystem &&
                    !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                    descendantGroupIds.Contains(asset.ParentGroupId))
                .ToList();
            var previousStates = children.ToDictionary(
                child => child.Id,
                child => child.GetWholeState(),
                StringComparer.Ordinal);
            var previousDefaults = descendantGroups.ToDictionary(
                candidate => candidate.Id,
                candidate => candidate.DefaultChildState,
                StringComparer.Ordinal);
            if (descendantGroups.All(candidate => candidate.DefaultChildState == state) &&
                children.All(child => child.GetWholeState() == state))
            {
                return null;
            }

            var previousDefault = group.DefaultChildState;
            foreach (var descendantGroup in descendantGroups)
            {
                descendantGroup.DefaultChildState = state;
            }
            foreach (var child in children)
            {
                child.SetWholeState(state);
            }

            return new UndoAction(
                UndoActionType.AssetGroupStateChanged,
                $"Changed Asset Group '{group.Name}' to {state}")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                PreviousGroupDefaultState = previousDefault,
                PreviousGroupDefaultStates = previousDefaults,
                PreviousAssetStates = previousStates,
                AffectedAssetIds = children.Select(child => child.Id).ToList(),
                AffectedGroupIds = descendantGroups.Select(candidate => candidate.Id).ToList()
            };
        }

        public UndoAction? SetGroupMembers(
            Configuration configuration,
            string groupId,
            IEnumerable<string>? memberAssetIds)
        {
            var existingChildGroups = GetOrderedChildGroups(configuration, groupId)
                .Select(group => group.Id)
                .ToList();
            return SetGroupMembers(
                configuration,
                groupId,
                memberAssetIds,
                existingChildGroups);
        }

        public UndoAction? SetGroupMembers(
            Configuration configuration,
            string groupId,
            IEnumerable<string>? memberAssetIds,
            IEnumerable<string>? childGroupIds)
        {
            var group = GetGroup(configuration, groupId);
            var desired = ResolveDistinctCustomAssets(configuration, memberAssetIds).ToList();
            var desiredGroups = ResolveDistinctGroups(configuration, childGroupIds).ToList();
            var conflicting = desired.FirstOrDefault(asset =>
                !string.Equals(asset.ParentGroupId, group.Id, StringComparison.Ordinal) &&
                !string.Equals(
                    asset.ParentGroupId,
                    group.ParentGroupId,
                    StringComparison.Ordinal));
            if (conflicting != null)
            {
                throw new InvalidOperationException(
                    $"Asset '{conflicting.Name}' already belongs to another Asset Group.");
            }
            var conflictingGroup = desiredGroups.FirstOrDefault(child =>
                string.Equals(child.Id, group.Id, StringComparison.Ordinal) ||
                (!string.Equals(child.ParentGroupId, group.Id, StringComparison.Ordinal) &&
                 !string.Equals(
                     child.ParentGroupId,
                     group.ParentGroupId,
                     StringComparison.Ordinal)));
            if (conflictingGroup != null)
            {
                throw new InvalidOperationException(
                    $"Asset Group '{conflictingGroup.Name}' is not a movable sibling or existing child.");
            }
            foreach (var desiredGroup in desiredGroups)
            {
                EnsureCanMoveGroup(configuration, desiredGroup, group);
            }

            var desiredIds = new HashSet<string>(
                desired.Select(asset => asset.Id),
                StringComparer.Ordinal);
            var desiredGroupIds = new HashSet<string>(
                desiredGroups.Select(child => child.Id),
                StringComparer.Ordinal);
            var current = GetOrderedChildren(configuration, group.Id).ToList();
            var currentGroups = GetOrderedChildGroups(configuration, group.Id).ToList();
            var currentIds = new HashSet<string>(
                current.Select(asset => asset.Id),
                StringComparer.Ordinal);
            var currentGroupIds = new HashSet<string>(
                currentGroups.Select(child => child.Id),
                StringComparer.Ordinal);
            if (currentIds.SetEquals(desiredIds) &&
                currentGroupIds.SetEquals(desiredGroupIds))
            {
                return null;
            }

            var changed = current.Where(asset => !desiredIds.Contains(asset.Id))
                .Concat(desired.Where(asset => !currentIds.Contains(asset.Id)))
                .Distinct()
                .ToList();
            var changedGroups = currentGroups
                .Where(child => !desiredGroupIds.Contains(child.Id))
                .Concat(desiredGroups.Where(child => !currentGroupIds.Contains(child.Id)))
                .Distinct()
                .ToList();
            var previousParents = changed.ToDictionary(
                asset => asset.Id,
                asset => asset.ParentGroupId,
                StringComparer.Ordinal);
            var previousOrders = changed.ToDictionary(
                asset => asset.Id,
                asset => asset.SortOrder,
                StringComparer.Ordinal);
            var previousGroupParents = changedGroups.ToDictionary(
                child => child.Id,
                child => child.ParentGroupId,
                StringComparer.Ordinal);
            var previousGroupOrders = changedGroups.ToDictionary(
                child => child.Id,
                child => child.SortOrder,
                StringComparer.Ordinal);

            foreach (var removed in current.Where(asset => !desiredIds.Contains(asset.Id)))
            {
                removed.ParentGroupId = group.ParentGroupId;
                removed.SortOrder = GetNextSortOrder(
                    configuration,
                    group.ParentGroupId,
                    removed.IsFavorite,
                    exceptAssetId: removed.Id);
            }
            foreach (var removed in currentGroups.Where(child =>
                         !desiredGroupIds.Contains(child.Id)))
            {
                removed.ParentGroupId = group.ParentGroupId;
                removed.SortOrder = GetNextSortOrder(
                    configuration,
                    group.ParentGroupId,
                    removed.IsFavorite,
                    exceptGroupId: removed.Id);
            }

            foreach (var added in desired.Where(asset => !currentIds.Contains(asset.Id)))
            {
                added.ParentGroupId = group.Id;
                added.SortOrder = GetNextSortOrder(
                    configuration,
                    group.Id,
                    added.IsFavorite,
                    exceptAssetId: added.Id);
            }
            foreach (var added in desiredGroups.Where(child =>
                         !currentGroupIds.Contains(child.Id)))
            {
                added.ParentGroupId = group.Id;
                added.SortOrder = GetNextSortOrder(
                    configuration,
                    group.Id,
                    added.IsFavorite,
                    exceptGroupId: added.Id);
            }

            return new UndoAction(
                UndoActionType.AssetGroupMembershipChanged,
                $"Changed members of Asset Group '{group.Name}'")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                AffectedAssetIds = changed.Select(asset => asset.Id).ToList(),
                AffectedGroupIds = changedGroups.Select(child => child.Id).ToList(),
                PreviousAssetParentGroupIds = previousParents,
                PreviousAssetSortOrders = previousOrders,
                PreviousGroupParentGroupIds = previousGroupParents,
                PreviousAssetGroupSortOrders = previousGroupOrders
            };
        }

        /// <summary>
        /// Explicitly transfers one existing Asset. Unlike creation, its state
        /// is preserved even when the destination Group has a different default.
        /// </summary>
        public UndoAction? MoveAsset(
            Configuration configuration,
            string assetId,
            string? destinationGroupId)
        {
            var asset = GetCustomAsset(configuration, assetId);
            var destination = string.IsNullOrWhiteSpace(destinationGroupId)
                ? null
                : GetGroup(configuration, destinationGroupId);
            if (string.Equals(
                    asset.ParentGroupId,
                    destination?.Id,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var previousParent = asset.ParentGroupId;
            var previousOrder = asset.SortOrder;
            asset.ParentGroupId = destination?.Id;
            asset.SortOrder = GetNextSortOrder(
                configuration,
                asset.ParentGroupId,
                asset.IsFavorite,
                exceptAssetId: asset.Id);

            return new UndoAction(
                UndoActionType.AssetGroupMembershipChanged,
                destination == null
                    ? $"Moved Asset '{asset.Name}' to the root"
                    : $"Moved Asset '{asset.Name}' to Asset Group '{destination.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                GroupId = destination?.Id,
                GroupName = destination?.Name,
                AffectedAssetIds = new List<string> { asset.Id },
                PreviousAssetParentGroupIds = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [asset.Id] = previousParent
                },
                PreviousAssetSortOrders = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [asset.Id] = previousOrder
                }
            };
        }

        public UndoAction? MoveGroup(
            Configuration configuration,
            string groupId,
            string? destinationGroupId)
        {
            var group = GetGroup(configuration, groupId);
            var destination = string.IsNullOrWhiteSpace(destinationGroupId)
                ? null
                : GetGroup(configuration, destinationGroupId);
            if (string.Equals(
                    group.ParentGroupId,
                    destination?.Id,
                    StringComparison.Ordinal))
            {
                return null;
            }

            EnsureCanMoveGroup(configuration, group, destination);
            var previousParent = group.ParentGroupId;
            var previousOrder = group.SortOrder;
            group.ParentGroupId = destination?.Id;
            group.SortOrder = GetNextSortOrder(
                configuration,
                group.ParentGroupId,
                group.IsFavorite,
                exceptGroupId: group.Id);

            return new UndoAction(
                UndoActionType.AssetGroupMembershipChanged,
                destination == null
                    ? $"Moved Asset Group '{group.Name}' to the root"
                    : $"Moved Asset Group '{group.Name}' to Asset Group '{destination.Name}'")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                AffectedGroupIds = new List<string> { group.Id },
                PreviousGroupParentGroupIds =
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [group.Id] = previousParent
                    },
                PreviousAssetGroupSortOrders =
                    new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        [group.Id] = previousOrder
                    }
            };
        }

        public UndoAction DeleteGroup(Configuration configuration, string groupId)
        {
            return DeleteGroup(
                configuration,
                groupId,
                AssetGroupDeleteMode.KeepAssets);
        }

        public UndoAction DeleteGroup(
            Configuration configuration,
            string groupId,
            AssetGroupDeleteMode deleteMode)
        {
            if (!Enum.IsDefined(typeof(AssetGroupDeleteMode), deleteMode))
            {
                throw new ArgumentOutOfRangeException(nameof(deleteMode));
            }

            var group = GetGroup(configuration, groupId);
            var subtreeGroups = GetGroupSubtree(configuration, group.Id).ToList();
            var subtreeGroupIds = new HashSet<string>(
                subtreeGroups.Select(candidate => candidate.Id),
                StringComparer.Ordinal);
            var subtreeAssets = configuration.Assets
                .Where(asset =>
                    !string.IsNullOrWhiteSpace(asset.ParentGroupId) &&
                    subtreeGroupIds.Contains(asset.ParentGroupId))
                .ToList();
            if (subtreeAssets.Any(IsProtectedGroupAsset))
            {
                throw new InvalidOperationException(
                    "System Assets cannot be deleted with or unwrapped from an Asset Group.");
            }

            var directAssets = GetOrderedChildren(configuration, group.Id).ToList();
            var directGroups = GetOrderedChildGroups(configuration, group.Id).ToList();
            var affectedAssets = deleteMode == AssetGroupDeleteMode.DeleteAssets
                ? subtreeAssets
                : directAssets;
            var affectedGroups = deleteMode == AssetGroupDeleteMode.DeleteAssets
                ? subtreeGroups
                : directGroups;
            var previousParents = affectedAssets.ToDictionary(
                asset => asset.Id,
                asset => asset.ParentGroupId,
                StringComparer.Ordinal);
            var previousOrders = affectedAssets.ToDictionary(
                asset => asset.Id,
                asset => asset.SortOrder,
                StringComparer.Ordinal);
            var previousGroupParents = affectedGroups.ToDictionary(
                child => child.Id,
                child => child.ParentGroupId,
                StringComparer.Ordinal);
            var previousGroupOrders = affectedGroups.ToDictionary(
                child => child.Id,
                child => child.SortOrder,
                StringComparer.Ordinal);
            var directEntries = GetContainerEntries(configuration, group.Id)
                .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();

            if (deleteMode == AssetGroupDeleteMode.DeleteAssets)
            {
                foreach (var child in subtreeAssets)
                {
                    configuration.Assets.Remove(child);
                }
                foreach (var descendant in subtreeGroups)
                {
                    configuration.AssetGroups.Remove(descendant);
                }
            }
            else
            {
                configuration.AssetGroups.Remove(group);
                foreach (var entry in directEntries)
                {
                    entry.ParentGroupId = group.ParentGroupId;
                    entry.SortOrder = GetNextSortOrder(
                        configuration,
                        group.ParentGroupId,
                        entry.IsFavorite,
                        entry.Kind == AssetListEntryKind.Asset ? entry.Id : null,
                        entry.Kind == AssetListEntryKind.Group ? entry.Id : null);
                }
            }

            return new UndoAction(
                UndoActionType.AssetGroupDeleted,
                deleteMode == AssetGroupDeleteMode.DeleteAssets
                    ? $"Asset Group '{group.Name}' and its contained tree deleted"
                    : $"Asset Group '{group.Name}' deleted")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                DeletedAssetGroup = group,
                DeletedAssets = deleteMode == AssetGroupDeleteMode.DeleteAssets
                    ? subtreeAssets
                    : null,
                DeletedAssetGroups = deleteMode == AssetGroupDeleteMode.DeleteAssets
                    ? subtreeGroups
                    : null,
                AffectedAssetIds = affectedAssets.Select(child => child.Id).ToList(),
                AffectedGroupIds = affectedGroups.Select(child => child.Id).ToList(),
                PreviousAssetParentGroupIds = previousParents,
                PreviousAssetSortOrders = previousOrders,
                PreviousGroupParentGroupIds = previousGroupParents,
                PreviousAssetGroupSortOrders = previousGroupOrders
            };
        }

        /// <summary>
        /// Reorders a custom root entry or child Asset. targetIndex is the final
        /// zero-based index among reorderable entries in that container. The
        /// destination is clamped to the entry's favorite/normal band.
        /// </summary>
        public UndoAction? ReorderEntry(
            Configuration configuration,
            AssetListEntryKind kind,
            string entryId,
            int targetIndex,
            string? parentGroupId)
        {
            RequireConfiguration(configuration);
            OrderEntry moving;
            if (kind == AssetListEntryKind.Asset)
            {
                var asset = GetCustomAsset(configuration, entryId);
                moving = new OrderEntry(asset);
                if (!string.Equals(
                        asset.ParentGroupId,
                        parentGroupId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The Asset is no longer in the requested order container.");
                }
            }
            else
            {
                var group = GetGroup(configuration, entryId);
                moving = new OrderEntry(group);
                if (!string.Equals(
                        group.ParentGroupId,
                        parentGroupId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The Asset Group is no longer in the requested order container.");
                }
            }

            var entries = GetContainerEntries(configuration, parentGroupId)
                .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();
            var currentIndex = entries.FindIndex(entry =>
                entry.Kind == moving.Kind &&
                string.Equals(entry.Id, moving.Id, StringComparison.Ordinal));
            if (currentIndex < 0)
            {
                throw new InvalidOperationException($"Order entry not found: {entryId}");
            }

            var bandIndexes = entries
                .Select((entry, index) => new { entry, index })
                .Where(item => item.entry.IsFavorite == moving.IsFavorite)
                .Select(item => item.index)
                .ToList();
            var clampedTarget = Math.Max(
                bandIndexes[0],
                Math.Min(bandIndexes[bandIndexes.Count - 1], targetIndex));
            if (clampedTarget == currentIndex)
            {
                return null;
            }

            var previousAssetOrders = CaptureAssetOrders(entries);
            var previousGroupOrders = CaptureGroupOrders(entries);
            entries.RemoveAt(currentIndex);
            entries.Insert(Math.Min(clampedTarget, entries.Count), moving);
            AssignSequentialOrders(entries);

            return new UndoAction(
                UndoActionType.AssetOrderChanged,
                $"Reordered {(kind == AssetListEntryKind.Asset ? "Asset" : "Asset Group")} '{moving.Name}'")
            {
                AssetId = kind == AssetListEntryKind.Asset ? moving.Id : null,
                GroupId = kind == AssetListEntryKind.Group ? moving.Id : null,
                PreviousAssetSortOrders = previousAssetOrders,
                PreviousAssetGroupSortOrders = previousGroupOrders
            };
        }

        public UndoAction? SetFavorite(
            Configuration configuration,
            AssetListEntryKind kind,
            string entryId,
            bool isFavorite)
        {
            RequireConfiguration(configuration);
            OrderEntry entry;
            string? parentGroupId;
            if (kind == AssetListEntryKind.Asset)
            {
                var asset = GetCustomAsset(configuration, entryId);
                if (asset.IsFavorite == isFavorite)
                {
                    return null;
                }
                entry = new OrderEntry(asset);
                parentGroupId = asset.ParentGroupId;
            }
            else
            {
                var group = GetGroup(configuration, entryId);
                if (group.IsFavorite == isFavorite)
                {
                    return null;
                }
                entry = new OrderEntry(group);
                parentGroupId = group.ParentGroupId;
            }

            var containerEntries = GetContainerEntries(configuration, parentGroupId).ToList();
            var previousAssetOrders = CaptureAssetOrders(containerEntries);
            var previousGroupOrders = CaptureGroupOrders(containerEntries);
            var previousFavorite = entry.IsFavorite;
            entry.IsFavorite = isFavorite;
            entry.SortOrder = GetNextSortOrder(
                configuration,
                parentGroupId,
                isFavorite,
                kind == AssetListEntryKind.Asset ? entry.Id : null,
                kind == AssetListEntryKind.Group ? entry.Id : null);

            return new UndoAction(
                kind == AssetListEntryKind.Asset
                    ? UndoActionType.AssetFavoriteChanged
                    : UndoActionType.AssetGroupFavoriteChanged,
                $"{(isFavorite ? "Favorited" : "Unfavorited")} {(kind == AssetListEntryKind.Asset ? "Asset" : "Asset Group")} '{entry.Name}'")
            {
                AssetId = kind == AssetListEntryKind.Asset ? entry.Id : null,
                GroupId = kind == AssetListEntryKind.Group ? entry.Id : null,
                PreviousFavoriteState = kind == AssetListEntryKind.Asset
                    ? previousFavorite
                    : null,
                PreviousGroupFavoriteState = kind == AssetListEntryKind.Group
                    ? previousFavorite
                    : null,
                PreviousAssetSortOrders = previousAssetOrders,
                PreviousAssetGroupSortOrders = previousGroupOrders
            };
        }

        public UndoAction? RenameGroup(
            Configuration configuration,
            string groupId,
            string name)
        {
            var group = GetGroup(configuration, groupId);
            var normalizedName = ValidateUniqueName(
                configuration,
                name,
                exceptGroupId: group.Id);
            if (string.Equals(group.Name, normalizedName, StringComparison.Ordinal))
            {
                return null;
            }

            var previousName = group.Name;
            group.Name = normalizedName;
            return new UndoAction(
                UndoActionType.AssetGroupRenamed,
                $"Renamed Asset Group '{previousName}' to '{normalizedName}'")
            {
                GroupId = group.Id,
                GroupName = normalizedName,
                PreviousGroupName = previousName
            };
        }

        public UndoAction? SetAssetMemo(
            Configuration configuration,
            string assetId,
            string? memo)
        {
            var asset = GetCustomAsset(configuration, assetId);
            var normalized = memo ?? string.Empty;
            if (string.Equals(asset.Memo, normalized, StringComparison.Ordinal))
            {
                return null;
            }

            var previous = asset.Memo;
            asset.Memo = normalized;
            return new UndoAction(
                UndoActionType.AssetMemoChanged,
                $"Changed memo for Asset '{asset.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                PreviousMemo = previous
            };
        }

        public UndoAction? SetGroupMemo(
            Configuration configuration,
            string groupId,
            string? memo)
        {
            var group = GetGroup(configuration, groupId);
            var normalized = memo ?? string.Empty;
            if (string.Equals(group.Memo, normalized, StringComparison.Ordinal))
            {
                return null;
            }

            var previous = group.Memo;
            group.Memo = normalized;
            return new UndoAction(
                UndoActionType.AssetGroupMemoChanged,
                $"Changed memo for Asset Group '{group.Name}'")
            {
                GroupId = group.Id,
                GroupName = group.Name,
                PreviousMemo = previous
            };
        }

        /// <summary>
        /// Applies the configuration-only inverse of an Asset Group/order action.
        /// The returned mutation supplies an exact in-memory rollback for a later
        /// persistence failure. Runtime and image-file work remain the caller's
        /// responsibility.
        /// </summary>
        public bool TryUndo(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            RequireConfiguration(configuration);
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            mutation = null;
            switch (action.Type)
            {
                case UndoActionType.AssetGroupCreated:
                    return TryUndoGroupCreated(configuration, action, out mutation);
                case UndoActionType.AssetGroupDeleted:
                    return TryUndoGroupDeleted(configuration, action, out mutation);
                case UndoActionType.AssetGroupStateChanged:
                    return TryUndoGroupState(configuration, action, out mutation);
                case UndoActionType.AssetGroupMembershipChanged:
                case UndoActionType.AssetOrderChanged:
                    return TryUndoStructure(configuration, action, out mutation);
                case UndoActionType.AssetFavoriteChanged:
                case UndoActionType.AssetGroupFavoriteChanged:
                    return TryUndoFavorite(configuration, action, out mutation);
                case UndoActionType.AssetGroupRenamed:
                    return TryUndoGroupRename(configuration, action, out mutation);
                case UndoActionType.AssetGroupImageChanged:
                    return TryUndoGroupImageReference(configuration, action, out mutation);
                case UndoActionType.AssetGroupDepthLimitChanged:
                    return TryUndoDepthLimit(configuration, action, out mutation);
                case UndoActionType.AssetMemoChanged:
                case UndoActionType.AssetGroupMemoChanged:
                    return TryUndoMemo(configuration, action, out mutation);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Repairs schema-level invariants without touching runtime state. During
        /// schema-5 migration, legacyVisibleOrder preserves the exact ordering
        /// users previously saw: fixed, favorites by name, then normal by name.
        /// </summary>
        public bool NormalizeConfiguration(
            Configuration configuration,
            bool legacyVisibleOrder = false)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var changed = false;
            if (configuration.Assets == null)
            {
                configuration.Assets = new List<Asset>();
                changed = true;
            }
            if (configuration.AssetGroups == null)
            {
                configuration.AssetGroups = new List<AssetGroup>();
                changed = true;
            }

            changed |= NormalizeGroupIdentities(configuration);
            changed |= NormalizeGlobalNames(configuration);

            var normalizedMaxDepth = Math.Max(
                Configuration.MinimumNestedGroupDepth,
                Math.Min(
                    Configuration.MaximumNestedGroupDepth,
                    configuration.MaxNestedGroupDepth));
            if (configuration.MaxNestedGroupDepth != normalizedMaxDepth)
            {
                configuration.MaxNestedGroupDepth = normalizedMaxDepth;
                changed = true;
            }

            var groupIds = new HashSet<string>(
                configuration.AssetGroups.Select(group => group.Id),
                StringComparer.Ordinal);
            foreach (var asset in configuration.Assets)
            {
                var normalizedMemo = asset.IsSystem
                    ? string.Empty
                    : asset.Memo ?? string.Empty;
                if (!string.Equals(asset.Memo, normalizedMemo, StringComparison.Ordinal))
                {
                    asset.Memo = normalizedMemo;
                    changed = true;
                }
                var parent = asset.ParentGroupId?.Trim();
                var normalizedParent = asset.IsSystem ||
                                       string.IsNullOrWhiteSpace(parent) ||
                                       !groupIds.Contains(parent)
                    ? null
                    : parent;
                if (!string.Equals(asset.ParentGroupId, normalizedParent, StringComparison.Ordinal))
                {
                    asset.ParentGroupId = normalizedParent;
                    changed = true;
                }
            }

            foreach (var group in configuration.AssetGroups)
            {
                var normalizedMemo = group.Memo ?? string.Empty;
                if (!string.Equals(group.Memo, normalizedMemo, StringComparison.Ordinal))
                {
                    group.Memo = normalizedMemo;
                    changed = true;
                }
                if (!Enum.IsDefined(typeof(AddonState), group.DefaultChildState))
                {
                    group.DefaultChildState = AddonState.Enabled;
                    changed = true;
                }
            }

            changed |= NormalizeGroupHierarchy(configuration, groupIds);

            var systemIndex = 0;
            foreach (var systemAsset in configuration.Assets
                         .Where(asset => asset.IsSystem)
                         .OrderBy(GetSystemAssetRank)
                         .ThenBy(asset => asset.Id, StringComparer.Ordinal))
            {
                if (systemAsset.SortOrder != systemIndex)
                {
                    systemAsset.SortOrder = systemIndex;
                    changed = true;
                }
                systemIndex++;
            }

            changed |= NormalizeContainerOrders(configuration, null, legacyVisibleOrder);
            foreach (var group in configuration.AssetGroups)
            {
                changed |= NormalizeContainerOrders(configuration, group.Id, legacyVisibleOrder);
            }

            return changed;
        }

        private static bool TryUndoGroupCreated(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (string.IsNullOrWhiteSpace(action.GroupId))
            {
                return false;
            }
            var groupIndex = configuration.AssetGroups.FindIndex(group =>
                string.Equals(group.Id, action.GroupId, StringComparison.Ordinal));
            if (groupIndex < 0 ||
                !TryResolveAssetsForSnapshot(configuration, action, out var assets) ||
                !TryResolveGroupsForSnapshot(configuration, action, out var groups) ||
                !CanRestoreParents(
                    configuration,
                    action.PreviousAssetParentGroupIds,
                    forbiddenGroupId: action.GroupId) ||
                !CanRestoreGroupParents(
                    configuration,
                    action.PreviousGroupParentGroupIds,
                    forbiddenGroupId: action.GroupId))
            {
                return false;
            }

            var group = configuration.AssetGroups[groupIndex];
            if (configuration.Assets.Any(asset =>
                    string.Equals(
                        asset.ParentGroupId,
                        group.Id,
                        StringComparison.Ordinal) &&
                    !assets.ContainsKey(asset.Id)) ||
                configuration.AssetGroups.Any(candidate =>
                    string.Equals(
                        candidate.ParentGroupId,
                        group.Id,
                        StringComparison.Ordinal) &&
                    !groups.ContainsKey(candidate.Id)))
            {
                return false;
            }
            var currentParents = CaptureParents(assets.Values);
            var currentOrders = CaptureOrders(assets.Values);
            var currentGroupParents = CaptureGroupParents(groups.Values);
            var currentGroupOrders = CaptureGroupOrders(groups.Values);
            configuration.AssetGroups.RemoveAt(groupIndex);
            RestoreAssetStructure(
                assets,
                action.PreviousAssetParentGroupIds,
                action.PreviousAssetSortOrders);
            RestoreGroupStructure(
                groups,
                action.PreviousGroupParentGroupIds,
                action.PreviousAssetGroupSortOrders);

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () =>
                {
                    if (configuration.AssetGroups.All(candidate => candidate.Id != group.Id))
                    {
                        configuration.AssetGroups.Insert(
                            Math.Min(groupIndex, configuration.AssetGroups.Count),
                            group);
                    }
                    RestoreAssetStructure(assets, currentParents, currentOrders);
                    RestoreGroupStructure(
                        groups,
                        currentGroupParents,
                        currentGroupOrders);
                },
                removedGroup: group);
            return true;
        }

        private static bool TryUndoGroupDeleted(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            var group = action.DeletedAssetGroup;
            if (group == null ||
                configuration.AssetGroups.Any(candidate =>
                    string.Equals(candidate.Id, group.Id, StringComparison.Ordinal)) ||
                configuration.Assets.Any(asset =>
                    string.Equals(asset.Id, group.Id, StringComparison.Ordinal) ||
                    string.Equals(
                        asset.Name,
                        group.Name,
                        StringComparison.OrdinalIgnoreCase)) ||
                configuration.AssetGroups.Any(candidate => string.Equals(
                    candidate.Name,
                    group.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (action.DeletedAssets != null)
            {
                return TryUndoGroupAndAssetsDeleted(
                    configuration,
                    action,
                    group,
                    out mutation);
            }

            if (!TryResolveAssetsForSnapshot(configuration, action, out var assets) ||
                !TryResolveGroupsForSnapshot(configuration, action, out var groups) ||
                !CanRestoreParentsWithAdditionalGroup(
                    configuration,
                    action.PreviousAssetParentGroupIds,
                    group.Id) ||
                !CanRestoreGroupParents(
                    configuration,
                    action.PreviousGroupParentGroupIds,
                    new[] { group }))
            {
                return false;
            }

            var currentParents = CaptureParents(assets.Values);
            var currentOrders = CaptureOrders(assets.Values);
            var currentGroupParents = CaptureGroupParents(groups.Values);
            var currentGroupOrders = CaptureGroupOrders(groups.Values);
            configuration.AssetGroups.Add(group);
            RestoreAssetStructure(
                assets,
                action.PreviousAssetParentGroupIds,
                action.PreviousAssetSortOrders);
            RestoreGroupStructure(
                groups,
                action.PreviousGroupParentGroupIds,
                action.PreviousAssetGroupSortOrders);

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () =>
                {
                    RestoreAssetStructure(assets, currentParents, currentOrders);
                    RestoreGroupStructure(
                        groups,
                        currentGroupParents,
                        currentGroupOrders);
                    configuration.AssetGroups.Remove(group);
                },
                restoredGroup: group);
            return true;
        }

        private static bool TryUndoGroupAndAssetsDeleted(
            Configuration configuration,
            UndoAction action,
            AssetGroup group,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            var deletedAssets = action.DeletedAssets!;
            var deletedGroups = action.DeletedAssetGroups ?? new List<AssetGroup> { group };
            if (deletedAssets.Any(asset => asset == null || IsProtectedGroupAsset(asset)))
            {
                return false;
            }

            var deletedById = new Dictionary<string, Asset>(StringComparer.Ordinal);
            var deletedGroupsById = new Dictionary<string, AssetGroup>(StringComparer.Ordinal);
            var restoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var deletedGroup in deletedGroups)
            {
                if (deletedGroup == null ||
                    string.IsNullOrWhiteSpace(deletedGroup.Id) ||
                    string.IsNullOrWhiteSpace(deletedGroup.Name) ||
                    !deletedGroupsById.TryAdd(deletedGroup.Id, deletedGroup) ||
                    !restoredNames.Add(deletedGroup.Name))
                {
                    return false;
                }
            }
            if (!deletedGroupsById.ContainsKey(group.Id))
            {
                return false;
            }

            foreach (var asset in deletedAssets)
            {
                if (string.IsNullOrWhiteSpace(asset.Id) ||
                    string.IsNullOrWhiteSpace(asset.Name) ||
                    deletedGroupsById.ContainsKey(asset.Id) ||
                    !deletedById.TryAdd(asset.Id, asset) ||
                    !restoredNames.Add(asset.Name))
                {
                    return false;
                }
            }

            var previousParents = action.PreviousAssetParentGroupIds;
            var previousOrders = action.PreviousAssetSortOrders;
            var previousGroupParents = action.PreviousGroupParentGroupIds;
            var previousGroupOrders = action.PreviousAssetGroupSortOrders;
            if (previousParents == null ||
                previousOrders == null ||
                previousGroupParents == null ||
                previousGroupOrders == null ||
                previousParents.Count != deletedById.Count ||
                previousOrders.Count != deletedById.Count ||
                previousGroupParents.Count != deletedGroupsById.Count ||
                previousGroupOrders.Count != deletedGroupsById.Count ||
                deletedById.Keys.Any(id =>
                    !previousParents.ContainsKey(id) ||
                    !previousOrders.ContainsKey(id)) ||
                deletedGroupsById.Keys.Any(id =>
                    !previousGroupParents.ContainsKey(id) ||
                    !previousGroupOrders.ContainsKey(id)) ||
                !CanRestoreParentsWithAdditionalGroups(
                    configuration,
                    previousParents,
                    deletedGroupsById.Keys) ||
                !CanRestoreGroupParents(
                    configuration,
                    previousGroupParents,
                    deletedGroups))
            {
                return false;
            }

            var restoredIds = new HashSet<string>(deletedById.Keys, StringComparer.Ordinal);
            restoredIds.UnionWith(deletedGroupsById.Keys);
            if (configuration.Assets.Any(asset =>
                    restoredIds.Contains(asset.Id) ||
                    restoredNames.Contains(asset.Name)) ||
                configuration.AssetGroups.Any(candidate =>
                    restoredIds.Contains(candidate.Id) ||
                    restoredNames.Contains(candidate.Name)))
            {
                return false;
            }

            configuration.AssetGroups.AddRange(deletedGroups);
            configuration.Assets.AddRange(deletedAssets);
            RestoreAssetStructure(deletedById, previousParents, previousOrders);
            RestoreGroupStructure(
                deletedGroupsById,
                previousGroupParents,
                previousGroupOrders);

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: deletedAssets.Count > 0,
                rollback: () =>
                {
                    foreach (var asset in deletedAssets)
                    {
                        configuration.Assets.Remove(asset);
                    }
                    foreach (var deletedGroup in deletedGroups)
                    {
                        configuration.AssetGroups.Remove(deletedGroup);
                    }
                },
                restoredGroup: group);
            return true;
        }

        private static bool TryUndoGroupState(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (string.IsNullOrWhiteSpace(action.GroupId))
            {
                return false;
            }
            var previousDefaults = action.PreviousGroupDefaultStates;
            if (previousDefaults == null)
            {
                if (!action.PreviousGroupDefaultState.HasValue ||
                    !Enum.IsDefined(
                        typeof(AddonState),
                        action.PreviousGroupDefaultState.Value))
                {
                    return false;
                }
                previousDefaults = new Dictionary<string, AddonState>(StringComparer.Ordinal)
                {
                    [action.GroupId] = action.PreviousGroupDefaultState.Value
                };
            }
            if (previousDefaults.Values.Any(value =>
                    !Enum.IsDefined(typeof(AddonState), value)))
            {
                return false;
            }

            var previousStates = action.PreviousAssetStates;
            var groups = new Dictionary<string, AssetGroup>(StringComparer.Ordinal);
            foreach (var groupId in previousDefaults.Keys)
            {
                var resolved = configuration.AssetGroups.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, groupId, StringComparison.Ordinal));
                if (resolved == null)
                {
                    return false;
                }
                groups[groupId] = resolved;
            }
            if (previousStates == null)
            {
                return false;
            }

            var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
            foreach (var item in previousStates)
            {
                if (!Enum.IsDefined(typeof(AddonState), item.Value))
                {
                    return false;
                }
                var asset = configuration.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.Key, StringComparison.Ordinal));
                if (asset == null)
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(asset.ParentGroupId) ||
                    !groups.ContainsKey(asset.ParentGroupId))
                {
                    return false;
                }
                assets[item.Key] = asset;
            }

            var currentDefaults = CaptureGroupDefaults(groups.Values);
            var currentStates = assets.ToDictionary(
                item => item.Key,
                item => item.Value.GetWholeState(),
                StringComparer.Ordinal);
            RestoreGroupDefaults(groups, previousDefaults);
            foreach (var item in previousStates)
            {
                assets[item.Key].SetWholeState(item.Value);
            }

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: previousStates.Count > 0,
                rollback: () =>
                {
                    RestoreGroupDefaults(groups, currentDefaults);
                    foreach (var item in currentStates)
                    {
                        assets[item.Key].SetWholeState(item.Value);
                    }
                });
            return true;
        }

        private static bool TryUndoStructure(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (!TryResolveAssetsForSnapshot(configuration, action, out var assets) ||
                !TryResolveGroupsForSnapshot(configuration, action, out var groups) ||
                !CanRestoreParents(configuration, action.PreviousAssetParentGroupIds) ||
                !CanRestoreGroupParents(
                    configuration,
                    action.PreviousGroupParentGroupIds))
            {
                return false;
            }
            if (assets.Count == 0 && groups.Count == 0)
            {
                return false;
            }

            var currentParents = CaptureParents(assets.Values);
            var currentAssetOrders = CaptureOrders(assets.Values);
            var currentGroupOrders = groups.ToDictionary(
                item => item.Key,
                item => item.Value.SortOrder,
                StringComparer.Ordinal);
            var currentGroupParents = CaptureGroupParents(groups.Values);
            RestoreAssetStructure(
                assets,
                action.PreviousAssetParentGroupIds,
                action.PreviousAssetSortOrders);
            RestoreGroupStructure(
                groups,
                action.PreviousGroupParentGroupIds,
                action.PreviousAssetGroupSortOrders);

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () =>
                {
                    RestoreAssetStructure(assets, currentParents, currentAssetOrders);
                    RestoreGroupStructure(
                        groups,
                        currentGroupParents,
                        currentGroupOrders);
                });
            return true;
        }

        private static bool TryUndoFavorite(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (!TryResolveAssetsForSnapshot(configuration, action, out var assets) ||
                !TryResolveGroupsForSnapshot(configuration, action, out var groups))
            {
                return false;
            }

            Asset? asset = null;
            AssetGroup? group = null;
            bool previousFavorite;
            if (action.Type == UndoActionType.AssetFavoriteChanged)
            {
                if (string.IsNullOrWhiteSpace(action.AssetId) ||
                    !action.PreviousFavoriteState.HasValue)
                {
                    return false;
                }
                asset = configuration.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, action.AssetId, StringComparison.Ordinal));
                if (asset == null || asset.IsSystem)
                {
                    return false;
                }
                previousFavorite = action.PreviousFavoriteState.Value;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(action.GroupId) ||
                    !action.PreviousGroupFavoriteState.HasValue)
                {
                    return false;
                }
                group = configuration.AssetGroups.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, action.GroupId, StringComparison.Ordinal));
                if (group == null)
                {
                    return false;
                }
                previousFavorite = action.PreviousGroupFavoriteState.Value;
            }

            var currentFavorite = asset?.IsFavorite ?? group!.IsFavorite;
            var currentAssetOrders = CaptureOrders(assets.Values);
            var currentGroupOrders = groups.ToDictionary(
                item => item.Key,
                item => item.Value.SortOrder,
                StringComparer.Ordinal);
            if (asset != null)
            {
                asset.IsFavorite = previousFavorite;
            }
            else
            {
                group!.IsFavorite = previousFavorite;
            }
            RestoreAssetStructure(assets, parents: null, action.PreviousAssetSortOrders);
            RestoreGroupOrders(groups, action.PreviousAssetGroupSortOrders);

            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () =>
                {
                    if (asset != null)
                    {
                        asset.IsFavorite = currentFavorite;
                    }
                    else
                    {
                        group!.IsFavorite = currentFavorite;
                    }
                    RestoreAssetStructure(assets, parents: null, currentAssetOrders);
                    RestoreGroupOrders(groups, currentGroupOrders);
                });
            return true;
        }

        private static bool TryUndoGroupRename(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (string.IsNullOrWhiteSpace(action.GroupId) ||
                string.IsNullOrWhiteSpace(action.PreviousGroupName) ||
                action.PreviousGroupName.Length > GamAssetDocumentCodec.MaximumAssetNameLength ||
                action.PreviousGroupName.Any(char.IsControl))
            {
                return false;
            }
            var group = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, action.GroupId, StringComparison.Ordinal));
            if (group == null ||
                configuration.Assets.Any(asset => string.Equals(
                    asset.Name,
                    action.PreviousGroupName,
                    StringComparison.OrdinalIgnoreCase)) ||
                configuration.AssetGroups.Any(candidate =>
                    !ReferenceEquals(candidate, group) &&
                    string.Equals(
                        candidate.Name,
                        action.PreviousGroupName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var currentName = group.Name;
            group.Name = action.PreviousGroupName;
            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () => group.Name = currentName);
            return true;
        }

        private static bool TryUndoGroupImageReference(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (string.IsNullOrWhiteSpace(action.GroupId))
            {
                return false;
            }
            var group = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, action.GroupId, StringComparison.Ordinal));
            if (group == null)
            {
                return false;
            }

            var currentPath = group.ImagePath;
            group.ImagePath = action.PreviousGroupImagePath;
            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () => group.ImagePath = currentPath);
            return true;
        }

        private static bool TryUndoDepthLimit(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (!action.PreviousMaxNestedGroupDepth.HasValue)
            {
                return false;
            }
            var previous = action.PreviousMaxNestedGroupDepth.Value;
            if (previous < Configuration.MinimumNestedGroupDepth ||
                previous > Configuration.MaximumNestedGroupDepth ||
                previous < configuration.AssetGroups
                    .Select(group => GetGroupDepth(configuration, group.Id))
                    .DefaultIfEmpty(0)
                    .Max())
            {
                return false;
            }

            var current = configuration.MaxNestedGroupDepth;
            configuration.MaxNestedGroupDepth = previous;
            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () => configuration.MaxNestedGroupDepth = current);
            return true;
        }

        private static bool TryUndoMemo(
            Configuration configuration,
            UndoAction action,
            out AssetGroupUndoMutation? mutation)
        {
            mutation = null;
            if (action.Type == UndoActionType.AssetMemoChanged)
            {
                if (string.IsNullOrWhiteSpace(action.AssetId))
                {
                    return false;
                }
                var asset = configuration.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, action.AssetId, StringComparison.Ordinal));
                if (asset == null || IsProtectedGroupAsset(asset))
                {
                    return false;
                }

                var current = asset.Memo;
                asset.Memo = action.PreviousMemo ?? string.Empty;
                mutation = new AssetGroupUndoMutation(
                    requiresRuntimeReconcile: false,
                    rollback: () => asset.Memo = current);
                return true;
            }

            if (string.IsNullOrWhiteSpace(action.GroupId))
            {
                return false;
            }
            var group = configuration.AssetGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, action.GroupId, StringComparison.Ordinal));
            if (group == null)
            {
                return false;
            }

            var currentMemo = group.Memo;
            group.Memo = action.PreviousMemo ?? string.Empty;
            mutation = new AssetGroupUndoMutation(
                requiresRuntimeReconcile: false,
                rollback: () => group.Memo = currentMemo);
            return true;
        }

        private static bool TryResolveAssetsForSnapshot(
            Configuration configuration,
            UndoAction action,
            out Dictionary<string, Asset> assets)
        {
            assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
            var ids = (action.PreviousAssetParentGroupIds?.Keys ?? Enumerable.Empty<string>())
                .Concat(action.PreviousAssetSortOrders?.Keys ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var asset = configuration.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (asset == null)
                {
                    return false;
                }
                assets[id] = asset;
            }
            return true;
        }

        private static bool TryResolveGroupsForSnapshot(
            Configuration configuration,
            UndoAction action,
            out Dictionary<string, AssetGroup> groups)
        {
            groups = new Dictionary<string, AssetGroup>(StringComparer.Ordinal);
            var ids = (action.PreviousAssetGroupSortOrders?.Keys ??
                       Enumerable.Empty<string>())
                .Concat(action.PreviousGroupParentGroupIds?.Keys ??
                    Enumerable.Empty<string>())
                .Concat(action.PreviousGroupDefaultStates?.Keys ??
                    Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var group = configuration.AssetGroups.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (group == null)
                {
                    return false;
                }
                groups[id] = group;
            }
            return true;
        }

        private static bool CanRestoreGroupParents(
            Configuration configuration,
            IReadOnlyDictionary<string, string?>? parents,
            IEnumerable<AssetGroup>? additionalGroups = null,
            string? forbiddenGroupId = null)
        {
            if (parents == null)
            {
                return true;
            }

            var allGroups = configuration.AssetGroups
                .Where(group => !string.Equals(
                    group.Id,
                    forbiddenGroupId,
                    StringComparison.Ordinal))
                .Concat(additionalGroups ?? Array.Empty<AssetGroup>())
                .GroupBy(group => group.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToDictionary(group => group.Id, StringComparer.Ordinal);
            if (parents.Keys.Any(id => !allGroups.ContainsKey(id)) ||
                parents.Values.Any(parent =>
                    !string.IsNullOrWhiteSpace(parent) &&
                    !allGroups.ContainsKey(parent)))
            {
                return false;
            }

            var effectiveParents = allGroups.ToDictionary(
                item => item.Key,
                item => parents.TryGetValue(item.Key, out var parent)
                    ? parent
                    : item.Value.ParentGroupId,
                StringComparer.Ordinal);
            if (effectiveParents.Values.Any(parent =>
                    !string.IsNullOrWhiteSpace(parent) &&
                    !effectiveParents.ContainsKey(parent)))
            {
                return false;
            }
            foreach (var id in effectiveParents.Keys)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = id;
                var depth = 0;
                while (effectiveParents.TryGetValue(current, out var parent) &&
                       !string.IsNullOrWhiteSpace(parent))
                {
                    if (!visited.Add(current) ||
                        string.Equals(current, parent, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    current = parent;
                    depth++;
                    if (depth > configuration.MaxNestedGroupDepth)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool CanRestoreParents(
            Configuration configuration,
            IReadOnlyDictionary<string, string?>? parents,
            string? forbiddenGroupId = null)
        {
            if (parents == null)
            {
                return true;
            }

            var validGroups = new HashSet<string>(
                configuration.AssetGroups
                    .Where(group => !string.Equals(
                        group.Id,
                        forbiddenGroupId,
                        StringComparison.Ordinal))
                    .Select(group => group.Id),
                StringComparer.Ordinal);
            return parents.Values.All(parent =>
                string.IsNullOrWhiteSpace(parent) || validGroups.Contains(parent));
        }

        private static bool CanRestoreParentsWithAdditionalGroup(
            Configuration configuration,
            IReadOnlyDictionary<string, string?>? parents,
            string additionalGroupId)
        {
            return CanRestoreParentsWithAdditionalGroups(
                configuration,
                parents,
                new[] { additionalGroupId });
        }

        private static bool CanRestoreParentsWithAdditionalGroups(
            Configuration configuration,
            IReadOnlyDictionary<string, string?>? parents,
            IEnumerable<string> additionalGroupIds)
        {
            if (parents == null)
            {
                return true;
            }
            var validGroups = new HashSet<string>(
                configuration.AssetGroups.Select(group => group.Id),
                StringComparer.Ordinal);
            validGroups.UnionWith(additionalGroupIds);
            return parents.Values.All(parent =>
                string.IsNullOrWhiteSpace(parent) || validGroups.Contains(parent));
        }

        private static Dictionary<string, string?> CaptureParents(
            IEnumerable<Asset> assets)
        {
            return assets.ToDictionary(
                asset => asset.Id,
                asset => asset.ParentGroupId,
                StringComparer.Ordinal);
        }

        private static Dictionary<string, int> CaptureOrders(IEnumerable<Asset> assets)
        {
            return assets.ToDictionary(
                asset => asset.Id,
                asset => asset.SortOrder,
                StringComparer.Ordinal);
        }

        private static Dictionary<string, string?> CaptureGroupParents(
            IEnumerable<AssetGroup> groups)
        {
            return groups.ToDictionary(
                group => group.Id,
                group => group.ParentGroupId,
                StringComparer.Ordinal);
        }

        private static Dictionary<string, int> CaptureGroupOrders(
            IEnumerable<AssetGroup> groups)
        {
            return groups.ToDictionary(
                group => group.Id,
                group => group.SortOrder,
                StringComparer.Ordinal);
        }

        private static Dictionary<string, AddonState> CaptureGroupDefaults(
            IEnumerable<AssetGroup> groups)
        {
            return groups.ToDictionary(
                group => group.Id,
                group => group.DefaultChildState,
                StringComparer.Ordinal);
        }

        private static void RestoreAssetStructure(
            IReadOnlyDictionary<string, Asset> assets,
            IReadOnlyDictionary<string, string?>? parents,
            IReadOnlyDictionary<string, int>? orders)
        {
            if (parents != null)
            {
                foreach (var item in parents)
                {
                    assets[item.Key].ParentGroupId = item.Value;
                }
            }
            if (orders != null)
            {
                foreach (var item in orders)
                {
                    assets[item.Key].SortOrder = item.Value;
                }
            }
        }

        private static void RestoreGroupOrders(
            IReadOnlyDictionary<string, AssetGroup> groups,
            IReadOnlyDictionary<string, int>? orders)
        {
            if (orders == null)
            {
                return;
            }
            foreach (var item in orders)
            {
                groups[item.Key].SortOrder = item.Value;
            }
        }

        private static void RestoreGroupStructure(
            IReadOnlyDictionary<string, AssetGroup> groups,
            IReadOnlyDictionary<string, string?>? parents,
            IReadOnlyDictionary<string, int>? orders)
        {
            if (parents != null)
            {
                foreach (var item in parents)
                {
                    groups[item.Key].ParentGroupId = item.Value;
                }
            }
            RestoreGroupOrders(groups, orders);
        }

        private static void RestoreGroupDefaults(
            IReadOnlyDictionary<string, AssetGroup> groups,
            IReadOnlyDictionary<string, AddonState>? defaults)
        {
            if (defaults == null)
            {
                return;
            }
            foreach (var item in defaults)
            {
                groups[item.Key].DefaultChildState = item.Value;
            }
        }

        private static bool NormalizeGroupIdentities(Configuration configuration)
        {
            var changed = false;
            var usedIds = new HashSet<string>(
                configuration.Assets
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Id))
                    .Select(asset => asset.Id),
                StringComparer.Ordinal);
            var firstGroupByOriginalId = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in configuration.AssetGroups)
            {
                var originalId = group.Id?.Trim() ?? string.Empty;
                var normalizedId = originalId;
                var collidesWithAsset = usedIds.Contains(normalizedId);
                var duplicateGroup = !string.IsNullOrWhiteSpace(normalizedId) &&
                                     !firstGroupByOriginalId.Add(normalizedId);
                if (string.IsNullOrWhiteSpace(normalizedId) ||
                    collidesWithAsset ||
                    duplicateGroup)
                {
                    normalizedId = CreateUniqueEntityId(configuration, usedIds);
                }

                if (!string.Equals(group.Id, normalizedId, StringComparison.Ordinal))
                {
                    // A collision with an Asset can still be unambiguously a
                    // parent reference because only Group IDs are valid there.
                    if (!string.IsNullOrWhiteSpace(originalId) && !duplicateGroup)
                    {
                        foreach (var asset in configuration.Assets.Where(asset =>
                                     string.Equals(
                                         asset.ParentGroupId,
                                         originalId,
                                         StringComparison.Ordinal)))
                        {
                            asset.ParentGroupId = normalizedId;
                        }
                        foreach (var childGroup in configuration.AssetGroups.Where(candidate =>
                                     !ReferenceEquals(candidate, group) &&
                                     string.Equals(
                                         candidate.ParentGroupId,
                                         originalId,
                                         StringComparison.Ordinal)))
                        {
                            childGroup.ParentGroupId = normalizedId;
                        }
                    }
                    group.Id = normalizedId;
                    changed = true;
                }
                usedIds.Add(normalizedId);
            }
            return changed;
        }

        private static bool NormalizeGroupHierarchy(
            Configuration configuration,
            ISet<string> validGroupIds)
        {
            var changed = false;
            foreach (var group in configuration.AssetGroups)
            {
                var parent = group.ParentGroupId?.Trim();
                var normalizedParent = string.IsNullOrWhiteSpace(parent) ||
                                       !validGroupIds.Contains(parent) ||
                                       string.Equals(parent, group.Id, StringComparison.Ordinal)
                    ? null
                    : parent;
                if (!string.Equals(
                        group.ParentGroupId,
                        normalizedParent,
                        StringComparison.Ordinal))
                {
                    group.ParentGroupId = normalizedParent;
                    changed = true;
                }
            }

            while (TryFindCycleGroup(configuration, out var cycleGroup))
            {
                cycleGroup!.ParentGroupId = null;
                changed = true;
            }

            // A lower saved setting must never flatten an otherwise valid tree.
            // Preserve the topology and raise the setting up to the hard product
            // maximum. Only structurally invalid depth beyond that hard maximum
            // is repaired below.
            var actualDepth = configuration.AssetGroups
                .Select(group => GetGroupDepth(configuration, group.Id))
                .DefaultIfEmpty(0)
                .Max();
            var requiredSupportedDepth = Math.Min(
                actualDepth,
                Configuration.MaximumNestedGroupDepth);
            if (configuration.MaxNestedGroupDepth < requiredSupportedDepth)
            {
                configuration.MaxNestedGroupDepth = requiredSupportedDepth;
                changed = true;
            }

            while (true)
            {
                var overDepth = configuration.AssetGroups
                    .Select(group => new
                    {
                        Group = group,
                        Depth = GetGroupDepth(configuration, group.Id)
                    })
                    .Where(item => item.Depth > configuration.MaxNestedGroupDepth)
                    .OrderBy(item => item.Depth)
                    .ThenBy(item => item.Group.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (overDepth == null)
                {
                    break;
                }

                overDepth.Group.ParentGroupId = null;
                changed = true;
            }

            return changed;
        }

        private static bool TryFindCycleGroup(
            Configuration configuration,
            out AssetGroup? cycleGroup)
        {
            var groupsById = configuration.AssetGroups.ToDictionary(
                group => group.Id,
                StringComparer.Ordinal);
            foreach (var start in configuration.AssetGroups
                         .OrderBy(group => group.Id, StringComparer.Ordinal))
            {
                var path = new List<AssetGroup>();
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                var current = start;
                while (true)
                {
                    if (positions.TryGetValue(current.Id, out var cycleStart))
                    {
                        cycleGroup = path
                            .Skip(cycleStart)
                            .OrderByDescending(group => group.Id, StringComparer.Ordinal)
                            .First();
                        return true;
                    }

                    positions[current.Id] = path.Count;
                    path.Add(current);
                    if (string.IsNullOrWhiteSpace(current.ParentGroupId) ||
                        !groupsById.TryGetValue(current.ParentGroupId, out current))
                    {
                        break;
                    }
                }
            }

            cycleGroup = null;
            return false;
        }

        private static bool NormalizeGlobalNames(Configuration configuration)
        {
            var changed = false;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in configuration.Assets
                         .Where(asset => asset.IsSystem)
                         .OrderBy(GetSystemAssetRank)
                         .ThenBy(asset => asset.Id, StringComparer.Ordinal)
                         .Concat(configuration.Assets.Where(asset => !asset.IsSystem)))
            {
                var normalized = MakeUniqueName(asset.Name, "Asset", used);
                if (!string.Equals(asset.Name, normalized, StringComparison.Ordinal))
                {
                    asset.Name = normalized;
                    changed = true;
                }
            }
            foreach (var group in configuration.AssetGroups)
            {
                var normalized = MakeUniqueName(group.Name, "Asset Group", used);
                if (!string.Equals(group.Name, normalized, StringComparison.Ordinal))
                {
                    group.Name = normalized;
                    changed = true;
                }
            }
            return changed;
        }

        private static string MakeUniqueName(
            string? source,
            string fallback,
            ISet<string> used)
        {
            var baseName = SanitizePortableName(source, fallback);
            var candidate = baseName;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                var suffixText = $" ({suffix})";
                var prefixLength = GamAssetDocumentCodec.MaximumAssetNameLength - suffixText.Length;
                var prefix = TruncateAtTextElementBoundary(baseName, prefixLength)
                    .TrimEnd();
                if (prefix.Length == 0)
                {
                    prefix = TruncateAtTextElementBoundary(fallback, prefixLength);
                }
                candidate = prefix + suffixText;
                suffix++;
            }
            return candidate;
        }

        private static string SanitizePortableName(string? source, string fallback)
        {
            var sanitized = new string((source ?? string.Empty)
                .Select(character => char.IsControl(character) ? ' ' : character)
                .ToArray())
                .Trim();
            if (sanitized.Length == 0)
            {
                sanitized = fallback;
            }
            if (sanitized.Length > GamAssetDocumentCodec.MaximumAssetNameLength)
            {
                sanitized = TruncateAtTextElementBoundary(
                        sanitized,
                        GamAssetDocumentCodec.MaximumAssetNameLength)
                    .TrimEnd();
            }
            return sanitized.Length == 0 ? fallback : sanitized;
        }

        private static string TruncateAtTextElementBoundary(
            string value,
            int maximumLength)
        {
            if (value.Length <= maximumLength)
            {
                return value;
            }

            var boundaries = StringInfo.ParseCombiningCharacters(value);
            var acceptedLength = 0;
            for (var index = 0; index < boundaries.Length; index++)
            {
                var elementEnd = index + 1 < boundaries.Length
                    ? boundaries[index + 1]
                    : value.Length;
                if (elementEnd > maximumLength)
                {
                    break;
                }
                acceptedLength = elementEnd;
            }

            return value.Substring(0, acceptedLength);
        }

        private static bool NormalizeContainerOrders(
            Configuration configuration,
            string? parentGroupId,
            bool legacyVisibleOrder)
        {
            var entries = GetContainerEntries(configuration, parentGroupId);
            IOrderedEnumerable<OrderEntry> ordered;
            if (legacyVisibleOrder)
            {
                ordered = entries
                    .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                    .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(entry => entry.Kind)
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal);
            }
            else
            {
                ordered = entries
                    .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                    .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Kind)
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal);
            }

            var changed = false;
            foreach (var band in ordered.GroupBy(entry => entry.IsFavorite))
            {
                var sortOrder = 0;
                foreach (var entry in band)
                {
                    if (entry.SortOrder != sortOrder)
                    {
                        entry.SortOrder = sortOrder;
                        changed = true;
                    }
                    sortOrder++;
                }
            }
            return changed;
        }

        private static IEnumerable<OrderEntry> GetContainerEntries(
            Configuration configuration,
            string? parentGroupId)
        {
            var assets = configuration.Assets
                .Where(asset => !asset.IsSystem)
                .Where(asset => string.Equals(
                    asset.ParentGroupId,
                    parentGroupId,
                    StringComparison.Ordinal))
                .Select(asset => new OrderEntry(asset));
            var groups = configuration.AssetGroups
                .Where(group => string.Equals(
                    group.ParentGroupId,
                    parentGroupId,
                    StringComparison.Ordinal))
                .Select(group => new OrderEntry(group));
            return assets.Concat(groups);
        }

        private static IEnumerable<Asset> GetChildren(
            Configuration configuration,
            string groupId)
        {
            return configuration.Assets.Where(asset =>
                !asset.IsSystem &&
                string.Equals(asset.ParentGroupId, groupId, StringComparison.Ordinal));
        }

        private static IEnumerable<AssetGroup> GetChildGroups(
            Configuration configuration,
            string groupId)
        {
            return configuration.AssetGroups.Where(group =>
                string.Equals(group.ParentGroupId, groupId, StringComparison.Ordinal));
        }

        private static IEnumerable<AssetGroup> GetGroupSubtree(
            Configuration configuration,
            string rootGroupId)
        {
            var root = GetGroup(configuration, rootGroupId);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<AssetGroup>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current.Id))
                {
                    throw new InvalidOperationException(
                        "The Asset Group hierarchy contains a cycle.");
                }

                yield return current;
                var children = OrderGroups(GetChildGroups(configuration, current.Id))
                    .Reverse()
                    .ToList();
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }

        private static AssetGroupDisplayState GetDisplayState(
            Configuration configuration,
            AssetGroup group,
            ISet<string> visiting)
        {
            if (!visiting.Add(group.Id))
            {
                throw new InvalidOperationException(
                    "The Asset Group hierarchy contains a cycle.");
            }

            try
            {
                var states = GetChildren(configuration, group.Id)
                    .Select(child => ToDisplayState(child.GetWholeState()))
                    .Concat(GetChildGroups(configuration, group.Id)
                        .Select(child => GetDisplayState(configuration, child, visiting)))
                    .ToList();
                if (states.Count == 0)
                {
                    return ToDisplayState(group.DefaultChildState);
                }
                if (states.Any(state => state == AssetGroupDisplayState.Mixed))
                {
                    return AssetGroupDisplayState.Mixed;
                }

                var first = states[0];
                return states.All(state => state == first)
                    ? first
                    : AssetGroupDisplayState.Mixed;
            }
            finally
            {
                visiting.Remove(group.Id);
            }
        }

        private static int GetGroupDepth(
            Configuration configuration,
            string groupId)
        {
            var current = GetGroup(configuration, groupId);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var depth = 0;
            while (!string.IsNullOrWhiteSpace(current.ParentGroupId))
            {
                if (!visited.Add(current.Id))
                {
                    throw new InvalidOperationException(
                        "The Asset Group hierarchy contains a cycle.");
                }
                current = GetGroup(configuration, current.ParentGroupId);
                depth = checked(depth + 1);
            }
            return depth;
        }

        private static int GetSubtreeHeight(
            Configuration configuration,
            string groupId)
        {
            return GetSubtreeHeight(
                configuration,
                GetGroup(configuration, groupId),
                new HashSet<string>(StringComparer.Ordinal));
        }

        private static int GetSubtreeHeight(
            Configuration configuration,
            AssetGroup group,
            ISet<string> visiting)
        {
            if (!visiting.Add(group.Id))
            {
                throw new InvalidOperationException(
                    "The Asset Group hierarchy contains a cycle.");
            }
            try
            {
                var childHeights = GetChildGroups(configuration, group.Id)
                    .Select(child => checked(
                        1 + GetSubtreeHeight(configuration, child, visiting)))
                    .ToList();
                return childHeights.Count == 0 ? 0 : childHeights.Max();
            }
            finally
            {
                visiting.Remove(group.Id);
            }
        }

        private static void EnsureCanMoveGroup(
            Configuration configuration,
            AssetGroup group,
            AssetGroup? destination)
        {
            if (destination != null)
            {
                if (string.Equals(group.Id, destination.Id, StringComparison.Ordinal) ||
                    GetGroupSubtree(configuration, group.Id).Any(candidate =>
                        string.Equals(candidate.Id, destination.Id, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        "An Asset Group cannot be moved into itself or one of its descendants.");
                }
            }

            var destinationDepth = destination == null
                ? 0
                : checked(GetGroupDepth(configuration, destination.Id) + 1);
            EnsureDepthAllowed(
                configuration,
                destinationDepth,
                GetSubtreeHeight(configuration, group.Id));
        }

        private static void EnsureDepthAllowed(
            Configuration configuration,
            int rootDepth,
            int subtreeHeight)
        {
            ValidateMaxNestedGroupDepth(configuration.MaxNestedGroupDepth);
            var deepest = checked(rootDepth + subtreeHeight);
            if (deepest > configuration.MaxNestedGroupDepth)
            {
                throw new InvalidOperationException(
                    $"The operation would create Asset Group depth {deepest}, " +
                    $"above the configured maximum {configuration.MaxNestedGroupDepth}.");
            }
        }

        private static void ValidateMaxNestedGroupDepth(int value)
        {
            if (value < Configuration.MinimumNestedGroupDepth ||
                value > Configuration.MaximumNestedGroupDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Maximum nested Asset Group depth must be between " +
                    $"{Configuration.MinimumNestedGroupDepth} and " +
                    $"{Configuration.MaximumNestedGroupDepth}.");
            }
        }

        private static IOrderedEnumerable<Asset> OrderAssets(IEnumerable<Asset> assets)
        {
            return assets
                .OrderBy(asset => asset.IsFavorite ? 0 : 1)
                .ThenBy(asset => NormalizeSortOrder(asset.SortOrder))
                .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.Id, StringComparer.Ordinal);
        }

        private static IOrderedEnumerable<AssetGroup> OrderGroups(
            IEnumerable<AssetGroup> groups)
        {
            return groups
                .OrderBy(group => group.IsFavorite ? 0 : 1)
                .ThenBy(group => NormalizeSortOrder(group.SortOrder))
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Id, StringComparer.Ordinal);
        }

        private static void AssignEntriesToEmptyGroup(
            IEnumerable<Asset> assets,
            IEnumerable<AssetGroup> groups,
            string groupId)
        {
            var entries = assets.Select(asset => new OrderEntry(asset))
                .Concat(groups.Select(group => new OrderEntry(group)))
                .OrderBy(entry => entry.IsFavorite ? 0 : 1)
                .ThenBy(entry => NormalizeSortOrder(entry.SortOrder))
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();
            var favoriteOrder = 0;
            var normalOrder = 0;
            foreach (var entry in entries)
            {
                entry.ParentGroupId = groupId;
                entry.SortOrder = entry.IsFavorite
                    ? favoriteOrder++
                    : normalOrder++;
            }
        }

        private static void AssignSequentialOrders(IEnumerable<OrderEntry> entries)
        {
            var favoriteOrder = 0;
            var normalOrder = 0;
            foreach (var entry in entries)
            {
                entry.SortOrder = entry.IsFavorite
                    ? favoriteOrder++
                    : normalOrder++;
            }
        }

        private static int GetNextSortOrder(
            Configuration configuration,
            string? parentGroupId,
            bool isFavorite,
            string? exceptAssetId = null,
            string? exceptGroupId = null)
        {
            var values = GetContainerEntries(configuration, parentGroupId)
                .Where(entry => entry.IsFavorite == isFavorite)
                .Where(entry =>
                    !(entry.Kind == AssetListEntryKind.Asset &&
                      string.Equals(entry.Id, exceptAssetId, StringComparison.Ordinal)))
                .Where(entry =>
                    !(entry.Kind == AssetListEntryKind.Group &&
                      string.Equals(entry.Id, exceptGroupId, StringComparison.Ordinal)))
                .Select(entry => NormalizeSortOrder(entry.SortOrder))
                .Where(value => value != int.MaxValue)
                .ToList();
            if (values.Count == 0)
            {
                return 0;
            }

            var maximum = values.Max();
            // A malformed/future profile must not turn a reversible operation
            // into a partial mutation through integer overflow. Normal startup
            // normalization compacts these values; this is a final fail-safe.
            return maximum >= int.MaxValue - 1 ? values.Count : maximum + 1;
        }

        private static Dictionary<string, int> CaptureAssetOrders(
            IEnumerable<OrderEntry> entries)
        {
            return entries
                .Where(entry => entry.Asset != null)
                .ToDictionary(
                    entry => entry.Id,
                    entry => entry.SortOrder,
                    StringComparer.Ordinal);
        }

        private static Dictionary<string, int> CaptureGroupOrders(
            IEnumerable<OrderEntry> entries)
        {
            return entries
                .Where(entry => entry.Group != null)
                .ToDictionary(
                    entry => entry.Id,
                    entry => entry.SortOrder,
                    StringComparer.Ordinal);
        }

        private static IEnumerable<Asset> ResolveDistinctCustomAssets(
            Configuration configuration,
            IEnumerable<string>? assetIds)
        {
            RequireConfiguration(configuration);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawId in assetIds ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    throw new ArgumentException("Asset identity cannot be empty.", nameof(assetIds));
                }
                var id = rawId.Trim();
                if (seen.Add(id))
                {
                    yield return GetCustomAsset(configuration, id);
                }
            }
        }

        private static IEnumerable<AssetGroup> ResolveDistinctGroups(
            Configuration configuration,
            IEnumerable<string>? groupIds)
        {
            RequireConfiguration(configuration);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawId in groupIds ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    throw new ArgumentException(
                        "Asset Group identity cannot be empty.",
                        nameof(groupIds));
                }
                var id = rawId.Trim();
                if (seen.Add(id))
                {
                    yield return GetGroup(configuration, id);
                }
            }
        }

        private static Asset GetCustomAsset(Configuration configuration, string assetId)
        {
            RequireConfiguration(configuration);
            var asset = configuration.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, assetId, StringComparison.Ordinal));
            if (asset == null)
            {
                throw new InvalidOperationException($"Asset not found: {assetId}");
            }
            if (IsProtectedGroupAsset(asset))
            {
                throw new InvalidOperationException("System Assets cannot belong to an Asset Group.");
            }
            return asset;
        }

        private static bool IsProtectedGroupAsset(Asset asset)
        {
            return asset.IsSystem ||
                   string.Equals(
                       asset.Id,
                       SystemAssetDefinitions.SubscribeId,
                       StringComparison.Ordinal) ||
                   GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(asset.Id);
        }

        private static AssetGroup GetGroup(Configuration configuration, string groupId)
        {
            RequireConfiguration(configuration);
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("Asset Group identity cannot be empty.", nameof(groupId));
            }

            return configuration.AssetGroups.FirstOrDefault(group =>
                       string.Equals(group.Id, groupId.Trim(), StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"Asset Group not found: {groupId}");
        }

        private static void RequireConfiguration(Configuration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }
            configuration.Assets ??= new List<Asset>();
            configuration.AssetGroups ??= new List<AssetGroup>();
        }

        private static string CreateUniqueEntityId(Configuration configuration)
        {
            var used = new HashSet<string>(
                configuration.Assets.Select(asset => asset.Id)
                    .Concat(configuration.AssetGroups.Select(group => group.Id)),
                StringComparer.Ordinal);
            return CreateUniqueEntityId(configuration, used);
        }

        private static string CreateUniqueEntityId(
            Configuration configuration,
            ISet<string> usedIds)
        {
            string candidate;
            do
            {
                candidate = Guid.NewGuid().ToString();
            }
            while (usedIds.Contains(candidate));
            return candidate;
        }

        private static int GetSystemAssetRank(Asset asset)
        {
            return asset.Id switch
            {
                SystemAssetDefinitions.SubscribeId => 0,
                SystemAssetDefinitions.GmodDisabledId => 1,
                _ => 2
            };
        }

        private static int NormalizeSortOrder(int value)
        {
            return value < 0 ? int.MaxValue : value;
        }

        private static AssetGroupDisplayState ToDisplayState(AddonState state)
        {
            return state switch
            {
                AddonState.Enabled => AssetGroupDisplayState.Enabled,
                AddonState.Disabled => AssetGroupDisplayState.Disabled,
                AddonState.Excluded => AssetGroupDisplayState.Excluded,
                _ => AssetGroupDisplayState.Mixed
            };
        }

        private sealed class OrderEntry
        {
            public OrderEntry(Asset asset)
            {
                Asset = asset;
                Kind = AssetListEntryKind.Asset;
            }

            public OrderEntry(AssetGroup group)
            {
                Group = group;
                Kind = AssetListEntryKind.Group;
            }

            public Asset? Asset { get; }
            public AssetGroup? Group { get; }
            public AssetListEntryKind Kind { get; }
            public string Id => Asset?.Id ?? Group!.Id;
            public string Name => Asset?.Name ?? Group!.Name;

            public bool IsFavorite
            {
                get => Asset?.IsFavorite ?? Group!.IsFavorite;
                set
                {
                    if (Asset != null)
                    {
                        Asset.IsFavorite = value;
                    }
                    else
                    {
                        Group!.IsFavorite = value;
                    }
                }
            }

            public int SortOrder
            {
                get => Asset?.SortOrder ?? Group!.SortOrder;
                set
                {
                    if (Asset != null)
                    {
                        Asset.SortOrder = value;
                    }
                    else
                    {
                        Group!.SortOrder = value;
                    }
                }
            }

            public string? ParentGroupId
            {
                get => Asset?.ParentGroupId ?? Group!.ParentGroupId;
                set
                {
                    if (Asset != null)
                    {
                        Asset.ParentGroupId = value;
                    }
                    else
                    {
                        Group!.ParentGroupId = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Result of an already-applied configuration-only Undo mutation.
    /// </summary>
    public sealed class AssetGroupUndoMutation
    {
        internal AssetGroupUndoMutation(
            bool requiresRuntimeReconcile,
            Action rollback,
            AssetGroup? removedGroup = null,
            AssetGroup? restoredGroup = null)
        {
            RequiresRuntimeReconcile = requiresRuntimeReconcile;
            Rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
            RemovedGroup = removedGroup;
            RestoredGroup = restoredGroup;
        }

        public bool RequiresRuntimeReconcile { get; }
        public Action Rollback { get; }
        public AssetGroup? RemovedGroup { get; }
        public AssetGroup? RestoredGroup { get; }
    }
}
