using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Resolves the desired state from the new whole-asset semantics while the
    /// persisted Asset model still uses its compatibility fields.
    ///
    /// Asset.Enabled=false maps to the neutral Disabled state. Otherwise,
    /// Asset.DefaultAddonState is treated as the whole asset state. Per-addon
    /// compatibility states are deliberately ignored.
    /// </summary>
    public sealed class AssetStateResolver
    {
        public const string SubscribeSystemAssetId = SystemAssetDefinitions.SubscribeId;

        public ResolvedAddonState Resolve(
            string addonId,
            IEnumerable<Asset> assets,
            ISet<string> subscribedAddonIds)
        {
            if (string.IsNullOrWhiteSpace(addonId))
            {
                throw new ArgumentException("Addon ID cannot be empty.", nameof(addonId));
            }

            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }

            if (subscribedAddonIds == null)
            {
                throw new ArgumentNullException(nameof(subscribedAddonIds));
            }

            var normalizedAddonId = addonId.Trim();
            return ResolveCore(normalizedAddonId, assets, subscribedAddonIds.Contains(normalizedAddonId),
                membershipAlreadyMatched: false);
        }

        internal IReadOnlyDictionary<string, ResolvedAddonState> ResolveAll(
            IEnumerable<Asset> assets, ISet<string> subscribedAddonIds)
        {
            var assetsByAddon = subscribedAddonIds.ToDictionary(
                id => id, _ => new List<Asset>(), StringComparer.Ordinal);
            foreach (var asset in assets.Where(asset => asset != null))
            {
                if (asset.Id == SubscribeSystemAssetId)
                {
                    foreach (var matchingAssets in assetsByAddon.Values)
                    {
                        matchingAssets.Add(asset);
                    }
                    continue;
                }

                if (GetWholeAssetState(asset) == AddonState.Disabled ||
                    (asset.IsSystem && asset.Id != SystemAssetDefinitions.GmodDisabledId) ||
                    asset.Addons == null)
                {
                    continue;
                }

                foreach (var id in asset.Addons.Distinct(StringComparer.Ordinal))
                {
                    if (id != null && assetsByAddon.TryGetValue(id, out var matchingAssets))
                    {
                        matchingAssets.Add(asset);
                    }
                }
            }

            return assetsByAddon.ToDictionary(
                pair => pair.Key,
                pair => ResolveCore(pair.Key, pair.Value, isSubscribed: true, membershipAlreadyMatched: true),
                StringComparer.Ordinal);
        }

        private static ResolvedAddonState ResolveCore(
            string normalizedAddonId, IEnumerable<Asset> assets, bool isSubscribed, bool membershipAlreadyMatched)
        {
            var enabledBySubscribe = false;
            var enabledByAssets = new List<ResolvedAddonStateSource>();
            var excludedByAssets = new List<ResolvedAddonStateSource>();

            foreach (var asset in assets.Where(asset => asset != null))
            {
                var state = GetWholeAssetState(asset);

                if (string.Equals(
                        asset.Id,
                        SubscribeSystemAssetId,
                        StringComparison.Ordinal))
                {
                    enabledBySubscribe =
                        isSubscribed &&
                        state == AddonState.Enabled;
                    if (isSubscribed && state == AddonState.Excluded)
                    {
                        excludedByAssets.Add(CreateSource(asset));
                    }
                    continue;
                }

                if (string.Equals(
                        asset.Id,
                        SystemAssetDefinitions.GmodDisabledId,
                        StringComparison.Ordinal))
                {
                    if (!membershipAlreadyMatched && !ContainsAddon(asset, normalizedAddonId))
                    {
                        continue;
                    }

                    if (state == AddonState.Enabled)
                    {
                        enabledByAssets.Add(CreateSource(asset));
                    }
                    else if (state == AddonState.Excluded)
                    {
                        excludedByAssets.Add(CreateSource(asset));
                    }
                    continue;
                }

                // Other system assets (notably the legacy Junction asset) are
                // not Custom Assets and must not participate in resolution.
                if (asset.IsSystem || (!membershipAlreadyMatched && !ContainsAddon(asset, normalizedAddonId)))
                {
                    continue;
                }

                if (state == AddonState.Enabled)
                {
                    enabledByAssets.Add(CreateSource(asset));
                }
                else if (state == AddonState.Excluded)
                {
                    excludedByAssets.Add(CreateSource(asset));
                }
            }

            var desiredEnabled =
                isSubscribed &&
                excludedByAssets.Count == 0 &&
                (enabledBySubscribe || enabledByAssets.Count > 0);

            var reason = !isSubscribed
                ? AddonStateResolutionReason.NotSubscribed
                : excludedByAssets.Count > 0
                    ? AddonStateResolutionReason.Excluded
                    : desiredEnabled
                        ? AddonStateResolutionReason.Enabled
                        : AddonStateResolutionReason.NoEnabledSource;

            return new ResolvedAddonState(
                normalizedAddonId,
                isSubscribed,
                desiredEnabled,
                enabledBySubscribe,
                reason,
                enabledByAssets.AsReadOnly(),
                excludedByAssets.AsReadOnly());
        }

        private static AddonState GetWholeAssetState(Asset asset)
        {
            return asset.Enabled
                ? asset.DefaultAddonState
                : AddonState.Disabled;
        }

        private static bool ContainsAddon(Asset asset, string addonId)
        {
            return asset.Addons != null &&
                   asset.Addons.Any(id =>
                       string.Equals(id, addonId, StringComparison.Ordinal));
        }

        private static ResolvedAddonStateSource CreateSource(Asset asset)
        {
            return new ResolvedAddonStateSource(asset.Id, asset.Name);
        }
    }
}
