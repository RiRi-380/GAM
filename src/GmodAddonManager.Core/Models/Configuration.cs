using System;
using System.Collections.Generic;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class Configuration
    {
        public const int CurrentSchemaVersion = 7;
        public const int MinimumNestedGroupDepth = 1;
        public const int MaximumNestedGroupDepth = 10;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("assets")]
        public List<Asset> Assets { get; set; }

        [JsonProperty("assetGroups")]
        public List<AssetGroup> AssetGroups { get; set; }

        /// <summary>
        /// Maximum nesting below a root Asset Group. Root Groups have depth 0,
        /// their child Groups depth 1, and so on.
        /// </summary>
        [JsonProperty("maxNestedGroupDepth")]
        public int MaxNestedGroupDepth { get; set; }

        [JsonProperty("addonMetadata")]
        public Dictionary<string, WorkshopAddon> AddonMetadata { get; set; }

        [JsonProperty("junctionHistory")]
        public Dictionary<string, List<string>> JunctionHistory { get; set; }

        [JsonProperty("pathState")]
        public PathState PathState { get; set; }

        [JsonProperty("initialRuntimeImportCompleted")]
        public bool InitialRuntimeImportCompleted { get; set; }

        [JsonProperty("initialRuntimeImportCompletedAtUtc")]
        public DateTime? InitialRuntimeImportCompletedAtUtc { get; set; }

        [JsonProperty("subscriptionBaselineInitialized")]
        public bool SubscriptionBaselineInitialized { get; set; }

        [JsonProperty("knownSubscribedAddonIds")]
        public List<string> KnownSubscribedAddonIds { get; set; }

        [JsonProperty("subscriptionFirstSeenAtUtc")]
        public Dictionary<string, DateTime> SubscriptionFirstSeenAtUtc { get; set; }

        [JsonProperty("retainMissingAssetReferences")]
        public bool RetainMissingAssetReferences { get; set; }

        /// <summary>
        /// True once GAM has successfully persisted at least one runtime state.
        /// The dictionary is intentionally allowed to be partial because a legacy
        /// single-addon operation can predate the next full reconcile.
        /// </summary>
        [JsonProperty("gamAppliedRuntimeBaselineInitialized")]
        public bool GamAppliedRuntimeBaselineInitialized { get; set; }

        [JsonProperty("lastGamAppliedAddonStates")]
        public Dictionary<string, bool> LastGamAppliedAddonStates { get; set; }

        [JsonProperty("lastGamAppliedRuntimeAtUtc")]
        public DateTime? LastGamAppliedRuntimeAtUtc { get; set; }

        [JsonProperty("lastGamAppliedStateStorePath")]
        public string? LastGamAppliedStateStorePath { get; set; }

        /// <summary>
        /// The last valid GMod state acknowledged by GAM. External transitions are
        /// detected against this baseline; GAM writes advance it only after the
        /// runtime store confirms success.
        /// </summary>
        [JsonProperty("gmodObservationBaselineInitialized")]
        public bool GmodObservationBaselineInitialized { get; set; }

        [JsonProperty("lastObservedGmodAddonStates")]
        public Dictionary<string, bool> LastObservedGmodAddonStates { get; set; }

        [JsonProperty("lastObservedGmodRuntimeAtUtc")]
        public DateTime? LastObservedGmodRuntimeAtUtc { get; set; }

        [JsonProperty("lastObservedGmodStateStorePath")]
        public string? LastObservedGmodStateStorePath { get; set; }

        /// <summary>
        /// Durable cross-file intent written before addonnomount.txt. If GAM exits
        /// between the runtime write and config save, the next valid observation
        /// can attribute a matching state to GAM instead of importing it as an
        /// external disable.
        /// </summary>
        [JsonProperty("pendingGamRuntimeWrite")]
        public PendingGamRuntimeWrite? PendingGamRuntimeWrite { get; set; }

        /// <summary>
        /// Durable latch used when startup reconciliation needs a runtime apply
        /// before PendingChangeManager is available. The pending.json marker is
        /// created first when the provider arrives, then this latch is cleared.
        /// A crash can therefore cause a harmless duplicate apply, never a lost
        /// desired-state transition.
        /// </summary>
        [JsonProperty("pendingRuntimeApplyRequired")]
        public bool PendingRuntimeApplyRequired { get; set; }

        /// <summary>
        /// One valid post-upgrade observation must classify only runtime OFF
        /// states that the pre-attribution Asset model expected to be ON.
        /// </summary>
        [JsonProperty("gmodAttributionMigrationPending")]
        public bool GmodAttributionMigrationPending { get; set; }

        public Configuration()
        {
            SchemaVersion = CurrentSchemaVersion;
            Version = "2.0";
            LastUpdated = DateTime.UtcNow;
            Assets = new List<Asset>();
            AssetGroups = new List<AssetGroup>();
            MaxNestedGroupDepth = MinimumNestedGroupDepth;
            AddonMetadata = new Dictionary<string, WorkshopAddon>();
            JunctionHistory = new Dictionary<string, List<string>>();
            PathState = new PathState();
            InitialRuntimeImportCompleted = false;
            InitialRuntimeImportCompletedAtUtc = null;
            SubscriptionBaselineInitialized = false;
            KnownSubscribedAddonIds = new List<string>();
            SubscriptionFirstSeenAtUtc = new Dictionary<string, DateTime>();
            RetainMissingAssetReferences = false;
            GamAppliedRuntimeBaselineInitialized = false;
            LastGamAppliedAddonStates = new Dictionary<string, bool>();
            LastGamAppliedRuntimeAtUtc = null;
            LastGamAppliedStateStorePath = null;
            GmodObservationBaselineInitialized = false;
            LastObservedGmodAddonStates = new Dictionary<string, bool>();
            LastObservedGmodRuntimeAtUtc = null;
            LastObservedGmodStateStorePath = null;
            PendingGamRuntimeWrite = null;
            PendingRuntimeApplyRequired = false;
            GmodAttributionMigrationPending = false;
        }

        public void CreateDefaultAssets()
        {
            CreateDefaultAssets(includeJunction: false);
        }

        public void CreateDefaultAssets(bool includeJunction)
        {
            var subscribeAsset = new Asset(SystemAssetDefinitions.SubscribeName, true);
            subscribeAsset.Id = SystemAssetDefinitions.SubscribeId;
            subscribeAsset.SetWholeState(AddonState.Enabled);
            subscribeAsset.SetAllAddons();
            subscribeAsset.SortOrder = 0;
            Assets.Add(subscribeAsset);

            var gmodDisabledAsset = new Asset(SystemAssetDefinitions.GmodDisabledName, true);
            gmodDisabledAsset.Id = SystemAssetDefinitions.GmodDisabledId;
            gmodDisabledAsset.SetWholeState(SystemAssetDefinitions.GmodDisabledDefaultState);
            gmodDisabledAsset.SortOrder = 1;
            Assets.Add(gmodDisabledAsset);

            if (includeJunction)
            {
                var junctionAsset = new Asset(SystemAssetDefinitions.JunctionName, true);
                junctionAsset.Id = SystemAssetDefinitions.JunctionId;
                junctionAsset.SetWholeState(AddonState.Disabled);
                junctionAsset.SortOrder = 2;
                Assets.Add(junctionAsset);
            }
        }
    }

    public sealed class PendingGamRuntimeWrite
    {
        [JsonProperty("operationId")]
        public string OperationId { get; set; } = string.Empty;

        [JsonProperty("targetStates")]
        public Dictionary<string, bool> TargetStates { get; set; } =
            new Dictionary<string, bool>();

        [JsonProperty("previousStates")]
        public Dictionary<string, bool> PreviousStates { get; set; } =
            new Dictionary<string, bool>();

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonProperty("stateStorePath")]
        public string StateStorePath { get; set; } = string.Empty;

        /// <summary>
        /// Durable fail-closed latch. Once an unresolved GAM write and the
        /// observed runtime state diverge, automatic apply remains blocked until
        /// its pending apply marker has been durably removed.
        /// </summary>
        [JsonProperty("conflictDetected")]
        public bool ConflictDetected { get; set; }
    }

    public class PathState
    {
        [JsonProperty("lastKnownGoodSnapshot")]
        public PathSnapshot? LastKnownGoodSnapshot { get; set; }

        [JsonProperty("lastDetectedSnapshot")]
        public PathSnapshot? LastDetectedSnapshot { get; set; }

        [JsonProperty("previousDetectedSnapshot")]
        public PathSnapshot? PreviousDetectedSnapshot { get; set; }

        [JsonProperty("lastManagerPath")]
        public string? LastManagerPath { get; set; }

        [JsonProperty("lastAddonsPath")]
        public string? LastAddonsPath { get; set; }

        [JsonProperty("previousManagerPath")]
        public string? PreviousManagerPath { get; set; }

        [JsonProperty("previousAddonsPath")]
        public string? PreviousAddonsPath { get; set; }

        [JsonProperty("changes")]
        public List<PathChangeRecord> Changes { get; set; }

        public PathState()
        {
            Changes = new List<PathChangeRecord>();
        }
    }

    public class PathChangeRecord
    {
        [JsonProperty("detectedAt")]
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("pathKind")]
        public string PathKind { get; set; } = string.Empty;

        [JsonProperty("oldPath")]
        public string? OldPath { get; set; }

        [JsonProperty("newPath")]
        public string? NewPath { get; set; }
    }

    public class PendingChanges
    {
        [JsonProperty("changes")]
        public List<AddonChange> Changes { get; set; }

        public PendingChanges()
        {
            Changes = new List<AddonChange>();
        }
    }

    public class AddonChange
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("addonId")]
        public string AddonId { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        public AddonChange()
        {
            Action = string.Empty;
            AddonId = string.Empty;
            Timestamp = DateTime.UtcNow;
        }

        public AddonChange(string action, string addonId) : this()
        {
            Action = action;
            AddonId = addonId;
        }
    }
}
