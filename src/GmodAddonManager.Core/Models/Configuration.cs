using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class Configuration
    {
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("assets")]
        public List<Asset> Assets { get; set; }

        [JsonProperty("addonMetadata")]
        public Dictionary<string, WorkshopAddon> AddonMetadata { get; set; }

        [JsonProperty("junctionHistory")]
        public Dictionary<string, List<string>> JunctionHistory { get; set; } // アドオンID -> 元のアセットID[]

        public Configuration()
        {
            Version = "1.0";
            LastUpdated = DateTime.UtcNow;
            Assets = new List<Asset>();
            AddonMetadata = new Dictionary<string, WorkshopAddon>();
            JunctionHistory = new Dictionary<string, List<string>>();
        }

        public void CreateDefaultAssets()
        {
            // サブスクライブアセット
            var subscribeAsset = new Asset("Subscribe", true);
            subscribeAsset.Id = "subscribe-system-asset"; // 固定ID
            subscribeAsset.SetAllAddons();
            Assets.Add(subscribeAsset);
            
            // ジャンクションアセット（無効化されたアドオンを表示）
            var junctionAsset = new Asset("Junction", true);
            junctionAsset.Id = "junction-system-asset"; // 固定ID
            junctionAsset.Enabled = false; // デフォルトで無効
            Assets.Add(junctionAsset);
        }
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