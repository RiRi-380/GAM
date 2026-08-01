using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Reconciles valid, read-only observations of addonnomount.txt with the
    /// dedicated GMod-originated exclusion Asset. Runtime I/O and persistence
    /// transactions remain the caller's responsibility.
    /// </summary>
    public sealed class GmodDisabledAddonReconciliationService
    {
        public const string SystemAssetId = SystemAssetDefinitions.GmodDisabledId;
        public const string SystemAssetName = SystemAssetDefinitions.GmodDisabledName;
        public const string LegacyImportedAssetName = "GModで無効化されていたAddon";

        public GmodDisabledSystemAssetNormalizationResult EnsureSystemAsset(
            Configuration configuration,
            bool absorbUntouchedLegacyImport)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            configuration.Assets ??= new List<Asset>();
            var changed = false;
            var absorbedLegacy = false;
            var fixedAssets = configuration.Assets
                .Where(asset => string.Equals(
                    asset.Id,
                    SystemAssetId,
                    StringComparison.Ordinal))
                .ToList();

            Asset systemAsset;
            if (fixedAssets.Count == 0)
            {
                systemAsset = CreateSystemAsset();
                configuration.Assets.Add(systemAsset);
                changed = true;
            }
            else
            {
                systemAsset = fixedAssets[0];
                foreach (var duplicate in fixedAssets.Skip(1))
                {
                    systemAsset.Addons ??= new List<string>();
                    systemAsset.Addons.AddRange(duplicate.Addons ?? Enumerable.Empty<string>());
                    configuration.Assets.Remove(duplicate);
                    changed = true;
                }
            }

            if (absorbUntouchedLegacyImport)
            {
                var legacyCandidates = configuration.Assets
                    .Where(asset => !ReferenceEquals(asset, systemAsset))
                    .Where(IsExactUntouchedLegacyImport)
                    .ToList();
                if (legacyCandidates.Count == 1)
                {
                    systemAsset.Addons ??= new List<string>();
                    systemAsset.Addons.AddRange(legacyCandidates[0].Addons);
                    configuration.Assets.Remove(legacyCandidates[0]);
                    changed = true;
                    absorbedLegacy = true;
                }
            }

            var normalizedMembers = NormalizeIds(systemAsset.Addons);
            if (!SequenceEqual(systemAsset.Addons, normalizedMembers))
            {
                systemAsset.Addons = normalizedMembers;
                changed = true;
            }

            if (!string.Equals(systemAsset.Name, SystemAssetName, StringComparison.Ordinal))
            {
                systemAsset.Name = SystemAssetName;
                changed = true;
            }
            if (!systemAsset.IsSystem)
            {
                systemAsset.IsSystem = true;
                changed = true;
            }
            if (systemAsset.GetWholeState() != AddonState.Excluded)
            {
                systemAsset.SetWholeState(AddonState.Excluded);
                changed = true;
            }
            if (systemAsset.IsFavorite)
            {
                systemAsset.IsFavorite = false;
                changed = true;
            }
            if (systemAsset.NeedsMigrationReview)
            {
                systemAsset.NeedsMigrationReview = false;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(systemAsset.ImagePath))
            {
                systemAsset.ImagePath = null;
                changed = true;
            }
            systemAsset.AddonStates ??= new Dictionary<string, AddonState>();
            if (systemAsset.AddonStates.Count > 0)
            {
                systemAsset.AddonStates.Clear();
                changed = true;
            }
            systemAsset.VersionHistory ??= new List<AssetVersion>();
            if (systemAsset.VersionHistory.Count > 0)
            {
                systemAsset.VersionHistory.Clear();
                changed = true;
            }
            if (systemAsset.CurrentVersion != 0)
            {
                systemAsset.CurrentVersion = 0;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(systemAsset.WorkshopCollectionId))
            {
                systemAsset.WorkshopCollectionId = null;
                changed = true;
            }
            if (systemAsset.AutoUpdateCollection)
            {
                systemAsset.AutoUpdateCollection = false;
                changed = true;
            }

            var desiredIndex = configuration.Assets.Any(asset =>
                string.Equals(
                    asset.Id,
                    SystemAssetDefinitions.SubscribeId,
                    StringComparison.Ordinal))
                ? 1
                : 0;
            var currentIndex = configuration.Assets.IndexOf(systemAsset);
            if (currentIndex != desiredIndex)
            {
                configuration.Assets.Remove(systemAsset);
                configuration.Assets.Insert(
                    Math.Min(desiredIndex, configuration.Assets.Count),
                    systemAsset);
                changed = true;
            }

            return new GmodDisabledSystemAssetNormalizationResult(
                systemAsset,
                changed,
                absorbedLegacy);
        }

        public GmodDisabledAddonReconciliationResult ReconcileValidObservation(
            Configuration configuration,
            IEnumerable<string> subscribedIds,
            IEnumerable<string> disabledIds,
            DateTime observedAtUtc,
            bool allowInitialSeed,
            string? stateStorePath = null,
            IReadOnlyDictionary<string, bool>? migrationDesiredStates = null)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var normalizedObservedAt = NormalizeUtc(observedAtUtc);
            var subscribed = new HashSet<string>(NormalizeIds(subscribedIds), StringComparer.Ordinal);
            var disabled = new HashSet<string>(NormalizeIds(disabledIds), StringComparer.Ordinal);
            var actualStates = subscribed.ToDictionary(
                id => id,
                id => !disabled.Contains(id),
                StringComparer.Ordinal);
            var normalization = EnsureSystemAsset(
                configuration,
                absorbUntouchedLegacyImport: false);
            var systemAsset = normalization.Asset;
            var previousMembers = new HashSet<string>(
                NormalizeIds(systemAsset.Addons),
                StringComparer.Ordinal);
            var members = new HashSet<string>(
                previousMembers.Where(subscribed.Contains),
                StringComparer.Ordinal);

            configuration.LastObservedGmodAddonStates ??=
                new Dictionary<string, bool>(StringComparer.Ordinal);
            configuration.LastGamAppliedAddonStates ??=
                new Dictionary<string, bool>(StringComparer.Ordinal);

            var pendingOperationId = configuration.PendingGamRuntimeWrite?.OperationId;
            var pendingRecovery = RecoverPendingWrite(
                configuration,
                disabled,
                normalizedObservedAt,
                stateStorePath,
                subscribed);
            var normalizedStateStorePath = stateStorePath?.Trim();
            var observationPathMatches =
                !string.IsNullOrWhiteSpace(
                    configuration.LastObservedGmodStateStorePath) &&
                !string.IsNullOrWhiteSpace(normalizedStateStorePath) &&
                string.Equals(
                    configuration.LastObservedGmodStateStorePath,
                    normalizedStateStorePath,
                    StringComparison.OrdinalIgnoreCase);
            var stateStoreChanged =
                configuration.GmodObservationBaselineInitialized &&
                !observationPathMatches;
            var hadObservationBaseline =
                configuration.GmodObservationBaselineInitialized &&
                !stateStoreChanged;
            var priorObserved = new Dictionary<string, bool>(
                configuration.LastObservedGmodAddonStates,
                StringComparer.Ordinal);

            if (!hadObservationBaseline)
            {
                if (allowInitialSeed)
                {
                    members = new HashSet<string>(
                        subscribed.Where(disabled.Contains),
                        StringComparer.Ordinal);
                }
                else if (!stateStoreChanged)
                {
                    // During v2->v3 migration an exact untouched legacy import
                    // may already have supplied members. Keep only members still
                    // disabled in the first valid observation; do not broaden it.
                    members.RemoveWhere(id => actualStates[id]);

                    if (configuration.GmodAttributionMigrationPending &&
                        migrationDesiredStates != null)
                    {
                        foreach (var addonId in subscribed)
                        {
                            if (disabled.Contains(addonId) &&
                                migrationDesiredStates.TryGetValue(
                                    addonId,
                                    out var wasDesiredEnabled) &&
                                wasDesiredEnabled)
                            {
                                members.Add(addonId);
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var addonId in subscribed)
                {
                    var actualEnabled = actualStates[addonId];

                    if (!priorObserved.TryGetValue(addonId, out var previousEnabled))
                    {
                        // A newly subscribed or re-subscribed ID has no observed
                        // transition yet. A stale addonnomount entry must not be
                        // imported merely because the ID reappeared.
                        continue;
                    }

                    if (previousEnabled && !actualEnabled)
                    {
                        members.Add(addonId);
                    }
                    else if (!previousEnabled && actualEnabled)
                    {
                        members.Remove(addonId);
                    }
                }
            }

            var orderedMembers = members.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var membershipChanged = !previousMembers.SetEquals(members);
            var observationChanged =
                !configuration.GmodObservationBaselineInitialized ||
                stateStoreChanged ||
                !DictionaryEqual(configuration.LastObservedGmodAddonStates, actualStates);
            var initialMarkerChanged = !configuration.InitialRuntimeImportCompleted;

            systemAsset.Addons = orderedMembers;
            configuration.GmodObservationBaselineInitialized = true;
            configuration.LastObservedGmodAddonStates = actualStates;
            configuration.LastObservedGmodStateStorePath = normalizedStateStorePath;
            if (observationChanged)
            {
                configuration.LastObservedGmodRuntimeAtUtc = normalizedObservedAt;
            }

            configuration.InitialRuntimeImportCompleted = true;
            configuration.InitialRuntimeImportCompletedAtUtc ??= normalizedObservedAt;
            var migrationMarkerChanged =
                configuration.GmodAttributionMigrationPending;
            configuration.GmodAttributionMigrationPending = false;

            // Subscription is authoritative for the scope of both baselines.
            configuration.LastGamAppliedAddonStates =
                configuration.LastGamAppliedAddonStates
                    .Where(entry => subscribed.Contains(entry.Key))
                    .ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value,
                        StringComparer.Ordinal);

            var result = new GmodDisabledAddonReconciliationResult(
                normalization.Changed ||
                membershipChanged ||
                observationChanged ||
                initialMarkerChanged ||
                migrationMarkerChanged ||
                pendingRecovery != PendingGamRuntimeWriteRecovery.None,
                membershipChanged,
                pendingRecovery,
                orderedMembers);
            result.PendingOperationId = pendingOperationId;
            return result;
        }

        public PendingGamRuntimeWrite CreatePendingWrite(
            IReadOnlyDictionary<string, bool> targetStates,
            IReadOnlyDictionary<string, bool> previousStates,
            DateTime createdAtUtc,
            string? stateStorePath = null)
        {
            if (targetStates == null)
            {
                throw new ArgumentNullException(nameof(targetStates));
            }
            if (previousStates == null)
            {
                throw new ArgumentNullException(nameof(previousStates));
            }

            var targets = NormalizeStateMap(targetStates);
            var previous = NormalizeStateMap(previousStates)
                .Where(entry => targets.ContainsKey(entry.Key))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);
            return new PendingGamRuntimeWrite
            {
                OperationId = Guid.NewGuid().ToString("N"),
                TargetStates = targets,
                PreviousStates = previous,
                CreatedAtUtc = NormalizeUtc(createdAtUtc),
                StateStorePath = stateStorePath?.Trim() ?? string.Empty,
                ConflictDetected = false
            };
        }

        public void RecordSuccessfulGamWrite(
            Configuration configuration,
            IReadOnlyDictionary<string, bool> appliedStates,
            DateTime appliedAtUtc,
            string? stateStorePath = null)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var normalized = NormalizeStateMap(appliedStates);
            var timestamp = NormalizeUtc(appliedAtUtc);
            configuration.LastGamAppliedAddonStates ??=
                new Dictionary<string, bool>(StringComparer.Ordinal);
            configuration.LastObservedGmodAddonStates ??=
                new Dictionary<string, bool>(StringComparer.Ordinal);

            var normalizedStateStorePath = stateStorePath?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedStateStorePath) &&
                !string.IsNullOrWhiteSpace(configuration.LastGamAppliedStateStorePath) &&
                !string.Equals(
                    configuration.LastGamAppliedStateStorePath,
                    normalizedStateStorePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                configuration.LastGamAppliedAddonStates.Clear();
            }
            if (!string.IsNullOrWhiteSpace(normalizedStateStorePath) &&
                !string.IsNullOrWhiteSpace(configuration.LastObservedGmodStateStorePath) &&
                !string.Equals(
                    configuration.LastObservedGmodStateStorePath,
                    normalizedStateStorePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                configuration.LastObservedGmodAddonStates.Clear();
            }

            foreach (var entry in normalized)
            {
                configuration.LastGamAppliedAddonStates[entry.Key] = entry.Value;
                configuration.LastObservedGmodAddonStates[entry.Key] = entry.Value;
            }

            configuration.GamAppliedRuntimeBaselineInitialized = true;
            configuration.GmodObservationBaselineInitialized = true;
            configuration.LastGamAppliedRuntimeAtUtc = timestamp;
            configuration.LastObservedGmodRuntimeAtUtc = timestamp;
            configuration.LastGamAppliedStateStorePath = normalizedStateStorePath;
            configuration.LastObservedGmodStateStorePath = normalizedStateStorePath;
            configuration.PendingGamRuntimeWrite = null;
        }

        public PendingGamRuntimeWriteRecovery RecoverPendingWrite(
            Configuration configuration,
            ISet<string> actualDisabledIds,
            DateTime observedAtUtc,
            string? stateStorePath = null,
            ISet<string>? authoritativeSubscribedIds = null)
        {
            if (configuration.PendingGamRuntimeWrite == null)
            {
                return PendingGamRuntimeWriteRecovery.None;
            }

            var pending = configuration.PendingGamRuntimeWrite;
            pending.TargetStates ??= new Dictionary<string, bool>();
            pending.PreviousStates ??= new Dictionary<string, bool>();
            var unscopedTargets = NormalizeStateMap(pending.TargetStates);
            var targets = unscopedTargets;
            if (authoritativeSubscribedIds != null)
            {
                targets = targets
                    .Where(entry => authoritativeSubscribedIds.Contains(entry.Key))
                    .ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value,
                        StringComparer.Ordinal);
            }
            var previous = NormalizeStateMap(pending.PreviousStates)
                .Where(entry => targets.ContainsKey(entry.Key))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);
            pending.TargetStates = targets;
            pending.PreviousStates = previous;
            if (targets.Count == 0)
            {
                configuration.PendingGamRuntimeWrite = null;
                return PendingGamRuntimeWriteRecovery.Completed;
            }

            var subscriptionScopeChanged =
                targets.Count != unscopedTargets.Count;
            if (pending.ConflictDetected && !subscriptionScopeChanged)
            {
                return PendingGamRuntimeWriteRecovery.Conflicted;
            }
            if (subscriptionScopeChanged)
            {
                // The old ambiguity included IDs that are no longer runtime
                // targets. Re-evaluate only the surviving authoritative scope.
                pending.ConflictDetected = false;
            }

            var sameStateStore =
                !string.IsNullOrWhiteSpace(pending.StateStorePath) &&
                !string.IsNullOrWhiteSpace(stateStorePath) &&
                string.Equals(
                    pending.StateStorePath.Trim(),
                    stateStorePath.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            var actualForScope = targets.Keys.ToDictionary(
                id => id,
                id => !actualDisabledIds.Contains(id),
                StringComparer.Ordinal);
            var matchesTarget =
                sameStateStore &&
                DictionaryEqual(targets, actualForScope);
            var matchesPrevious =
                sameStateStore &&
                previous.Count == targets.Count &&
                DictionaryEqual(previous, actualForScope);

            if (matchesTarget)
            {
                configuration.PendingGamRuntimeWrite = null;
                RecordSuccessfulGamWrite(
                    configuration,
                    targets,
                    observedAtUtc,
                    stateStorePath);
                return PendingGamRuntimeWriteRecovery.Completed;
            }

            if (matchesPrevious)
            {
                // Keep the durable intent until PendingChangeManager has
                // successfully persisted its full-reapply marker.
                return PendingGamRuntimeWriteRecovery.NotApplied;
            }

            pending.ConflictDetected = true;

            if (sameStateStore && !matchesPrevious)
            {
                // SetEnabledBulk replaces one addonnomount document atomically.
                // A mixed recovery state therefore means the write may have
                // completed and GMod/user activity subsequently changed a subset.
                // Acknowledge only changed entries still matching GAM's target;
                // leave all other entries to external-transition reconciliation.
                var inferableAppliedStates = targets
                    .Where(entry =>
                        previous.TryGetValue(entry.Key, out var previousValue) &&
                        previousValue != entry.Value &&
                        actualForScope.TryGetValue(entry.Key, out var actualValue) &&
                        actualValue == entry.Value)
                    .ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value,
                        StringComparer.Ordinal);
                if (inferableAppliedStates.Count > 0)
                {
                    RecordSuccessfulGamWrite(
                        configuration,
                        inferableAppliedStates,
                        observedAtUtc,
                        stateStorePath);
                }
            }

            // RecordSuccessfulGamWrite normally clears the journal. A conflict
            // must remain durable until PendingChangeManager has first removed
            // its automatic-apply marker, otherwise a crash between the two file
            // updates can forget the ambiguity and overwrite GMod on restart.
            configuration.PendingGamRuntimeWrite = pending;

            return PendingGamRuntimeWriteRecovery.Conflicted;
        }

        public static bool IsProtectedSystemAsset(string? assetId)
        {
            return string.Equals(assetId, SystemAssetId, StringComparison.Ordinal);
        }

        private static Asset CreateSystemAsset()
        {
            var asset = new Asset(SystemAssetName, isSystem: true)
            {
                Id = SystemAssetId
            };
            asset.SetWholeState(AddonState.Excluded);
            return asset;
        }

        private static bool IsExactUntouchedLegacyImport(Asset asset)
        {
            if (asset == null ||
                asset.IsSystem ||
                !string.Equals(asset.Name, LegacyImportedAssetName, StringComparison.Ordinal) ||
                asset.GetWholeState() != AddonState.Excluded ||
                asset.IsFavorite ||
                asset.NeedsMigrationReview ||
                !string.IsNullOrWhiteSpace(asset.ImagePath) ||
                !string.IsNullOrWhiteSpace(asset.WorkshopCollectionId) ||
                asset.AutoUpdateCollection ||
                asset.CurrentVersion != 0 ||
                (asset.VersionHistory?.Count ?? 0) != 0 ||
                (asset.AddonStates?.Count ?? 0) != 0)
            {
                return false;
            }

            var normalized = NormalizeIds(asset.Addons);
            return normalized.Count > 0 && SequenceEqual(asset.Addons, normalized);
        }

        private static Dictionary<string, bool> NormalizeStateMap(
            IEnumerable<KeyValuePair<string, bool>>? states)
        {
            return (states ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                .Where(entry => IsWorkshopId(entry.Key))
                .GroupBy(entry => entry.Key.Trim(), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Value,
                    StringComparer.Ordinal);
        }

        private static List<string> NormalizeIds(IEnumerable<string>? ids)
        {
            return (ids ?? Enumerable.Empty<string>())
                .Where(IsWorkshopId)
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsWorkshopId(string? id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                   ulong.TryParse(id.Trim(), out _);
        }

        private static bool SequenceEqual(
            IEnumerable<string>? left,
            IEnumerable<string> right)
        {
            return (left ?? Enumerable.Empty<string>())
                .SequenceEqual(right, StringComparer.Ordinal);
        }

        private static bool DictionaryEqual(
            IReadOnlyDictionary<string, bool> left,
            IReadOnlyDictionary<string, bool> right)
        {
            return left.Count == right.Count &&
                   left.All(entry =>
                       right.TryGetValue(entry.Key, out var value) &&
                       value == entry.Value);
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

    public enum PendingGamRuntimeWriteRecovery
    {
        None,
        Completed,
        NotApplied,
        Conflicted
    }

    public sealed class GmodDisabledSystemAssetNormalizationResult
    {
        public GmodDisabledSystemAssetNormalizationResult(
            Asset asset,
            bool changed,
            bool absorbedLegacyImport)
        {
            Asset = asset;
            Changed = changed;
            AbsorbedLegacyImport = absorbedLegacyImport;
        }

        public Asset Asset { get; }
        public bool Changed { get; }
        public bool AbsorbedLegacyImport { get; }
    }

    public sealed class GmodDisabledAddonReconciliationResult
    {
        public GmodDisabledAddonReconciliationResult(
            bool changed,
            bool membershipChanged,
            PendingGamRuntimeWriteRecovery pendingRecovery,
            IReadOnlyList<string> memberIds)
        {
            Changed = changed;
            MembershipChanged = membershipChanged;
            PendingRecovery = pendingRecovery;
            MemberIds = memberIds;
        }

        public bool Changed { get; }
        public bool MembershipChanged { get; }
        public PendingGamRuntimeWriteRecovery PendingRecovery { get; }
        public IReadOnlyList<string> MemberIds { get; }
        internal string? PendingOperationId { get; set; }
        internal Guid? QueuedRuntimeApplyGeneration { get; set; }
    }
}
