using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public sealed class SubscriptionObservationService
    {
        public SubscriptionObservationResult Observe(
            Configuration configuration,
            SteamWorkshopSnapshot snapshot)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var result = new SubscriptionObservationResult
            {
                IsAuthoritative = snapshot.IsAuthoritative,
                PendingDownloadCount = snapshot.SubscribedIds
                    .Except(snapshot.InstalledIds, StringComparer.Ordinal)
                    .Count()
            };

            if (!snapshot.IsAuthoritative)
            {
                return result;
            }

            configuration.KnownSubscribedAddonIds ??= new List<string>();
            configuration.SubscriptionFirstSeenAtUtc ??= new Dictionary<string, DateTime>();

            var current = new HashSet<string>(snapshot.SubscribedIds, StringComparer.Ordinal);
            var known = new HashSet<string>(configuration.KnownSubscribedAddonIds, StringComparer.Ordinal);

            if (!configuration.SubscriptionBaselineInitialized)
            {
                configuration.SubscriptionBaselineInitialized = true;
                configuration.KnownSubscribedAddonIds = current
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                configuration.SubscriptionFirstSeenAtUtc.Clear();
                result.Changed = true;
                return result;
            }

            var newlySubscribed = current
                .Except(known, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var unsubscribed = known
                .Except(current, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            foreach (var addonId in newlySubscribed)
            {
                configuration.SubscriptionFirstSeenAtUtc[addonId] = snapshot.ObservedAtUtc;
                if (configuration.AddonMetadata.TryGetValue(addonId, out var metadata))
                {
                    metadata.FirstSeenSubscribedAtUtc = snapshot.ObservedAtUtc;
                }
            }

            foreach (var addonId in unsubscribed)
            {
                configuration.SubscriptionFirstSeenAtUtc.Remove(addonId);
            }

            foreach (var addonId in current)
            {
                if (configuration.AddonMetadata.TryGetValue(addonId, out var metadata) &&
                    configuration.SubscriptionFirstSeenAtUtc.TryGetValue(addonId, out var firstSeen))
                {
                    metadata.FirstSeenSubscribedAtUtc = firstSeen;
                }
            }

            configuration.KnownSubscribedAddonIds = current
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            result.NewlySubscribedIds = newlySubscribed;
            result.UnsubscribedIds = unsubscribed;
            result.Changed = newlySubscribed.Count > 0 || unsubscribed.Count > 0;
            return result;
        }
    }
}
