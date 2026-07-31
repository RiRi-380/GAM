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
                addon => !addon.FirstSeenSubscribedAtUtc.HasValue);

            var ordered = direction == AddonSortDirection.Ascending
                ? withObservedFirst.ThenBy(addon => addon.FirstSeenSubscribedAtUtc)
                : withObservedFirst.ThenByDescending(addon => addon.FirstSeenSubscribedAtUtc);

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
                ? addons.OrderBy(GetWorkshopUpdatedAtUtc)
                : addons.OrderByDescending(GetWorkshopUpdatedAtUtc);

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

        private static DateTime GetWorkshopUpdatedAtUtc(WorkshopAddon addon)
        {
            return addon.WorkshopUpdatedAtUtc ?? addon.LastUpdated;
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
