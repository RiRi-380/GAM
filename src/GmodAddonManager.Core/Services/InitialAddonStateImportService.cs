using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// 新規profileでのみ、既存のGMod無効状態を固定system Assetへ取り込む。
    /// Runtimeファイルの読書きは呼出側の責務で、このサービス自身は構成だけを変更する。
    /// </summary>
    public sealed class InitialAddonStateImportService
    {
        public const string ImportedAssetName =
            GmodDisabledAddonReconciliationService.SystemAssetName;

        private readonly GmodDisabledAddonReconciliationService reconciliationService =
            new GmodDisabledAddonReconciliationService();

        public InitialAddonStateImportResult Import(
            Configuration configuration,
            IEnumerable<string> subscribedIds,
            IEnumerable<string> disabledIds,
            DateTime completedAtUtc)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (configuration.InitialRuntimeImportCompleted)
            {
                return new InitialAddonStateImportResult
                {
                    Completed = true
                };
            }

            var subscribed = NormalizeIds(subscribedIds);
            var disabled = NormalizeIds(disabledIds);
            var importedIds = subscribed
                .Intersect(disabled, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var result = reconciliationService.ReconcileValidObservation(
                configuration,
                subscribed,
                disabled,
                NormalizeUtc(completedAtUtc),
                allowInitialSeed: true);
            configuration.SubscriptionBaselineInitialized = true;
            configuration.KnownSubscribedAddonIds = subscribed
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            configuration.SubscriptionFirstSeenAtUtc.Clear();

            return new InitialAddonStateImportResult
            {
                Completed = true,
                CreatedAsset = result.MembershipChanged,
                CreatedAssetId = result.MembershipChanged
                    ? GmodDisabledAddonReconciliationService.SystemAssetId
                    : null,
                ImportedAddonIds = result.MemberIds
            };
        }

        private static HashSet<string> NormalizeIds(IEnumerable<string>? ids)
        {
            return new HashSet<string>(
                (ids ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Where(id => ulong.TryParse(id, out _)),
                StringComparer.Ordinal);
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
