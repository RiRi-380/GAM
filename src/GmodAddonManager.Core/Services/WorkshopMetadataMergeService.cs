using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    [Flags]
    public enum WorkshopMetadataMergeChanges
    {
        None = 0,
        Title = 1 << 0,
        ThumbnailUrl = 1 << 1,
        WorkshopUpdatedAtUtc = 1 << 2,
        Tags = 1 << 3,
        Type = 1 << 4
    }

    /// <summary>
    /// Merges authoritative Workshop metadata into GAM's persisted addon model.
    /// This service deliberately does not load or decode thumbnail bitmaps; the
    /// UI remains responsible for loading only the images it has realized.
    /// </summary>
    public sealed class WorkshopMetadataMergeService
    {
        private static readonly string[] PlaceholderPrefixes =
        {
            "Workshop-",
            "ワークショップ-"
        };

        public static bool NeedsSupplement(WorkshopAddon addon)
        {
            if (addon == null)
            {
                throw new ArgumentNullException(nameof(addon));
            }

            return NeedsTitleSupplement(addon) ||
                   string.IsNullOrWhiteSpace(addon.ThumbnailUrl) ||
                   !addon.WorkshopUpdatedAtUtc.HasValue ||
                   !HasTags(addon.Tags) ||
                   string.IsNullOrWhiteSpace(addon.Type);
        }

        public WorkshopMetadataMergeChanges Merge(
            WorkshopAddon target,
            WorkshopItemDetails? details,
            IEnumerable<string>? supplementalTags = null,
            string? supplementalType = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var changes = WorkshopMetadataMergeChanges.None;

            if (details != null)
            {
                var fetchedTitle = details.Title?.Trim();
                if (IsConcreteTitle(fetchedTitle) &&
                    !string.Equals(target.Title, fetchedTitle, StringComparison.Ordinal))
                {
                    target.Title = fetchedTitle!;
                    changes |= WorkshopMetadataMergeChanges.Title;
                }

                if (IsConcreteTitle(fetchedTitle) && target.NeedsTitleUpdate)
                {
                    target.NeedsTitleUpdate = false;
                    changes |= WorkshopMetadataMergeChanges.Title;
                }

                var previewUrl = details.PreviewUrl?.Trim();
                if (!string.IsNullOrWhiteSpace(previewUrl) &&
                    !string.Equals(
                        target.ThumbnailUrl,
                        previewUrl,
                        StringComparison.Ordinal))
                {
                    target.ThumbnailUrl = previewUrl!;
                    changes |= WorkshopMetadataMergeChanges.ThumbnailUrl;
                }

                if (TryGetWorkshopUpdatedAtUtc(
                        details.TimeUpdated,
                        out var workshopUpdatedAtUtc) &&
                    target.WorkshopUpdatedAtUtc != workshopUpdatedAtUtc)
                {
                    target.WorkshopUpdatedAtUtc = workshopUpdatedAtUtc;
                    changes |= WorkshopMetadataMergeChanges.WorkshopUpdatedAtUtc;
                }
            }

            if (!HasTags(target.Tags))
            {
                var tags = AddonClassificationService.NormalizeTags(
                    supplementalTags ?? details?.Tags);
                if (tags.Length > 0)
                {
                    target.Tags = tags;
                    changes |= WorkshopMetadataMergeChanges.Tags;
                }
            }

            if (string.IsNullOrWhiteSpace(target.Type))
            {
                var type = supplementalType?.Trim();
                if (string.IsNullOrWhiteSpace(type))
                {
                    type = AddonClassificationService.InferTypeFromTags(target.Tags);
                }

                if (!string.IsNullOrWhiteSpace(type))
                {
                    target.Type = type!;
                    changes |= WorkshopMetadataMergeChanges.Type;
                }
            }

            return changes;
        }

        private static bool NeedsTitleSupplement(WorkshopAddon addon)
        {
            if (addon.NeedsTitleUpdate || string.IsNullOrWhiteSpace(addon.Title))
            {
                return true;
            }

            var title = addon.Title.Trim();
            if (!string.IsNullOrWhiteSpace(addon.Id) &&
                string.Equals(title, addon.Id, StringComparison.Ordinal))
            {
                return true;
            }

            return PlaceholderPrefixes.Any(prefix =>
                title.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool IsConcreteTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var trimmed = title.Trim();
            return !PlaceholderPrefixes.Any(prefix =>
                trimmed.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool HasTags(IEnumerable<string>? tags)
        {
            return tags != null && tags.Any(tag => !string.IsNullOrWhiteSpace(tag));
        }

        private static bool TryGetWorkshopUpdatedAtUtc(
            long unixSeconds,
            out DateTime workshopUpdatedAtUtc)
        {
            workshopUpdatedAtUtc = default;
            if (unixSeconds <= 0)
            {
                return false;
            }

            try
            {
                workshopUpdatedAtUtc = DateTimeOffset
                    .FromUnixTimeSeconds(unixSeconds)
                    .UtcDateTime;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
