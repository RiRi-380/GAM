using System;
using System.Collections.Generic;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// A point-in-time view of Steam Workshop subscription and installation state.
    /// </summary>
    public sealed class SteamWorkshopSnapshot
    {
        public SteamWorkshopSnapshot(
            IEnumerable<string>? subscribedIds,
            IEnumerable<string>? installedIds,
            bool isAuthoritative,
            DateTime observedAtUtc)
        {
            SubscribedIds = NormalizeIds(subscribedIds);
            InstalledIds = NormalizeIds(installedIds);
            IsAuthoritative = isAuthoritative;
            ObservedAtUtc = NormalizeUtc(observedAtUtc);
        }

        public IReadOnlyList<string> SubscribedIds { get; }

        public IReadOnlyList<string> InstalledIds { get; }

        /// <summary>
        /// True only when every requested manifest existed and all required sections were parsed.
        /// </summary>
        public bool IsAuthoritative { get; }

        public DateTime ObservedAtUtc { get; }

        private static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids)
        {
            if (ids == null)
            {
                return Array.Empty<string>();
            }

            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Where(id => ulong.TryParse(id, out _))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
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
    }
}
