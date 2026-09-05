using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public partial class AddonManager
    {
        /// <summary>
        /// Capture in-memory inputs on the caller's UI context, then resolve
        /// counts and read the runtime file off-thread. Never refresh Steam,
        /// reconcile, save or log.
        /// Subscription-dependent counts use only the last observed baseline.
        /// </summary>
        public async Task<AddonDiagnosticSnapshot> CaptureDiagnosticSnapshotAsync()
        {
            if (configuration == null)
                throw new InvalidOperationException("Configuration has not been loaded.");

            var result = new AddonDiagnosticSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Initialized = _initializationCompleted,
                SchemaVersion = configuration.SchemaVersion,
                CustomAssets = configuration.Assets.Count(asset => !asset.IsSystem),
                SmartAssets = configuration.Assets.Count(asset => asset.IsSmart),
                Groups = configuration.AssetGroups.Count,
                AssetsNeedingReview = configuration.Assets.Count(asset => asset.NeedsMigrationReview),
                MetadataEntries = configuration.AddonMetadata.Count,
                PendingChanges = ObserveDiagnosticValue(PendingChangeCountProvider),
                ApplyInProgress = ObserveDiagnosticValue(PendingApplyInProgressProvider),
                GmodRunning = ObserveDiagnosticValue(GmodRunningProvider),
                PendingRuntimeApply = configuration.PendingRuntimeApplyRequired,
                PendingRuntimeWrite = configuration.PendingGamRuntimeWrite != null,
                RuntimeWriteConflict = configuration.PendingGamRuntimeWrite?.ConflictDetected == true
            };
            if (result.PendingChanges < 0)
            {
                result.PendingChanges = null;
            }

            var subscribed = configuration.SubscriptionBaselineInitialized
                ? new HashSet<string>(
                    configuration.KnownSubscribedAddonIds.Where(IsWorkshopNumericId),
                    StringComparer.Ordinal)
                : null;
            // Copy only the resolver's inputs; background work must not enumerate
            // live memberships while the UI is editing them.
            var assets = configuration.Assets.Select(asset => new Asset
            {
                Id = asset.Id,
                IsSystem = asset.IsSystem,
                State = asset.State,
                Addons = new List<string>(asset.Addons)
            }).ToArray();
            var store = gmodAddonStateStore;
            return await Task.Run(() =>
            {
                Dictionary<string, bool>? expected = null;
                if (subscribed != null)
                {
                    expected = BuildExpectedStatesForAssets(assets, subscribed);
                    result.LastKnownSubscriptions = expected.Count;
                    result.DesiredEnabled = expected.Count(pair => pair.Value);
                }
                if (store == null) return result;
                try
                {
                    var runtime = store.ReadSnapshot();
                    result.RuntimeReadAtUtc = runtime.ObservedAtUtc;
                    result.RuntimeStatus = !runtime.FileExists
                        ? DiagnosticRuntimeStatus.Missing
                        : runtime.IsValidFormat
                            ? DiagnosticRuntimeStatus.Valid
                            : DiagnosticRuntimeStatus.Invalid;
                    // Missing is explicit; it is not evidence of a successfully read state.
                    if (result.RuntimeStatus == DiagnosticRuntimeStatus.Valid && expected != null)
                    {
                        var disabled = new HashSet<string>(runtime.DisabledIds, StringComparer.Ordinal);
                        result.RuntimeEnabled = expected.Count(pair => !disabled.Contains(pair.Key));
                        result.Mismatches = expected.Count(pair => pair.Value == disabled.Contains(pair.Key));
                    }
                }
                catch (Exception)
                {
                    // A failed observation remains visible without copying exception text.
                    result.RuntimeStatus = DiagnosticRuntimeStatus.Unreadable;
                }
                return result;
            }).ConfigureAwait(false);
        }

        private static T? ObserveDiagnosticValue<T>(Func<T?>? provider) where T : struct
        {
            try
            {
                return provider?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
