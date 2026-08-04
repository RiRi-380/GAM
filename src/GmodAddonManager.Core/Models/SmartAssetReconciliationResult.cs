using System;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    public sealed class SmartAssetMembershipChange
    {
        public SmartAssetMembershipChange(
            string assetId,
            IEnumerable<string>? addedAddonIds,
            IEnumerable<string>? removedAddonIds,
            IEnumerable<string>? unknownAddonIds,
            bool isFrozen,
            string? message)
        {
            AssetId = assetId ?? string.Empty;
            AddedAddonIds = Normalize(addedAddonIds);
            RemovedAddonIds = Normalize(removedAddonIds);
            UnknownAddonIds = Normalize(unknownAddonIds);
            IsFrozen = isFrozen;
            Message = message;
        }

        public string AssetId { get; }
        public IReadOnlyList<string> AddedAddonIds { get; }
        public IReadOnlyList<string> RemovedAddonIds { get; }
        public IReadOnlyList<string> UnknownAddonIds { get; }
        public bool IsFrozen { get; }
        public string? Message { get; }
        public bool MembershipChanged =>
            AddedAddonIds.Count > 0 || RemovedAddonIds.Count > 0;

        private static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public sealed class SmartAssetReconciliationResult
    {
        public SmartAssetReconciliationResult(
            bool isAuthoritative,
            IEnumerable<SmartAssetMembershipChange>? assets,
            bool configurationChanged = false)
        {
            IsAuthoritative = isAuthoritative;
            Assets = (assets ?? Array.Empty<SmartAssetMembershipChange>()).ToArray();
            ConfigurationChanged = configurationChanged ||
                                   Assets.Any(asset => asset.MembershipChanged);
        }

        public bool IsAuthoritative { get; }
        public IReadOnlyList<SmartAssetMembershipChange> Assets { get; }
        public bool ConfigurationChanged { get; }
        public bool MembershipChanged => Assets.Any(asset => asset.MembershipChanged);
        public int AddedCount => Assets.Sum(asset => asset.AddedAddonIds.Count);
        public int RemovedCount => Assets.Sum(asset => asset.RemovedAddonIds.Count);
        public int UnknownCount => Assets.Sum(asset => asset.UnknownAddonIds.Count);
        public int FrozenAssetCount => Assets.Count(asset => asset.IsFrozen);

        public static SmartAssetReconciliationResult NotAuthoritative() =>
            new SmartAssetReconciliationResult(false, Array.Empty<SmartAssetMembershipChange>());
    }
}
