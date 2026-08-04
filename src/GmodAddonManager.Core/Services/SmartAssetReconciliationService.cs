using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Rebuilds materialized Smart Asset membership with in-memory set operations.
    /// It performs no file or network access and leaves AssetStateResolver unaware
    /// of rule mechanics.
    /// </summary>
    public sealed class SmartAssetReconciliationService
    {
        public SmartAssetReconciliationResult Reconcile(
            Configuration configuration,
            IEnumerable<string> authoritativeSubscribedAddonIds)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (authoritativeSubscribedAddonIds == null)
            {
                throw new ArgumentNullException(nameof(authoritativeSubscribedAddonIds));
            }

            configuration.Assets ??= new List<Asset>();
            configuration.AddonMetadata ??= new Dictionary<string, WorkshopAddon>();
            var subscribedIds = new HashSet<string>(
                authoritativeSubscribedAddonIds
                    .Where(IsWorkshopNumericId)
                    .Select(id => id.Trim()),
                StringComparer.Ordinal);
            var changes = new List<SmartAssetMembershipChange>();
            var configurationChanged = false;

            foreach (var asset in configuration.Assets.Where(asset => asset?.IsSmart == true))
            {
                asset.Addons ??= new List<string>();
                if (!AddonClassificationService.TryNormalizeRule(
                        asset.MembershipRule,
                        out var normalizedRule,
                        out var error))
                {
                    configurationChanged |= SetAutomationState(
                        asset,
                        SmartAssetAutomationStatus.FrozenInvalidRule,
                        error);
                    changes.Add(new SmartAssetMembershipChange(
                        asset.Id,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        isFrozen: true,
                        error));
                    continue;
                }

                if (!RulesEqual(asset.MembershipRule!, normalizedRule))
                {
                    asset.MembershipRule = normalizedRule;
                    configurationChanged = true;
                }

                configurationChanged |= SetAutomationState(
                    asset,
                    SmartAssetAutomationStatus.Active,
                    message: null);

                var existing = new HashSet<string>(
                    asset.Addons
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim()),
                    StringComparer.Ordinal);
                var desired = new HashSet<string>(existing, StringComparer.Ordinal);
                var added = new List<string>();
                var removed = new HashSet<string>(StringComparer.Ordinal);
                var unknown = new List<string>();

                foreach (var staleId in existing.Where(id => !subscribedIds.Contains(id)))
                {
                    desired.Remove(staleId);
                    removed.Add(staleId);
                }

                foreach (var addonId in subscribedIds)
                {
                    var match = configuration.AddonMetadata.TryGetValue(
                        addonId,
                        out var addon)
                        ? AddonClassificationService.Evaluate(addon, normalizedRule)
                        : AddonClassificationMatch.Unknown;

                    switch (match)
                    {
                        case AddonClassificationMatch.Match:
                            if (desired.Add(addonId))
                            {
                                added.Add(addonId);
                            }
                            break;

                        case AddonClassificationMatch.NoMatch:
                            if (desired.Remove(addonId))
                            {
                                removed.Add(addonId);
                            }
                            break;

                        default:
                            unknown.Add(addonId);
                            break;
                    }
                }

                var normalizedMembership = desired
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                if (!asset.Addons.SequenceEqual(normalizedMembership, StringComparer.Ordinal))
                {
                    asset.Addons = normalizedMembership;
                    configurationChanged = true;
                }

                asset.AddonStates?.Clear();
                changes.Add(new SmartAssetMembershipChange(
                    asset.Id,
                    added,
                    removed,
                    unknown,
                    isFrozen: false,
                    message: null));
            }

            return new SmartAssetReconciliationResult(
                isAuthoritative: true,
                changes,
                configurationChanged);
        }

        private static bool SetAutomationState(
            Asset asset,
            SmartAssetAutomationStatus status,
            string? message)
        {
            var existing = asset.SmartAutomationState;
            if (existing != null &&
                existing.SchemaVersion == SmartAssetAutomationState.CurrentSchemaVersion &&
                existing.Status == status &&
                string.Equals(existing.Message, message, StringComparison.Ordinal))
            {
                return false;
            }

            asset.SmartAutomationState = new SmartAssetAutomationState
            {
                SchemaVersion = SmartAssetAutomationState.CurrentSchemaVersion,
                Status = status,
                Message = message
            };
            return true;
        }

        private static bool RulesEqual(
            AssetMembershipRule left,
            AssetMembershipRule right)
        {
            return left.SchemaVersion == right.SchemaVersion &&
                   left.Kind == right.Kind &&
                   string.Equals(left.Value, right.Value, StringComparison.Ordinal);
        }

        private static bool IsWorkshopNumericId(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   ulong.TryParse(value.Trim(), out _);
        }
    }
}
