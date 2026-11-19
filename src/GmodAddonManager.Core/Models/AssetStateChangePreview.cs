using System;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    public sealed class AssetStateChangePreview
    {
        private readonly (string Id, AddonState Before, AddonState After)[] assets;
        private readonly (string Id, AddonState Before, AddonState After)[] groupDefaults;
        private readonly string[]? enabledIds;
        private readonly string[]? disabledIds;

        internal AssetStateChangePreview(
            IEnumerable<(string Id, AddonState Before, AddonState After)> assets,
            IEnumerable<(string Id, AddonState Before, AddonState After)> groupDefaults,
            IEnumerable<string>? enabledIds,
            IEnumerable<string>? disabledIds)
        {
            this.assets = assets.OrderBy(change => change.Id, StringComparer.Ordinal).ToArray();
            this.groupDefaults = groupDefaults.OrderBy(change => change.Id, StringComparer.Ordinal).ToArray();
            this.enabledIds = enabledIds?.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            this.disabledIds = disabledIds?.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }

        public int Assets => assets.Count(change => change.Before != change.After);
        public int GroupDefaults => groupDefaults.Count(change => change.Before != change.After);
        public int ExclusionsToNeutral => assets.Count(change =>
            change.Before == AddonState.Excluded && change.After == AddonState.Disabled);
        public int? On => enabledIds?.Length;
        public int? Off => disabledIds?.Length;

        internal bool Matches(AssetStateChangePreview other)
        {
            return assets.SequenceEqual(other.assets) && groupDefaults.SequenceEqual(other.groupDefaults) &&
                SameIds(enabledIds, other.enabledIds) && SameIds(disabledIds, other.disabledIds);
        }

        private static bool SameIds(string[]? left, string[]? right) =>
            left == null ? right == null : right != null && left.SequenceEqual(right, StringComparer.Ordinal);
    }
}
