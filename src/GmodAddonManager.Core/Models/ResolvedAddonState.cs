using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Explains the primary outcome of resolving an addon's desired runtime state.
    /// The contributing assets are exposed separately so callers do not need to
    /// parse display text to build state details.
    /// </summary>
    public enum AddonStateResolutionReason
    {
        NotSubscribed,
        Excluded,
        Enabled,
        NoEnabledSource
    }

    public sealed class ResolvedAddonStateSource
    {
        public ResolvedAddonStateSource(string assetId, string assetName)
        {
            AssetId = assetId ?? string.Empty;
            AssetName = assetName ?? string.Empty;
        }

        public string AssetId { get; }

        public string AssetName { get; }
    }

    /// <summary>
    /// Pure resolution result. DesiredEnabled is meaningful only when
    /// IsRuntimeTarget is true.
    /// </summary>
    public sealed class ResolvedAddonState
    {
        public ResolvedAddonState(
            string addonId,
            bool isSubscribed,
            bool desiredEnabled,
            bool enabledBySubscribe,
            AddonStateResolutionReason reason,
            IReadOnlyList<ResolvedAddonStateSource> enabledByAssets,
            IReadOnlyList<ResolvedAddonStateSource> excludedByAssets)
        {
            AddonId = string.IsNullOrWhiteSpace(addonId)
                ? throw new ArgumentException("Addon ID cannot be empty.", nameof(addonId))
                : addonId;
            IsSubscribed = isSubscribed;
            DesiredEnabled = desiredEnabled;
            EnabledBySubscribe = enabledBySubscribe;
            Reason = reason;
            EnabledByAssets = enabledByAssets ??
                throw new ArgumentNullException(nameof(enabledByAssets));
            ExcludedByAssets = excludedByAssets ??
                throw new ArgumentNullException(nameof(excludedByAssets));
        }

        public string AddonId { get; }

        public bool IsSubscribed { get; }

        public bool IsRuntimeTarget => IsSubscribed;

        public bool DesiredEnabled { get; }

        public bool EnabledBySubscribe { get; }

        public AddonStateResolutionReason Reason { get; }

        public IReadOnlyList<ResolvedAddonStateSource> EnabledByAssets { get; }

        public IReadOnlyList<ResolvedAddonStateSource> ExcludedByAssets { get; }
    }
}
