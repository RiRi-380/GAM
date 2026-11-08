using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public partial class AddonManager
    {
        private const string GamImportImpactChangedFailureCode = "gam_import_impact_changed";

        public GamImportRuntimeImpact PreviewGamImportRuntimeImpact(GamAssetFileImportPreview preview)
        {
            if (preview == null)
            {
                throw new ArgumentNullException(nameof(preview));
            }

            var snapshot = GetWorkshopSnapshotFromCache();
            var incoming = preview.IsBundle
                ? preview.Content.Bundle!.Assets
                    .Select(asset => CreateImportedAsset(asset.Name, asset.State, asset.Membership, asset.Memo))
                    .ToList()
                : new List<Asset>
                {
                    CreateImportedAsset(
                        preview.SingleAssetPreview!.Document.Name,
                        preview.SingleAssetPreview.Document.State,
                        preview.SingleAssetPreview.Document.Membership,
                        preview.SingleAssetPreview.Document.Memo)
                };

            if (!incoming.Any(asset => asset.IsSmart))
            {
                return CompareGamImportRuntimeImpact(
                    configuration.Assets, configuration.Assets.Concat(incoming).ToList(), snapshot);
            }

            var prospective = CloneConfiguration(configuration);
            prospective.Assets.AddRange(incoming);
            ReconcileImportedSmartAssets(prospective, incoming, snapshot);
            return CompareGamImportRuntimeImpact(configuration.Assets, prospective.Assets, snapshot);
        }

        private void ReconcileImportedSmartAssets(
            Configuration target,
            IReadOnlyList<Asset> incoming,
            SteamWorkshopSnapshot snapshot)
        {
            if (snapshot.IsAuthoritative && incoming.Any(asset => asset.IsSmart))
            {
                RefreshClassificationMetadataForIds(snapshot.SubscribedIds, target);
                smartAssetReconciliationService.Reconcile(target, snapshot.SubscribedIds);
            }
        }

        private GamImportRuntimeImpact CompareGamImportRuntimeImpact(
            IReadOnlyList<Asset> before,
            IReadOnlyList<Asset> after,
            SteamWorkshopSnapshot snapshot)
        {
            if (!snapshot.IsAuthoritative)
            {
                return new GamImportRuntimeImpact(false, new Dictionary<string, bool>());
            }

            var subscribedIds = new HashSet<string>(snapshot.SubscribedIds, StringComparer.Ordinal);
            var previousDesired = BuildExpectedStatesForAssets(before, subscribedIds);
            var desired = BuildExpectedStatesForAssets(after, subscribedIds);
            if (desired.All(pair => previousDesired.TryGetValue(pair.Key, out var previous) &&
                    previous == pair.Value) &&
                desired.Count == previousDesired.Count)
            {
                return new GamImportRuntimeImpact(true, new Dictionary<string, bool>());
            }

            return ReadGamImportRuntimeImpact(desired);
        }

        private GamImportRuntimeImpact ReadGamImportRuntimeImpact(
            IReadOnlyDictionary<string, bool> desired)
        {
            var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                var actual = gmodAddonStateStore?.ReadSnapshot();
                if (actual?.IsValidFormat != true)
                {
                    return new GamImportRuntimeImpact(false, changes);
                }

                var disabled = new HashSet<string>(actual.DisabledIds, StringComparer.Ordinal);
                foreach (var pair in desired)
                {
                    if (IsWorkshopNumericId(pair.Key) && pair.Value == disabled.Contains(pair.Key))
                    {
                        changes.Add(pair.Key, pair.Value);
                    }
                }

                return new GamImportRuntimeImpact(true, changes);
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(ex.Message, "ReadGamImportRuntimeImpact");
                return new GamImportRuntimeImpact(false, changes);
            }
        }

        private static void VerifyGamImportApproval(
            GamImportRuntimeImpact? approval,
            GamImportRuntimeImpact current)
        {
            if (approval != null && !approval.Matches(current))
            {
                throw new GamImportRuntimeImpactChangedException();
            }
        }

        private bool IsApprovedGamImportImpactCurrent(GamImportRuntimeImpact approval)
        {
            return TryGetSubscribedAddonIdSet("ImportGamFile", out var subscribed) &&
                approval.Matches(ReadGamImportRuntimeImpact(
                    BuildExpectedStatesForAssets(configuration.Assets, subscribed)));
        }

        private async Task<bool> ApplyGamImportRuntimeImpactAsync(GamImportRuntimeImpact impact)
        {
            if (!impact.IsKnown || impact.Changes.Count == 0)
            {
                return true;
            }

            try
            {
                var result = await UpdateAddonStatesWithResultAsync(
                    allowConflictSupersede: true,
                    approvedImportImpact: impact);
                if (result.FailureCode == GamImportImpactChangedFailureCode)
                {
                    errorHandler.HandleWarning(
                        "The import was saved, but its runtime changes were skipped because the confirmed state changed.",
                        "ImportGamFile");
                }
                else if (!result.Succeeded && result.FailureCode != "gmod_running_deferred")
                {
                    errorHandler.HandleWarning(
                        "The import was saved, but its confirmed runtime changes could not be applied.",
                        "ImportGamFile");
                }

                return result.Succeeded || result.FailureCode == "gmod_running_deferred";
            }
            catch (Exception ex)
            {
                // No unguarded fallback: applying a newly computed state would
                // bypass the import confirmation.
                errorHandler.HandleError(
                    ex,
                    "The import was saved, but applying its runtime changes failed.",
                    ErrorSeverity.Warning);
                return false;
            }
        }
    }
}
