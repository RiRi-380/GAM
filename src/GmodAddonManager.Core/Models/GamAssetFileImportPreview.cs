using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Read-only preview for every supported .gam generation. Steam subscription
    /// state is informational; importing never changes Workshop subscriptions.
    /// </summary>
    public sealed class GamAssetFileImportPreview
    {
        public GamAssetFileImportPreview(
            GamAssetFileReadResult content,
            GamAssetImportPreview? singleAssetPreview,
            bool subscriptionStatusKnown,
            IEnumerable<string> referencedAddonIds,
            IEnumerable<string> missingSubscriptionAddonIds)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SingleAssetPreview = singleAssetPreview;
            SubscriptionStatusKnown = subscriptionStatusKnown;
            ReferencedAddonIds = Copy(referencedAddonIds, nameof(referencedAddonIds));
            MissingSubscriptionAddonIds = Copy(
                missingSubscriptionAddonIds,
                nameof(missingSubscriptionAddonIds));
            RequiredNestedGroupDepth = CalculateRequiredNestedGroupDepth(content);

            if (content.Kind == GamAssetFileContentKind.SingleAsset &&
                singleAssetPreview == null)
            {
                throw new ArgumentException(
                    "A single-Asset file requires its compatibility preview.",
                    nameof(singleAssetPreview));
            }
            if (content.Kind == GamAssetFileContentKind.Bundle &&
                singleAssetPreview != null)
            {
                throw new ArgumentException(
                    "A bundle cannot contain a single-Asset preview.",
                    nameof(singleAssetPreview));
            }
        }

        public GamAssetFileReadResult Content { get; }

        public GamAssetImportPreview? SingleAssetPreview { get; }

        public bool SubscriptionStatusKnown { get; }

        public IReadOnlyList<string> ReferencedAddonIds { get; }

        public IReadOnlyList<string> MissingSubscriptionAddonIds { get; }

        public bool IsBundle => Content.Kind == GamAssetFileContentKind.Bundle;

        public int AssetCount => IsBundle ? Content.Bundle!.Assets.Count : 1;

        public int GroupCount => IsBundle ? Content.Bundle!.Groups.Count : 0;

        /// <summary>
        /// Deepest nested Group level required to restore this bundle. A root
        /// Group has depth 0 and its direct child Group has depth 1. Bundles
        /// without Groups therefore require depth 0.
        /// </summary>
        public int RequiredNestedGroupDepth { get; }

        public int ImageCount => IsBundle
            ? Content.Bundle!.Assets.Count(asset => asset.ImageBytes != null) +
              Content.Bundle.Groups.Count(group => group.ImageBytes != null)
            : Content.SingleAsset!.ImageBytes == null ? 0 : 1;

        private static int CalculateRequiredNestedGroupDepth(
            GamAssetFileReadResult content)
        {
            if (content.Kind != GamAssetFileContentKind.Bundle ||
                content.Bundle!.Groups.Count == 0)
            {
                return 0;
            }

            // The compatibility reader validates that every Group occurs once
            // in this topology, that references exist, and that it is acyclic.
            // Keep this calculation defensive for programmatically constructed
            // previews so a malformed graph cannot recurse forever.
            var groupsById = content.Bundle.Groups.ToDictionary(
                group => group.LocalId,
                StringComparer.OrdinalIgnoreCase);
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maximumDepth = 0;

            foreach (var rootEntry in content.Bundle.RootChildren)
            {
                if (rootEntry.Kind == GamAssetBundleEntryKind.Group)
                {
                    VisitGroup(rootEntry.LocalId, depth: 0);
                }
            }

            return maximumDepth;

            void VisitGroup(string localId, int depth)
            {
                maximumDepth = Math.Max(maximumDepth, depth);
                if (!groupsById.TryGetValue(localId, out var group) ||
                    !path.Add(group.LocalId))
                {
                    return;
                }

                try
                {
                    foreach (var child in group.Children)
                    {
                        if (child.Kind == GamAssetBundleEntryKind.Group)
                        {
                            VisitGroup(child.LocalId, depth + 1);
                        }
                    }
                }
                finally
                {
                    path.Remove(group.LocalId);
                }
            }
        }

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
