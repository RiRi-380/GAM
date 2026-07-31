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
        public const string SubscribeSystemAssetId = "subscribe-system-asset";

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
            var isSubscribed = subscribedAddonIds.Contains(normalizedAddonId);
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
                    continue;
                }

                // Other system assets (notably the legacy Junction asset) are
                // not Custom Assets and must not participate in resolution.
                if (asset.IsSystem || !ContainsAddon(asset, normalizedAddonId))
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
