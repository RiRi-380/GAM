using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Immutable, read-only preview produced before a .gam document is allowed to
    /// mutate the current profile. Missing IDs are informational only; import never
    /// invokes Steam subscription APIs.
    /// </summary>
    public sealed class GamAssetImportPreview
    {
        public GamAssetImportPreview(
            GamAssetDocument document,
            string suggestedAssetName,
            bool subscriptionStatusKnown,
            IEnumerable<string> referencedAddonIds,
            IEnumerable<string> missingSubscriptionAddonIds)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            SuggestedAssetName = string.IsNullOrWhiteSpace(suggestedAssetName)
                ? throw new ArgumentException(
                    "The suggested asset name cannot be empty.",
                    nameof(suggestedAssetName))
                : suggestedAssetName;
            SubscriptionStatusKnown = subscriptionStatusKnown;
            ReferencedAddonIds = Copy(referencedAddonIds, nameof(referencedAddonIds));
            MissingSubscriptionAddonIds = Copy(
                missingSubscriptionAddonIds,
                nameof(missingSubscriptionAddonIds));
        }

        public GamAssetDocument Document { get; }

        public string SuggestedAssetName { get; }

        public bool SubscriptionStatusKnown { get; }

        /// <summary>
        /// Authoritative fixed membership, or the Smart Asset export-time snapshot
        /// for display only. Never use this list as missing-subscription or
        /// Workshop-action authority; that is exposed separately below.
        /// </summary>
        public IReadOnlyList<string> ReferencedAddonIds { get; }

        public IReadOnlyList<string> MissingSubscriptionAddonIds { get; }

        public bool IsLegacyV1 => Document.SourceFormatVersion == 1;

        public bool IsSmart =>
            Document.Membership.Kind == GamAssetDocumentMembershipKind.Smart;

        public bool HasImage => Document.ImageBytes != null;

        private static IReadOnlyList<string> Copy(
            IEnumerable<string> values,
            string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            return new ReadOnlyCollection<string>(values.ToList());
        }
    }
}
