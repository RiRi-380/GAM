using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Provides deterministic, non-mutating ordering for the addon list.
    /// </summary>
    public sealed class AddonSortService
    {
        private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly StringComparer StableTextComparer = StringComparer.Ordinal;

        public IReadOnlyList<WorkshopAddon> Sort(
            IEnumerable<WorkshopAddon> addons,
            AddonSortOptions? options = null)
        {
            if (addons == null)
            {
                throw new ArgumentNullException(nameof(addons));
            }

            options ??= new AddonSortOptions();
            var source = addons.ToList();

            IOrderedEnumerable<WorkshopAddon> ordered = options.Mode switch
            {
                AddonSortMode.RecentlySubscribed => SortByRecentlySubscribed(
                    source,
                    options.Direction),
                AddonSortMode.Name => SortByName(source, options.Direction),
                AddonSortMode.Size => SortBySize(source, options.Direction),
                AddonSortMode.WorkshopUpdated => SortByWorkshopUpdated(
                    source,
                    options.Direction),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Mode,
                    "Unknown addon sort mode.")
            };

            return ordered.ToList().AsReadOnly();
        }

        private static IOrderedEnumerable<WorkshopAddon> SortByRecentlySubscribed(
            IEnumerable<WorkshopAddon> addons,
            AddonSortDirection direction)
        {
            // Migrated baseline entries have no observation timestamp. They
            // remain behind observed subscriptions in both directions.
            var withObservedFirst = addons.OrderBy(
                addon => !GetSortTimestampUtc(
                    addon,
                    AddonSortMode.RecentlySubscribed).HasValue);

            var ordered = direction == AddonSortDirection.Ascending
                ? withObservedFirst.ThenBy(addon => GetSortTimestampUtc(
                    addon,
                    AddonSortMode.RecentlySubscribed))
                : withObservedFirst.ThenByDescending(addon => GetSortTimestampUtc(
                    addon,
                    AddonSortMode.RecentlySubscribed));

            return ThenByName(ordered);
        }

        private static IOrderedEnumerable<WorkshopAddon> SortByName(
            IEnumerable<WorkshopAddon> addons,
            AddonSortDirection direction)
        {
            var ordered = direction == AddonSortDirection.Ascending
                ? addons.OrderBy(GetName, NameComparer)
                : addons.OrderByDescending(GetName, NameComparer);

            return ordered
                .ThenBy(GetName, StableTextComparer)
                .ThenBy(GetId, StableTextComparer);
        }

        private static IOrderedEnumerable<WorkshopAddon> SortBySize(
            IEnumerable<WorkshopAddon> addons,
            AddonSortDirection direction)
        {
            var ordered = direction == AddonSortDirection.Ascending
                ? addons.OrderBy(addon => addon.Size)
                : addons.OrderByDescending(addon => addon.Size);

            return ThenByName(ordered);
        }

        private static IOrderedEnumerable<WorkshopAddon> SortByWorkshopUpdated(
            IEnumerable<WorkshopAddon> addons,
            AddonSortDirection direction)
        {
            var ordered = direction == AddonSortDirection.Ascending
                ? addons.OrderBy(addon => GetSortTimestampUtc(
                    addon,
                    AddonSortMode.WorkshopUpdated))
                : addons.OrderByDescending(addon => GetSortTimestampUtc(
                    addon,
                    AddonSortMode.WorkshopUpdated));

            return ThenByName(ordered);
        }

        private static IOrderedEnumerable<WorkshopAddon> ThenByName(
            IOrderedEnumerable<WorkshopAddon> ordered)
        {
            return ordered
                .ThenBy(GetName, NameComparer)
                .ThenBy(GetName, StableTextComparer)
                .ThenBy(GetId, StableTextComparer);
        }

        /// <summary>
        /// Returns the normalized UTC timestamp used by a timestamp-based sort mode.
        /// Name and size modes do not have a timestamp key and return <see langword="null"/>.
        /// Keeping this projection public lets the UI display the exact value used for ordering.
        /// </summary>
        public static DateTime? GetSortTimestampUtc(
            WorkshopAddon addon,
            AddonSortMode mode)
        {
            if (addon == null)
            {
                throw new ArgumentNullException(nameof(addon));
            }

            return mode switch
            {
                AddonSortMode.RecentlySubscribed =>
                    NormalizeNullableUtc(addon.FirstSeenSubscribedAtUtc),
                AddonSortMode.WorkshopUpdated =>
                    NormalizeUtc(addon.WorkshopUpdatedAtUtc ?? addon.LastUpdated),
                AddonSortMode.Name or AddonSortMode.Size => null,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode,
                    "Unknown addon sort mode.")
            };
        }

        private static DateTime? NormalizeNullableUtc(DateTime? value)
        {
            return value.HasValue ? NormalizeUtc(value.Value) : null;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        private static string GetName(WorkshopAddon addon)
        {
            return addon.Title ?? string.Empty;
        }

        private static string GetId(WorkshopAddon addon)
        {
            return addon.Id ?? string.Empty;
        }
    }
}
