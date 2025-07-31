using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class Asset
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("isSystem")]
        public bool IsSystem { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("addons")]
        public List<string> Addons { get; set; }
        
        /// <summary>
        /// アドオンごとの状態を管理するディクショナリ
        /// キー: アドオンID、値: AddonState
        /// </summary>
        [JsonProperty("addonStates")]
        public Dictionary<string, AddonState> AddonStates { get; set; }
        
        /// <summary>
        /// アセット内の全アドオンのデフォルト状態
        /// </summary>
        [JsonProperty("defaultAddonState")]
        public AddonState DefaultAddonState { get; set; }
        
        /// <summary>
        /// WorkshopコレクションID（公開している場合）
        /// </summary>
        [JsonProperty("workshopCollectionId")]
        public string? WorkshopCollectionId { get; set; }
        
        /// <summary>
        /// コレクションの自動更新が有効か
        /// </summary>
        [JsonProperty("autoUpdateCollection")]
        public bool AutoUpdateCollection { get; set; }
        
        /// <summary>
        /// 現在のバージョン
        /// </summary>
        [JsonProperty("currentVersion")]
        public int CurrentVersion { get; set; }
        
        /// <summary>
        /// バージョン履歴
        /// </summary>
        [JsonProperty("versionHistory")]
        public List<AssetVersion> VersionHistory { get; set; }
        
        /// <summary>
        /// インポートベースラインバージョンを持つかどうか
        /// </summary>
        [JsonIgnore]
        public bool HasImportBaseline => VersionHistory?.Any(v => v.IsImportBaseline) ?? false;

        public Asset()
        {
            Id = Guid.NewGuid().ToString();
            Name = string.Empty;
            IsSystem = false;
            Enabled = true;
            Addons = new List<string>();
            AddonStates = new Dictionary<string, AddonState>();
            DefaultAddonState = AddonState.Enabled;
            WorkshopCollectionId = null;
            AutoUpdateCollection = true; // デフォルトで自動更新ON
            CurrentVersion = 0;
            VersionHistory = new List<AssetVersion>();
        }

        public Asset(string name, bool isSystem = false) : this()
        {
            Name = name;
            IsSystem = isSystem;
        }

        public bool ContainsAllAddons()
        {
            return Addons.Count == 1 && Addons[0] == "*";
        }

        public void SetAllAddons()
        {
            Addons.Clear();
            Addons.Add("*");
        }
        
        /// <summary>
        /// アドオンを追加し、デフォルトで有効状態に設定
        /// </summary>
        public void AddAddon(string addonId, AddonState state = AddonState.Enabled)
        {
            if (!Addons.Contains(addonId))
            {
                Addons.Add(addonId);
            }
            AddonStates[addonId] = state;
        }
        
        /// <summary>
        /// アドオンの状態を取得
        /// </summary>
        public AddonState GetAddonState(string addonId)
        {
            // 個別の状態が設定されている場合はそれを返す
            if (AddonStates.ContainsKey(addonId))
            {
                return AddonStates[addonId];
            }
            
            // 個別の状態がない場合は、DefaultAddonStateを返す
            return DefaultAddonState;
        }
        
        /// <summary>
        /// アドオンの状態を設定
        /// </summary>
        public void SetAddonState(string addonId, AddonState state)
        {
            // ContainsAllAddonsまたは個別に含まれている場合は状態を設定
            if (ContainsAllAddons() || Addons.Contains(addonId))
            {
                AddonStates[addonId] = state;
            }
        }
        
        /// <summary>
        /// アドオンを削除
        /// </summary>
        public void RemoveAddon(string addonId)
        {
            Addons.Remove(addonId);
            AddonStates.Remove(addonId);
        }
    }
}