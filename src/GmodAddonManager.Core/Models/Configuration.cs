using System;
using System.Collections.Generic;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class Configuration
    {
        public const int CurrentSchemaVersion = 2;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("assets")]
        public List<Asset> Assets { get; set; }

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

        public Configuration()
        {
            SchemaVersion = CurrentSchemaVersion;
            Version = "2.0";
            LastUpdated = DateTime.UtcNow;
            Assets = new List<Asset>();
            AddonMetadata = new Dictionary<string, WorkshopAddon>();
            JunctionHistory = new Dictionary<string, List<string>>();
            PathState = new PathState();
            InitialRuntimeImportCompleted = false;
            InitialRuntimeImportCompletedAtUtc = null;
            SubscriptionBaselineInitialized = false;
            KnownSubscribedAddonIds = new List<string>();
            SubscriptionFirstSeenAtUtc = new Dictionary<string, DateTime>();
            RetainMissingAssetReferences = false;
        }

        public void CreateDefaultAssets()
        {
            CreateDefaultAssets(includeJunction: false);
        }

        public void CreateDefaultAssets(bool includeJunction)
        {
            var subscribeAsset = new Asset("Subscribe Asset", true);
            subscribeAsset.Id = "subscribe-system-asset";
            subscribeAsset.SetWholeState(AddonState.Enabled);
            subscribeAsset.SetAllAddons();
            Assets.Add(subscribeAsset);

            if (includeJunction)
            {
                var junctionAsset = new Asset("Junction", true);
                junctionAsset.Id = "junction-system-asset";
                junctionAsset.SetWholeState(AddonState.Disabled);
                Assets.Add(junctionAsset);
            }
        }
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
