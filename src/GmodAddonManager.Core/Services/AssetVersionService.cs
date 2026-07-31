using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Manages membership-only Asset snapshots. This service never changes an Asset's
    /// whole state or performs Steam subscription operations.
    /// </summary>
    public sealed class AssetVersionService
    {
        private readonly Func<DateTime> utcNowProvider;

        public AssetVersionService(Func<DateTime>? utcNowProvider = null)
        {
            this.utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        }

        public int GetNextVersionNumber(Asset asset)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var history = asset.VersionHistory ?? new List<AssetVersion>();
            var maximum = history.Count == 0
                ? 0
                : Math.Max(0, history.Max(version => version?.Version ?? 0));

            return checked(maximum + 1);
        }

        public AssetVersion CreateSnapshot(Asset asset, string? note = null)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            asset.VersionHistory ??= new List<AssetVersion>();

            var snapshot = new AssetVersion
            {
                Version = GetNextVersionNumber(asset),
                CreatedAt = NormalizeUtc(utcNowProvider()),
                AddonIds = NormalizeMembership(asset.Addons),
                Note = note,
                GamContent = null,
                IncludeAddonStates = false,
                AddonStates = null,
                IsImportBaseline = false,
                NewlySubscribedAddonIds = null,
                ImportType = null
            };

            asset.VersionHistory.Add(snapshot);
            asset.CurrentVersion = snapshot.Version;
            return snapshot;
        }

        public bool RestoreSnapshot(Asset asset, int version)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var snapshot = asset.VersionHistory?.FirstOrDefault(item => item.Version == version);
            if (snapshot == null)
            {
                return false;
            }

            ClearLegacyFields(snapshot);
            asset.Addons = NormalizeMembership(snapshot.AddonIds);
            asset.CurrentVersion = snapshot.Version;
            return true;
        }

        public bool DeleteSnapshot(Asset asset, int version)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var snapshot = asset.VersionHistory?.FirstOrDefault(item => item.Version == version);
            if (snapshot == null)
            {
                return false;
            }

            asset.VersionHistory!.Remove(snapshot);
            if (asset.CurrentVersion == version)
            {
                // Live membership remains authoritative; it is now unsnapshotted.
                asset.CurrentVersion = 0;
            }

            return true;
        }

        public int ClearHistory(Asset asset)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var removedCount = asset.VersionHistory?.Count ?? 0;
            asset.VersionHistory?.Clear();
            asset.CurrentVersion = 0;
            return removedCount;
        }

        public AssetVersionMembershipDiffResult CompareCurrentMembership(
            Asset asset,
            AssetVersion snapshot)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var current = new HashSet<string>(
                NormalizeMembership(asset.Addons),
                StringComparer.Ordinal);
            var snapshotted = new HashSet<string>(
                NormalizeMembership(snapshot.AddonIds),
                StringComparer.Ordinal);

            var currentOnly = current
                .Except(snapshotted, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var snapshotOnly = snapshotted
                .Except(current, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            return new AssetVersionMembershipDiffResult(
                snapshot.Version,
                currentOnly,
                snapshotOnly);
        }

        public bool HasMembershipChanges(Asset asset, AssetVersion snapshot)
        {
            return CompareCurrentMembership(asset, snapshot).HasChanges;
        }

        private static List<string> NormalizeMembership(IEnumerable<string>? addonIds)
        {
            if (addonIds == null)
            {
                return new List<string>();
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addonId in addonIds)
            {
                if (string.IsNullOrWhiteSpace(addonId))
                {
                    continue;
                }

                var normalized = addonId.Trim();
                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static void ClearLegacyFields(AssetVersion snapshot)
        {
            snapshot.AddonIds = NormalizeMembership(snapshot.AddonIds);
            snapshot.GamContent = null;
            snapshot.IncludeAddonStates = false;
            snapshot.AddonStates = null;
            snapshot.IsImportBaseline = false;
            snapshot.NewlySubscribedAddonIds = null;
            snapshot.ImportType = null;
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
