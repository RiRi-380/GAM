using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Services
{
    public sealed class UnsupportedConfigurationSchemaException : InvalidOperationException
    {
        public UnsupportedConfigurationSchemaException(int foundVersion, int supportedVersion)
            : base(
                $"Configuration schema {foundVersion} is newer than the supported schema " +
                $"{supportedVersion}. Upgrade GAM before opening this profile.")
        {
            FoundVersion = foundVersion;
            SupportedVersion = supportedVersion;
        }

        public int FoundVersion { get; }
        public int SupportedVersion { get; }
    }

    /// <summary>
    /// Legacy configurationを、Asset全体状態をtruth sourceとするschemaへ正規化する。
    /// このサービスは構成オブジェクトだけを変更し、runtimeファイルには触れない。
    /// </summary>
    public sealed class ConfigurationMigrationService
    {
        private const string SubscribeSystemAssetId = SystemAssetDefinitions.SubscribeId;
        private const string JunctionSystemAssetId = SystemAssetDefinitions.JunctionId;
        private const int GmodAttributionSchemaVersion = 3;
        private const int AssetGroupOrderSchemaVersion = 6;
        private readonly GmodDisabledAddonReconciliationService gmodDisabledService =
            new GmodDisabledAddonReconciliationService();

        public bool RequiresMigration(JObject rawConfiguration)
        {
            if (rawConfiguration == null)
            {
                throw new ArgumentNullException(nameof(rawConfiguration));
            }

            var schemaVersion = GetSchemaVersion(rawConfiguration);
            EnsureSupportedSchema(schemaVersion);
            return schemaVersion < Configuration.CurrentSchemaVersion;
        }

        public void EnsureSupportedSchema(JObject rawConfiguration)
        {
            if (rawConfiguration == null)
            {
                throw new ArgumentNullException(nameof(rawConfiguration));
            }

            EnsureSupportedSchema(GetSchemaVersion(rawConfiguration));
        }

        public ConfigurationMigrationResult Migrate(
            JObject rawConfiguration,
            Configuration configuration,
            bool removeLegacyJunctionAsset)
        {
            if (rawConfiguration == null)
            {
                throw new ArgumentNullException(nameof(rawConfiguration));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            EnsureSupportedSchema(rawConfiguration);

            var result = new ConfigurationMigrationResult();
            var sourceSchemaVersion = GetSchemaVersion(rawConfiguration);
            if (sourceSchemaVersion >= GmodAttributionSchemaVersion)
            {
                // Schemas 3-5 already own the subscription and GMod attribution
                // truth. Schema 6 adds Groups/order and schema 7 adds bounded
                // nested Groups/memos; reusing the legacy
                // migration would destructively reset those baselines.
                NormalizeCurrentSchema(
                    configuration,
                    removeLegacyJunctionAsset,
                    legacyVisibleOrder: sourceSchemaVersion < AssetGroupOrderSchemaVersion);
                result.Changed = true;
                return result;
            }

            var rawAssets = (rawConfiguration["assets"] as JArray)?
                .OfType<JObject>()
                .ToList() ?? new List<JObject>();

            configuration.Assets ??= new List<Asset>();
            configuration.AssetGroups ??= new List<AssetGroup>();
            configuration.AddonMetadata ??= new Dictionary<string, WorkshopAddon>();
            configuration.JunctionHistory ??= new Dictionary<string, List<string>>();
            configuration.PathState ??= new PathState();
            configuration.KnownSubscribedAddonIds ??= new List<string>();
            configuration.SubscriptionFirstSeenAtUtc ??= new Dictionary<string, DateTime>();
            configuration.LastGamAppliedAddonStates ??= new Dictionary<string, bool>();
            configuration.LastObservedGmodAddonStates ??= new Dictionary<string, bool>();

            // Eligibility must be evaluated before legacy fields are stripped.
            // Otherwise a user-edited same-name Asset could be normalized into
            // the shape of the old generated import and be absorbed by mistake.
            var gmodDisabledNormalization = gmodDisabledService.EnsureSystemAsset(
                configuration,
                absorbUntouchedLegacyImport: true);

            foreach (var asset in configuration.Assets.ToList())
            {
                NormalizeAssetCollections(asset);

                var rawAsset = FindRawAsset(rawAssets, asset);
                if (IsSubscribeAsset(asset, rawAsset))
                {
                    NormalizeSubscribeAsset(asset, rawAsset);
                }
                else if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(asset.Id))
                {
                    // The dedicated Asset is normalized after duplicate and legacy
                    // import handling below.
                }
                else if (IsJunctionAsset(asset, rawAsset) && removeLegacyJunctionAsset)
                {
                    configuration.Assets.Remove(asset);
                    result.RemovedLegacySystemAssetIds.Add(asset.Id);
                    continue;
                }
                else
                {
                    NormalizeCustomAsset(asset, rawAsset, result);
                    ClearSmartAssetFields(asset);
                }

                NormalizeVersions(asset);
                asset.WorkshopCollectionId = null;
                asset.AutoUpdateCollection = false;
                asset.AddonStates.Clear();
            }

            EnsureSubscribeAsset(configuration);
            gmodDisabledService.EnsureSystemAsset(
                configuration,
                absorbUntouchedLegacyImport: false);
            foreach (var systemAsset in configuration.Assets.Where(asset => asset.IsSystem))
            {
                ClearSmartAssetFields(systemAsset);
            }
            NormalizeClassificationMetadata(configuration.AddonMetadata.Values);
            new AssetGroupService().NormalizeConfiguration(
                configuration,
                legacyVisibleOrder: true);

            configuration.SchemaVersion = Configuration.CurrentSchemaVersion;
            configuration.Version = "2.0";
            configuration.InitialRuntimeImportCompleted = true;
            configuration.InitialRuntimeImportCompletedAtUtc ??= DateTime.UtcNow;
            configuration.SubscriptionBaselineInitialized = false;
            configuration.KnownSubscribedAddonIds.Clear();
            configuration.SubscriptionFirstSeenAtUtc.Clear();
            configuration.GamAppliedRuntimeBaselineInitialized = false;
            configuration.LastGamAppliedAddonStates.Clear();
            configuration.LastGamAppliedRuntimeAtUtc = null;
            configuration.LastGamAppliedStateStorePath = null;
            configuration.GmodObservationBaselineInitialized = false;
            configuration.LastObservedGmodAddonStates.Clear();
            configuration.LastObservedGmodRuntimeAtUtc = null;
            configuration.LastObservedGmodStateStorePath = null;
            configuration.PendingGamRuntimeWrite = null;
            configuration.GmodAttributionMigrationPending = true;
            if (gmodDisabledNormalization.AbsorbedLegacyImport)
            {
                result.Changed = true;
            }
            result.Changed = true;
            return result;
        }

        private static int GetSchemaVersion(JObject rawConfiguration)
        {
            return rawConfiguration.Value<int?>("schemaVersion") ?? 0;
        }

        private static void EnsureSupportedSchema(int schemaVersion)
        {
            if (schemaVersion > Configuration.CurrentSchemaVersion)
            {
                throw new UnsupportedConfigurationSchemaException(
                    schemaVersion,
                    Configuration.CurrentSchemaVersion);
            }
        }

        public void NormalizeCurrentSchema(
            Configuration configuration,
            bool removeLegacyJunctionAsset = true,
            bool legacyVisibleOrder = false)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            configuration.Assets ??= new List<Asset>();
            configuration.AssetGroups ??= new List<AssetGroup>();
            configuration.AddonMetadata ??= new Dictionary<string, WorkshopAddon>();
            configuration.JunctionHistory ??= new Dictionary<string, List<string>>();
            configuration.PathState ??= new PathState();
            configuration.KnownSubscribedAddonIds ??= new List<string>();
            configuration.SubscriptionFirstSeenAtUtc ??= new Dictionary<string, DateTime>();
            configuration.LastGamAppliedAddonStates ??= new Dictionary<string, bool>();
            configuration.LastObservedGmodAddonStates ??= new Dictionary<string, bool>();

            NormalizeClassificationMetadata(configuration.AddonMetadata.Values);

            NormalizeAttributionState(configuration);

            var subscribeSeen = false;
            foreach (var asset in configuration.Assets.ToList())
            {
                NormalizeAssetCollections(asset);
                if (IsSubscribeAsset(asset, rawAsset: null))
                {
                    if (subscribeSeen)
                    {
                        configuration.Assets.Remove(asset);
                        continue;
                    }

                    NormalizeSubscribeAsset(asset, rawAsset: null);
                    ClearSmartAssetFields(asset);
                    subscribeSeen = true;
                }
                else if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(asset.Id))
                {
                    // Normalized after duplicate handling and legacy conversion.
                    ClearSmartAssetFields(asset);
                }
                else
                {
                    if (removeLegacyJunctionAsset && IsJunctionAsset(asset, rawAsset: null))
                    {
                        configuration.Assets.Remove(asset);
                        continue;
                    }

                    asset.IsSystem = false;
                    if (!Enum.IsDefined(typeof(AddonState), asset.State))
                    {
                        asset.SetWholeState(AddonState.Disabled);
                    }

                    NormalizeSmartAsset(asset);
                }

                asset.Addons = NormalizeIds(asset.Addons, allowWildcard: asset.Id == SubscribeSystemAssetId);
                asset.AddonStates.Clear();
                asset.WorkshopCollectionId = null;
                asset.AutoUpdateCollection = false;
                if (asset.IsSmart)
                {
                    asset.VersionHistory.Clear();
                    asset.CurrentVersion = 0;
                }
                else
                {
                    NormalizeVersions(asset);
                }
            }

            EnsureSubscribeAsset(configuration);
            gmodDisabledService.EnsureSystemAsset(
                configuration,
                absorbUntouchedLegacyImport: false);
            foreach (var systemAsset in configuration.Assets.Where(asset => asset.IsSystem))
            {
                ClearSmartAssetFields(systemAsset);
            }
            new AssetGroupService().NormalizeConfiguration(
                configuration,
                legacyVisibleOrder);
            configuration.SchemaVersion = Configuration.CurrentSchemaVersion;
            configuration.Version = "2.0";
        }

        private static void NormalizeSubscribeAsset(Asset asset, JObject? rawAsset)
        {
            AddonState normalizedState;
            if (rawAsset != null)
            {
                // Schemas before the state-model migration only had an ON/OFF
                // Subscribe contract. Do not promote legacy compatibility fields
                // to the new all-excluded state.
                var rawEnabled = rawAsset.Value<bool?>("enabled");
                var legacyState = asset.GetWholeState();
                var wasEnabled = rawEnabled ??
                    (Enum.IsDefined(typeof(AddonState), legacyState) &&
                     legacyState != AddonState.Disabled);
                normalizedState = wasEnabled
                    ? AddonState.Enabled
                    : AddonState.Disabled;
            }
            else
            {
                // Current-schema normalization must preserve the explicit
                // Subscribe Excluded state across every startup.
                normalizedState = Enum.IsDefined(typeof(AddonState), asset.GetWholeState())
                    ? asset.GetWholeState()
                    : AddonState.Excluded;
            }

            asset.Id = SubscribeSystemAssetId;
            asset.Name = "Subscribe Asset";
            asset.IsSystem = true;
            asset.IsFavorite = false;
            asset.NeedsMigrationReview = false;
            ClearSmartAssetFields(asset);
            asset.SetWholeState(normalizedState);
            asset.SetAllAddons();
        }

        private static void NormalizeCustomAsset(
            Asset asset,
            JObject? rawAsset,
            ConfigurationMigrationResult result)
        {
            asset.IsSystem = false;
            asset.Addons = NormalizeIds(asset.Addons, allowWildcard: false);

            var legacyEnabled = rawAsset?.Value<bool?>("enabled") ?? asset.Enabled;
            var migratedState = AddonState.Disabled;
            var needsReview = false;

            if (legacyEnabled)
            {
                var fallbackState = ParseAddonState(rawAsset?["defaultAddonState"], asset.DefaultAddonState);
                var rawStates = rawAsset?["addonStates"] as JObject;
                var memberStates = asset.Addons
                    .Select(id => ParseAddonState(rawStates?[id], fallbackState))
                    .Distinct()
                    .ToList();

                if (memberStates.Count == 0)
                {
                    migratedState = fallbackState;
                }
                else if (memberStates.Count == 1)
                {
                    migratedState = memberStates[0];
                }
                else
                {
                    migratedState = AddonState.Disabled;
                    needsReview = true;
                }
            }

            asset.SetWholeState(migratedState);
            asset.NeedsMigrationReview = needsReview;
            if (needsReview)
            {
                result.NeedsReviewAssetIds.Add(asset.Id);
            }
        }

        private static void NormalizeVersions(Asset asset)
        {
            asset.VersionHistory ??= new List<AssetVersion>();

            foreach (var version in asset.VersionHistory)
            {
                version.AddonIds = NormalizeIds(version.AddonIds, allowWildcard: false);
                version.GamContent = null;
                version.IncludeAddonStates = false;
                version.AddonStates = null;
                version.IsImportBaseline = false;
                version.NewlySubscribedAddonIds = null;
                version.ImportType = null;
            }

            asset.VersionHistory = asset.VersionHistory
                .Where(version => version.Version >= 0)
                .OrderBy(version => version.Version)
                .ThenBy(version => version.CreatedAt)
                .ToList();

            if (asset.CurrentVersion < 0 ||
                (asset.CurrentVersion > 0 &&
                 asset.VersionHistory.All(
                     version => version.Version != asset.CurrentVersion)))
            {
                asset.CurrentVersion = 0;
            }
        }

        private static void NormalizeSmartAsset(Asset asset)
        {
            if (asset.MembershipRule == null)
            {
                asset.SmartAutomationState = null;
                return;
            }

            // Rule-driven membership represents only the current authoritative
            // Workshop inventory, never an imported missing-reference snapshot.
            asset.RetainMissingReferences = false;
            if (AddonClassificationService.TryNormalizeRule(
                    asset.MembershipRule,
                    out var normalizedRule,
                    out var error))
            {
                asset.MembershipRule = normalizedRule;
                asset.SmartAutomationState = new SmartAssetAutomationState
                {
                    SchemaVersion = SmartAssetAutomationState.CurrentSchemaVersion,
                    Status = SmartAssetAutomationStatus.Active,
                    Message = null
                };
                return;
            }

            // Preserve malformed/future rules and their materialized members.
            // Freezing is fail-safe: startup never silently turns the Asset into
            // a fixed list or removes members it cannot classify.
            asset.SmartAutomationState = new SmartAssetAutomationState
            {
                SchemaVersion = SmartAssetAutomationState.CurrentSchemaVersion,
                Status = SmartAssetAutomationStatus.FrozenInvalidRule,
                Message = error
            };
        }

        private static void ClearSmartAssetFields(Asset asset)
        {
            asset.MembershipRule = null;
            asset.SmartAutomationState = null;
            asset.RetainMissingReferences = false;
        }

        private static void NormalizeClassificationMetadata(
            IEnumerable<WorkshopAddon> addons)
        {
            foreach (var addon in addons ?? Enumerable.Empty<WorkshopAddon>())
            {
                addon.Type ??= string.Empty;
                addon.Tags ??= Array.Empty<string>();
                addon.Tags = addon.Tags
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (!Enum.IsDefined(
                        typeof(AddonClassificationMetadataStatus),
                        addon.TypeMetadataStatus))
                {
                    addon.TypeMetadataStatus = AddonClassificationMetadataStatus.Unknown;
                }
                if (!Enum.IsDefined(
                        typeof(AddonClassificationMetadataStatus),
                        addon.TagsMetadataStatus))
                {
                    addon.TagsMetadataStatus = AddonClassificationMetadataStatus.Unknown;
                }

                if (!string.IsNullOrWhiteSpace(addon.Type))
                {
                    addon.Type = addon.Type.Trim();
                    addon.TypeMetadataStatus = AddonClassificationMetadataStatus.Known;
                }
                if (addon.Tags.Length > 0)
                {
                    addon.TagsMetadataStatus = AddonClassificationMetadataStatus.Known;
                }
            }
        }

        private static void EnsureSubscribeAsset(Configuration configuration)
        {
            var subscribe = configuration.Assets.FirstOrDefault(asset => asset.Id == SubscribeSystemAssetId);
            if (subscribe == null)
            {
                subscribe = new Asset("Subscribe Asset", true)
                {
                    Id = SubscribeSystemAssetId
                };
                subscribe.SetAllAddons();
                subscribe.SetWholeState(AddonState.Enabled);
            }

            configuration.Assets.RemoveAll(asset =>
                asset.Id == SubscribeSystemAssetId &&
                !ReferenceEquals(asset, subscribe));
            configuration.Assets.Remove(subscribe);
            configuration.Assets.Insert(0, subscribe);
        }

        private static void NormalizeAttributionState(Configuration configuration)
        {
            configuration.LastGamAppliedAddonStates = NormalizeStateMap(
                configuration.LastGamAppliedAddonStates);
            configuration.LastObservedGmodAddonStates = NormalizeStateMap(
                configuration.LastObservedGmodAddonStates);

            if (!configuration.GamAppliedRuntimeBaselineInitialized)
            {
                configuration.LastGamAppliedAddonStates.Clear();
                configuration.LastGamAppliedRuntimeAtUtc = null;
                configuration.LastGamAppliedStateStorePath = null;
            }
            if (!configuration.GmodObservationBaselineInitialized)
            {
                configuration.LastObservedGmodAddonStates.Clear();
                configuration.LastObservedGmodRuntimeAtUtc = null;
                configuration.LastObservedGmodStateStorePath = null;
            }

            if (configuration.PendingGamRuntimeWrite != null)
            {
                configuration.PendingGamRuntimeWrite.OperationId ??= string.Empty;
                configuration.PendingGamRuntimeWrite.TargetStates = NormalizeStateMap(
                    configuration.PendingGamRuntimeWrite.TargetStates);
                configuration.PendingGamRuntimeWrite.PreviousStates = NormalizeStateMap(
                    configuration.PendingGamRuntimeWrite.PreviousStates);
                if (configuration.PendingGamRuntimeWrite.TargetStates.Count == 0)
                {
                    configuration.PendingGamRuntimeWrite = null;
                }
            }
        }

        private static Dictionary<string, bool> NormalizeStateMap(
            IEnumerable<KeyValuePair<string, bool>>? states)
        {
            return (states ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Key) &&
                    ulong.TryParse(entry.Key.Trim(), out _))
                .GroupBy(entry => entry.Key.Trim(), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Value,
                    StringComparer.Ordinal);
        }

        private static void NormalizeAssetCollections(Asset asset)
        {
            asset.Id = string.IsNullOrWhiteSpace(asset.Id) ? Guid.NewGuid().ToString() : asset.Id.Trim();
            asset.Name ??= string.Empty;
            asset.Addons ??= new List<string>();
            asset.AddonStates ??= new Dictionary<string, AddonState>();
            asset.VersionHistory ??= new List<AssetVersion>();
            asset.SmartAutomationState ??= asset.MembershipRule == null
                ? null
                : new SmartAssetAutomationState();
        }

        private static List<string> NormalizeIds(IEnumerable<string>? ids, bool allowWildcard)
        {
            return (ids ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Where(id => (allowWildcard && id == "*") || ulong.TryParse(id, out _))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id == "*" ? string.Empty : id, StringComparer.Ordinal)
                .ToList();
        }

        private static JObject? FindRawAsset(IEnumerable<JObject> rawAssets, Asset asset)
        {
            return rawAssets.FirstOrDefault(raw =>
                       string.Equals(raw.Value<string>("id"), asset.Id, StringComparison.Ordinal))
                   ?? rawAssets.FirstOrDefault(raw =>
                       string.Equals(raw.Value<string>("name"), asset.Name, StringComparison.Ordinal));
        }

        private static bool IsSubscribeAsset(Asset asset, JObject? rawAsset)
        {
            var id = rawAsset?.Value<string>("id") ?? asset.Id;
            var name = rawAsset?.Value<string>("name") ?? asset.Name;
            return string.Equals(id, SubscribeSystemAssetId, StringComparison.Ordinal) ||
                   (asset.IsSystem &&
                    (string.Equals(name, "Subscribe Asset", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "Subscribe", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "サブスクライブ", StringComparison.Ordinal) ||
                     string.Equals(name, "サブスクライブアセット", StringComparison.Ordinal)));
        }

        private static bool IsJunctionAsset(Asset asset, JObject? rawAsset)
        {
            var id = rawAsset?.Value<string>("id") ?? asset.Id;
            var name = rawAsset?.Value<string>("name") ?? asset.Name;
            return string.Equals(id, JunctionSystemAssetId, StringComparison.Ordinal) ||
                   (asset.IsSystem &&
                    (string.Equals(name, "Junction", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "ジャンクション", StringComparison.Ordinal)));
        }

        private static AddonState ParseAddonState(JToken? token, AddonState fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.Integer &&
                Enum.IsDefined(typeof(AddonState), token.Value<int>()))
            {
                return (AddonState)token.Value<int>();
            }

            var text = token.Value<string>();
            if (Enum.TryParse(text, ignoreCase: true, out AddonState parsed))
            {
                return parsed;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
                Enum.IsDefined(typeof(AddonState), numeric))
            {
                return (AddonState)numeric;
            }

            return fallback;
        }
    }
}
